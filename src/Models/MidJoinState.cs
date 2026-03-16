using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace IamFlaw.Models;

public class PendingPlayer
{
    public ulong NetId { get; set; }
    public DateTime JoinTime { get; set; }
    public bool IsApproved { get; set; }
    public int CopySourceIndex { get; set; }
}

public class MidJoinState
{
    private static MidJoinState? _instance;
    public static MidJoinState Instance => _instance ??= new MidJoinState();

    public MidJoinConfig Config { get; set; } = new MidJoinConfig();
    public List<PendingPlayer> PendingPlayers { get; } = new List<PendingPlayer>();
    public int ApprovedPlayerCount { get; set; }

    private CancellationTokenSource? _timeoutCts;

    public void Reset()
    {
        PendingPlayers.Clear();
        ApprovedPlayerCount = 0;
        _timeoutCts?.Cancel();
        _timeoutCts = null;
    }

    public void AddPendingPlayer(ulong netId, int defaultCopyIndex)
    {
        var pending = new PendingPlayer
        {
            NetId = netId,
            JoinTime = DateTime.Now,
            IsApproved = false,
            CopySourceIndex = defaultCopyIndex
        };
        PendingPlayers.Add(pending);
    }

    public PendingPlayer? GetPendingPlayer(ulong netId)
    {
        foreach (var p in PendingPlayers)
        {
            if (p.NetId == netId) return p;
        }
        return null;
    }

    public bool RemovePendingPlayer(ulong netId)
    {
        return PendingPlayers.RemoveAll(p => p.NetId == netId) > 0;
    }

    public bool ApprovePlayer(ulong netId, int copySourceIndex)
    {
        var pending = GetPendingPlayer(netId);
        if (pending == null) return false;
        
        pending.IsApproved = true;
        pending.CopySourceIndex = copySourceIndex;
        ApprovedPlayerCount++;
        return true;
    }

    public bool RejectPlayer(ulong netId)
    {
        return RemovePendingPlayer(netId);
    }

    public bool IsPlayerApproved(ulong netId)
    {
        var pending = GetPendingPlayer(netId);
        return pending?.IsApproved ?? false;
    }

    public int GetPlayerCopySourceIndex(ulong netId)
    {
        var pending = GetPendingPlayer(netId);
        return pending?.CopySourceIndex ?? 0;
    }

    public void StartTimeoutTimer(Action onTimeout)
    {
        _timeoutCts?.Cancel();
        _timeoutCts = new CancellationTokenSource();
        
        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(30000, _timeoutCts.Token);
                onTimeout?.Invoke();
            }
            catch (TaskCanceledException)
            {
            }
        });
    }

    public void CancelTimeout()
    {
        _timeoutCts?.Cancel();
    }
}
