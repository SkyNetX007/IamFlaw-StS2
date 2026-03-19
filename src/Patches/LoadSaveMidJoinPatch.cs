using HarmonyLib;
using IamFlaw.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using System.Reflection;

namespace IamFlaw.Patches;

[HarmonyPatch]
public class LoadSaveMidJoinPatch
{
    private static readonly string LogTag = $"{ModEntry.ModId}.LoadSaveMidJoinPatch";

    private const int DefaultMaxPlayers = 4;
    private const int DefaultTimeoutSeconds = 30;

    private static readonly FieldInfo ConnectingPlayersField = AccessTools.Field(typeof(LoadRunLobby), "_connectingPlayers");

    public static LoadRunLobby? CurrentLobby { get; private set; }

    [HarmonyPatch(typeof(LoadRunLobby), MethodType.Constructor, typeof(INetGameService), typeof(ILoadRunLobbyListener), typeof(SerializableRun))]
    [HarmonyPrefix]
    public static void OnLoadRunLobbyCreated(LoadRunLobby __instance)
    {
        CurrentLobby = __instance;
    }

    [HarmonyPatch(typeof(LoadRunLobby), "OnConnectedToClientAsHost")]
    [HarmonyPrefix]
    public static bool OnConnectedToClientAsHost_Pre(LoadRunLobby __instance, ulong playerId)
    {
        try
        {
            var state = MidJoinState.Instance;

            if (!IsMidJoinAllowed(state, __instance.Run, playerId, out string reason))
            {
                if (reason == "not_enabled")
                    return true;
                return false;
            }

            if (state.GetPendingPlayer(playerId) != null)
            {
                return true;
            }

            var defaultCopyIndex = GetValidCopyIndex(state, __instance.Run);
            var sourcePlayer = __instance.Run.Players[defaultCopyIndex];

            var placeholderPlayer = new SerializablePlayer
            {
                NetId = playerId,
                CharacterId = sourcePlayer.CharacterId,
                CurrentHp = sourcePlayer.CurrentHp,
                MaxHp = sourcePlayer.MaxHp,
                MaxEnergy = sourcePlayer.MaxEnergy,
                MaxPotionSlotCount = sourcePlayer.MaxPotionSlotCount,
                Gold = sourcePlayer.Gold,
                BaseOrbSlotCount = sourcePlayer.BaseOrbSlotCount,
                Deck = new List<SerializableCard>(),
                Relics = new List<SerializableRelic>(),
                Potions = new List<SerializablePotion>(),
                Rng = sourcePlayer.Rng ?? new SerializablePlayerRngSet(),
                Odds = sourcePlayer.Odds ?? new SerializablePlayerOddsSet(),
                RelicGrabBag = sourcePlayer.RelicGrabBag ?? new SerializableRelicGrabBag(),
                ExtraFields = sourcePlayer.ExtraFields ?? new SerializableExtraPlayerFields(),
                UnlockState = sourcePlayer.UnlockState ?? new SerializableUnlockState(),
                DiscoveredCards = new List<ModelId>(),
                DiscoveredEnemies = new List<ModelId>(),
                DiscoveredEpochs = new List<string>(),
                DiscoveredPotions = new List<ModelId>(),
                DiscoveredRelics = new List<ModelId>()
            };
            __instance.Run.Players.Add(placeholderPlayer);

            state.AddPendingPlayer(playerId, defaultCopyIndex);

            Log.Info($"{LogTag}: Player {playerId} added to pending at connect (placeholder from player {defaultCopyIndex}). Waiting for approval.");
            Log.Info($"{LogTag}: Use 'midjoin approve <netId> [index]' to approve, or 'midjoin reject <netId>' to reject");

            StartJoinTimeout(__instance, playerId, state, null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"{LogTag}: Error in OnConnectedToClientAsHost_Pre: {ex.Message}\n{ex.StackTrace}");
            return true;
        }
    }

    [HarmonyPatch(typeof(LoadRunLobby), "OnConnectedToClientAsHost")]
    [HarmonyPostfix]
    public static void OnConnectedToClientAsHost_Post(LoadRunLobby __instance, ulong playerId)
    {
        try
        {
            var state = MidJoinState.Instance;

            if (!state.Config.Enabled)
                return;

            var pending = state.GetPendingPlayer(playerId);
            if (pending == null)
                return;

            if (__instance.Run.Players.Any(p => p.NetId == playerId))
                return;

            Log.Info($"{LogTag}: Re-sending join response to pending player {playerId}");

            var message = InitialGameInfoMessage.Basic();
            message.sessionState = RunSessionState.InLoadedLobby;
            message.gameMode = __instance.GameMode;
            __instance.NetService.SendMessage(message, playerId);

            AddToConnectingPlayers(__instance, playerId);
        }
        catch (Exception ex)
        {
            Log.Error($"{LogTag}: Error in OnConnectedToClientAsHost_Post: {ex.Message}\n{ex.StackTrace}");
        }
    }

    [HarmonyPatch(typeof(LoadRunLobby), "HandleClientLoadJoinRequestMessage")]
    [HarmonyPrefix]
    public static bool OnHandleClientLoadJoinRequestMessage(LoadRunLobby __instance, ClientLoadJoinRequestMessage message, ulong senderId)
    {
        try
        {
            var state = MidJoinState.Instance;
            var netService = __instance.NetService;

            if (netService.Type != NetGameType.Host)
                return true;

            var pending = state.GetPendingPlayer(senderId);
            if (pending != null && __instance.Run.Players.Any(p => p.NetId == senderId))
            {
                if (pending.IsApproved)
                {
                    Log.Info($"{LogTag}: Player {senderId} already approved, allowing normal join");
                    return true;
                }
                Log.Info($"{LogTag}: Player {senderId} is pending approval, waiting for host action");
                return false;
            }

            if (!IsMidJoinAllowed(state, __instance.Run, senderId, out string reason))
            {
                if (reason == "not_enabled")
                {
                    Log.Info($"{LogTag}: Mid-join disabled, rejecting player {senderId}");
                    return true;
                }
                if (reason == "max_players")
                {
                    Log.Info($"{LogTag}: Player {senderId} rejected - max players reached");
                    ((INetHostGameService)netService).DisconnectClient(senderId, NetError.InvalidJoin, false);
                    return false;
                }
                return true;
            }

            var defaultCopyIndex = GetValidCopyIndex(state, __instance.Run);
            state.AddPendingPlayer(senderId, defaultCopyIndex);

            Log.Info($"{LogTag}: Player {senderId} waiting for approval. Default copy index: {defaultCopyIndex}");
            Log.Info($"{LogTag}: Use 'midjoin approve <netId>' to approve, or 'midjoin reject <netId>' to reject");

            StartJoinTimeout(__instance, senderId, state, netService);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"{LogTag}: Error in OnHandleClientLoadJoinRequestMessage: {ex.Message}\n{ex.StackTrace}");
            return true;
        }
    }

    public static void ApproveAndCompleteJoin(LoadRunLobby lobby, ulong playerId, int copySourceIndex)
    {
        var state = MidJoinState.Instance;
        var netService = lobby.NetService;
        var run = lobby.Run;

        if (copySourceIndex < 0 || copySourceIndex >= run.Players.Count)
        {
            copySourceIndex = 0;
        }

        var sourcePlayer = run.Players[copySourceIndex];
        var newPlayer = CreateNewPlayerFromSource(sourcePlayer, playerId);

        var placeholderIndex = -1;
        for (int i = 0; i < run.Players.Count; i++)
        {
            if (run.Players[i].NetId == playerId)
            {
                placeholderIndex = i;
                break;
            }
        }

        if (placeholderIndex >= 0)
        {
            run.Players[placeholderIndex] = newPlayer;
            Log.Info($"{LogTag}: Replaced placeholder for player {playerId}, copied from player {copySourceIndex} ({sourcePlayer.CharacterId.Entry}). Total: {run.Players.Count}");
        }
        else
        {
            run.Players.Add(newPlayer);
            Log.Info($"{LogTag}: Added player {playerId}, copied from player {copySourceIndex} ({sourcePlayer.CharacterId.Entry}). Total: {run.Players.Count}");
        }

        state.ApprovePlayer(playerId, copySourceIndex);
        state.CancelTimeout();

        lobby.ConnectedPlayerIds.Add(playerId);

        var hostService = (INetHostGameService)netService;
        var connectedPlayerIds = new HashSet<ulong>(lobby.ConnectedPlayerIds);
        connectedPlayerIds.Add(playerId);

        var response = new ClientLoadJoinResponseMessage
        {
            serializableRun = run,
            playersAlreadyConnected = connectedPlayerIds.ToList()
        };
        hostService.SendMessage(response, playerId);
        hostService.SetPeerReadyForBroadcasting(playerId);

        var reconnectMsg = new PlayerReconnectedMessage { playerId = playerId };
        foreach (var existingId in connectedPlayerIds)
        {
            if (existingId != playerId && existingId != netService.NetId)
            {
                hostService.SendMessage(reconnectMsg, existingId);
            }
        }

        lobby.LobbyListener.PlayerConnected(playerId);

        try
        {
            var connectingPlayersObj = ConnectingPlayersField.GetValue(lobby);
            if (connectingPlayersObj == null)
            {
                Log.Warn($"{LogTag}: _connectingPlayers is null");
            }
            else
            {
                var listType = connectingPlayersObj.GetType();
                if (listType.IsGenericType && listType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var elementType = listType.GetGenericArguments()[0];
                    if (elementType.FullName?.Contains("ConnectingPlayer") == true)
                    {
                        var countProperty = listType.GetProperty("Count");
                        var getItemMethod = listType.GetMethod("get_Item");
                        var removeAtMethod = listType.GetMethod("RemoveAt");
                        var idField = elementType.GetField("id");
                        var tokenField = elementType.GetField("timeoutCancelToken");

                        if (countProperty != null && getItemMethod != null && idField != null && tokenField != null && removeAtMethod != null)
                        {
                            int count = (int)countProperty.GetValue(connectingPlayersObj)!;
                            for (int i = count - 1; i >= 0; i--)
                            {
                                var item = getItemMethod.Invoke(connectingPlayersObj, new object[] { i });
                                var idValue = idField.GetValue(item);
                                if (idValue != null && (ulong)idValue == playerId)
                                {
                                    var tokenSource = tokenField.GetValue(item) as CancellationTokenSource;
                                    tokenSource?.Cancel();
                                    removeAtMethod.Invoke(connectingPlayersObj, new object[] { i });
                                    Log.Info($"{LogTag}: Cancelled and removed connecting player {playerId}");
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{LogTag}: Failed to remove connecting player: {ex.Message}\n{ex.StackTrace}");
        }

        Log.Info($"{LogTag}: Player {playerId} successfully joined");
    }

    public static void RejectPlayer(LoadRunLobby lobby, ulong playerId)
    {
        var state = MidJoinState.Instance;
        var netService = lobby.NetService;
        var run = lobby.Run;

        for (int i = run.Players.Count - 1; i >= 0; i--)
        {
            if (run.Players[i].NetId == playerId)
            {
                run.Players.RemoveAt(i);
                Log.Info($"{LogTag}: Removed placeholder for player {playerId}");
                break;
            }
        }

        state.RejectPlayer(playerId);
        state.CancelTimeout();

        try
        {
            var hostService = (INetHostGameService)netService;
            hostService.DisconnectClient(playerId, NetError.NotInSaveGame, false);
            Log.Info($"{LogTag}: Player {playerId} rejected by host");
        }
        catch (Exception ex)
        {
            Log.Error($"{LogTag}: Failed to reject player {playerId}: {ex.Message}");
        }
    }

    private static bool IsMidJoinAllowed(MidJoinState state, SerializableRun run, ulong playerId, out string? reason)
    {
        if (!state.Config.Enabled)
        {
            reason = "not_enabled";
            return false;
        }

        if (run.Players.Any(p => p.NetId == playerId))
        {
            reason = "already_in_save";
            return false;
        }

        var maxPlayers = state.Config.MaxPlayers > 0 ? state.Config.MaxPlayers : DefaultMaxPlayers;
        if (run.Players.Count >= maxPlayers)
        {
            reason = "max_players";
            return false;
        }

        reason = null;
        return true;
    }

    private static int GetValidCopyIndex(MidJoinState state, SerializableRun run)
    {
        var index = state.Config.DefaultCopySourceIndex;
        if (index < 0 || index >= run.Players.Count)
            index = 0;
        return index;
    }

    private static void StartJoinTimeout(LoadRunLobby lobby, ulong playerId, MidJoinState state, INetGameService? netService = null)
    {
        state.StartTimeoutTimer(() =>
        {
            var pending = state.GetPendingPlayer(playerId);
            if (pending != null && !pending.IsApproved)
            {
                Log.Info($"{LogTag}: Player {playerId} join request timed out, rejecting");

                for (int i = lobby.Run.Players.Count - 1; i >= 0; i--)
                {
                    if (lobby.Run.Players[i].NetId == playerId)
                    {
                        lobby.Run.Players.RemoveAt(i);
                        Log.Info($"{LogTag}: Removed timeout placeholder for player {playerId}");
                        break;
                    }
                }

                try
                {
                    var host = (INetHostGameService)(netService ?? lobby.NetService);
                    host.DisconnectClient(playerId, NetError.NotInSaveGame, false);
                }
                catch { }
                state.RemovePendingPlayer(playerId);
            }
        });
    }

    private static void AddToConnectingPlayers(LoadRunLobby lobby, ulong playerId)
    {
        var connectingPlayersObj = ConnectingPlayersField.GetValue(lobby);
        if (connectingPlayersObj == null)
        {
            Log.Warn($"{LogTag}: ConnectingPlayers field is null");
            return;
        }

        var listType = connectingPlayersObj.GetType();
        if (!listType.IsGenericType || listType.GetGenericTypeDefinition() != typeof(List<>))
        {
            Log.Warn($"{LogTag}: ConnectingPlayers is not a List: {listType}");
            return;
        }

        var elementType = listType.GetGenericArguments()[0];
        if (!elementType.FullName?.Contains("ConnectingPlayer") == true)
        {
            Log.Warn($"{LogTag}: ConnectingPlayers element type is not ConnectingPlayer: {elementType}");
            return;
        }

        var countProperty = listType.GetProperty("Count")!;
        var getItemMethod = listType.GetMethod("get_Item")!;
        var addMethod = listType.GetMethod("Add")!;
        var idProperty = elementType.GetProperty("id")!;

        int count = (int)countProperty.GetValue(connectingPlayersObj)!;
        bool alreadyConnecting = false;
        for (int i = 0; i < count; i++)
        {
            var item = getItemMethod.Invoke(connectingPlayersObj, new object[] { i });
            if ((ulong)idProperty.GetValue(item)! == playerId)
            {
                alreadyConnecting = true;
                break;
            }
        }

        if (!alreadyConnecting)
        {
            var connectingPlayerType = AccessTools.Inner(typeof(LoadRunLobby), "ConnectingPlayer")!;
            var connectingPlayer = Activator.CreateInstance(connectingPlayerType, playerId, new CancellationTokenSource());

            addMethod.Invoke(connectingPlayersObj, new[] { connectingPlayer });
            Log.Info($"{LogTag}: Player {playerId} added to connecting players");
        }
    }

    private static SerializablePlayer CreateNewPlayerFromSource(SerializablePlayer source, ulong newNetId)
    {
        return new SerializablePlayer
        {
            NetId = newNetId,
            CharacterId = source.CharacterId,
            CurrentHp = source.CurrentHp,
            MaxHp = source.MaxHp,
            MaxEnergy = source.MaxEnergy,
            MaxPotionSlotCount = source.MaxPotionSlotCount,
            Gold = source.Gold,
            BaseOrbSlotCount = source.BaseOrbSlotCount,
            Deck = new List<SerializableCard>(source.Deck),
            Relics = new List<SerializableRelic>(source.Relics),
            Potions = new List<SerializablePotion>(source.Potions),
            Rng = source.Rng,
            Odds = source.Odds,
            RelicGrabBag = source.RelicGrabBag,
            ExtraFields = source.ExtraFields,
            UnlockState = source.UnlockState,
            DiscoveredCards = new List<ModelId>(source.DiscoveredCards),
            DiscoveredEnemies = new List<ModelId>(source.DiscoveredEnemies),
            DiscoveredEpochs = new List<string>(source.DiscoveredEpochs),
            DiscoveredPotions = new List<ModelId>(source.DiscoveredPotions),
            DiscoveredRelics = new List<ModelId>(source.DiscoveredRelics)
        };
    }
}
