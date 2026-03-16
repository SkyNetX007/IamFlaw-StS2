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
using System;
using System.Collections.Generic;
using System.Linq;

namespace IamFlaw.Patches;

[HarmonyPatch]
public class LoadSaveMidJoinPatch
{
    private static readonly string LogTag = $"{ModEntry.ModId}.LoadSaveMidJoinPatch";
    
    private const int DefaultMaxPlayers = 4;
    private const int DefaultTimeoutSeconds = 30;

    public static LoadRunLobby? CurrentLobby { get; private set; }

    [HarmonyPatch(typeof(LoadRunLobby), MethodType.Constructor, typeof(INetGameService), typeof(ILoadRunLobbyListener), typeof(SerializableRun))]
    [HarmonyPrefix]
    public static void OnLoadRunLobbyCreated(LoadRunLobby __instance)
    {
        CurrentLobby = __instance;
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
            {
                return true;
            }

            if (!state.Config.Enabled)
            {
                Log.Info($"{LogTag}: Mid-join disabled, rejecting player {senderId}");
                return true;
            }

            var run = __instance.Run;
            
            if (run.Players.Any(p => p.NetId == senderId))
            {
                Log.Info($"{LogTag}: Player {senderId} already in save, allowing normal join");
                return true;
            }

            var currentPlayerCount = run.Players.Count;
            var maxPlayers = state.Config.MaxPlayers > 0 ? state.Config.MaxPlayers : DefaultMaxPlayers;
            
            if (currentPlayerCount >= maxPlayers)
            {
                Log.Info($"{LogTag}: Player {senderId} rejected - max players ({maxPlayers}) reached");
                var hostService = (INetHostGameService)netService;
                hostService.DisconnectClient(senderId, NetError.InvalidJoin, false);
                return false;
            }

            var defaultCopyIndex = state.Config.DefaultCopySourceIndex;
            if (defaultCopyIndex < 0 || defaultCopyIndex >= run.Players.Count)
            {
                defaultCopyIndex = 0;
            }

            state.AddPendingPlayer(senderId, defaultCopyIndex);

            Log.Info($"{LogTag}: Player {senderId} waiting for approval. Default copy index: {defaultCopyIndex}. Timeout: {DefaultTimeoutSeconds}s");
            Log.Info($"{LogTag}: Use 'midjoin approve <index>' to approve with copy source, or 'midjoin reject' to reject");

            state.StartTimeoutTimer(() =>
            {
                var pending = state.GetPendingPlayer(senderId);
                if (pending != null && !pending.IsApproved)
                {
                    Log.Info($"{LogTag}: Player {senderId} join request timed out, rejecting");
                    try
                    {
                        var host = (INetHostGameService)netService;
                        host.DisconnectClient(senderId, NetError.NotInSaveGame, false);
                    }
                    catch { }
                    state.RemovePendingPlayer(senderId);
                }
            });

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
        
        run.Players.Add(newPlayer);
        
        Log.Info($"{LogTag}: Approved player {playerId}, copied from player {copySourceIndex} ({sourcePlayer.CharacterId.Entry}). Total: {run.Players.Count}");

        state.ApprovePlayer(playerId, copySourceIndex);
        state.CancelTimeout();

        var hostService = (INetHostGameService)netService;
        var connectedPlayerIds = GetConnectedPlayerIds(lobby);
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

        Log.Info($"{LogTag}: Player {playerId} successfully joined");
    }

    public static void RejectPlayer(LoadRunLobby lobby, ulong playerId)
    {
        var state = MidJoinState.Instance;
        var netService = lobby.NetService;

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

    private static HashSet<ulong> GetConnectedPlayerIds(LoadRunLobby lobby)
    {
        var field = AccessTools.Field(typeof(LoadRunLobby), "ConnectedPlayerIds");
        return (HashSet<ulong>)field.GetValue(lobby);
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
