using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;
using IamFlaw.Models;
using IamFlaw.Patches;

namespace IamFlaw.Utils;

public class MidJoinConsoleCommand : AbstractConsoleCmd
{
    private static readonly string LogTag = $"{ModEntry.ModId}.MidJoinConsoleCommand";

    public override string CmdName => "midjoin";

    public override string Args => "<subcommand:string> [args...]";

    public override string Description => "Mid-join commands: enable, disable, setmax, setcopy, status, players, pending, approve, reject";

    public override bool IsNetworked => true;

    public override CmdResult Process(Player issuingPlayer, string[] args)
    {
        var state = MidJoinState.Instance;

        if (args.Length < 1)
        {
            return new CmdResult(false, "Usage: midjoin <enable|disable|setmax|setcopy|status|players|pending|approve|reject> [args]");
        }

        var subCommand = args[0].ToLower();

        switch (subCommand)
        {
            case "enable":
                state.Config.Enabled = true;
                Log.Info($"{LogTag}: Mid-join enabled");
                return new CmdResult(true, "Mid-join enabled");

            case "disable":
                state.Config.Enabled = false;
                Log.Info($"{LogTag}: Mid-join disabled");
                return new CmdResult(true, "Mid-join disabled");

            case "setmax":
                if (args.Length < 2 || !int.TryParse(args[1], out int maxPlayers) || maxPlayers < 1 || maxPlayers > 4)
                {
                    return new CmdResult(false, "Usage: midjoin setmax <1-4>");
                }
                state.Config.MaxPlayers = maxPlayers;
                Log.Info($"{LogTag}: Max players set to {maxPlayers}");
                return new CmdResult(true, $"Max players set to {maxPlayers}");

            case "setcopy":
                if (args.Length < 2 || !int.TryParse(args[1], out int copyIndex) || copyIndex < 0)
                {
                    return new CmdResult(false, "Usage: midjoin setcopy <index>");
                }
                state.Config.DefaultCopySourceIndex = copyIndex;
                Log.Info($"{LogTag}: Default copy source index set to {copyIndex}");
                return new CmdResult(true, $"Default copy source index set to {copyIndex}");

            case "status":
                var statusMsg = $"Mid-join enabled: {state.Config.Enabled}, Max players: {state.Config.MaxPlayers}, Default copy: {state.Config.DefaultCopySourceIndex}";
                return new CmdResult(true, statusMsg);

            case "players":
                return GetPlayersList();

            case "pending":
                return GetPendingList();

            case "approve":
                return ApprovePlayer(args);

            case "reject":
                return RejectPlayer(args);

            default:
                return new CmdResult(false, $"Unknown: {subCommand}. Use: enable, disable, setmax, setcopy, status, players, pending, approve, reject");
        }
    }

    private CmdResult GetPlayersList()
    {
        try
        {
            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
            {
                return new CmdResult(false, "No run in progress");
            }

            var players = runState.Players;
            if (players == null || players.Count == 0)
            {
                return new CmdResult(true, "No players in run");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Players in save ({players.Count}):");
            
            for (int i = 0; i < players.Count; i++)
            {
                var player = players[i];
                var characterId = player.Character?.Id?.Entry ?? "Unknown";
                sb.AppendLine($"  [{i}] NetId: {player.NetId}, Character: {characterId}");
            }

            return new CmdResult(true, sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return new CmdResult(false, $"Error: {ex.Message}");
        }
    }

    private CmdResult GetPendingList()
    {
        var state = MidJoinState.Instance;
        var pending = state.PendingPlayers;

        if (pending.Count == 0)
        {
            return new CmdResult(true, "No pending players");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Pending players ({pending.Count}):");
        
        foreach (var p in pending)
        {
            var elapsed = (DateTime.Now - p.JoinTime).Seconds;
            sb.AppendLine($"  NetId: {p.NetId}, Waiting: {elapsed}s, Default copy index: {p.CopySourceIndex}");
        }
        
        sb.AppendLine("");
        sb.AppendLine("To approve: midjoin approve <netId> [copyIndex]");
        sb.AppendLine("To reject: midjoin reject <netId>");

        return new CmdResult(true, sb.ToString().TrimEnd());
    }

    private CmdResult ApprovePlayer(string[] args)
    {
        if (args.Length < 2)
        {
            return new CmdResult(false, "Usage: midjoin approve <netId> [copyIndex]");
        }

        if (!ulong.TryParse(args[1], out ulong netId))
        {
            return new CmdResult(false, "Invalid NetId");
        }

        var state = MidJoinState.Instance;
        var pending = state.GetPendingPlayer(netId);
        
        if (pending == null)
        {
            return new CmdResult(false, $"Player {netId} not in pending list");
        }

        int copyIndex = pending.CopySourceIndex;
        if (args.Length >= 3 && int.TryParse(args[2], out int newCopyIndex))
        {
            copyIndex = newCopyIndex;
        }

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            return new CmdResult(false, "No run in progress");
        }

        if (copyIndex < 0 || copyIndex >= runState.Players.Count)
        {
            return new CmdResult(false, $"Invalid copy index. Must be 0-{runState.Players.Count - 1}");
        }

        var lobby = GetLoadRunLobby();
        if (lobby == null)
        {
            return new CmdResult(false, "No LoadRunLobby found");
        }

        LoadSaveMidJoinPatch.ApproveAndCompleteJoin(lobby, netId, copyIndex);
        
        return new CmdResult(true, $"Approved player {netId} with copy index {copyIndex}");
    }

    private CmdResult RejectPlayer(string[] args)
    {
        if (args.Length < 2)
        {
            return new CmdResult(false, "Usage: midjoin reject <netId>");
        }

        if (!ulong.TryParse(args[1], out ulong netId))
        {
            return new CmdResult(false, "Invalid NetId");
        }

        var state = MidJoinState.Instance;
        var pending = state.GetPendingPlayer(netId);
        
        if (pending == null)
        {
            return new CmdResult(false, $"Player {netId} not in pending list");
        }

        var lobby = GetLoadRunLobby();
        if (lobby == null)
        {
            return new CmdResult(false, "No LoadRunLobby found");
        }

        LoadSaveMidJoinPatch.RejectPlayer(lobby, netId);
        
        return new CmdResult(true, $"Rejected player {netId}");
    }

    private LoadRunLobby? GetLoadRunLobby()
    {
        return LoadSaveMidJoinPatch.CurrentLobby;
    }
}
