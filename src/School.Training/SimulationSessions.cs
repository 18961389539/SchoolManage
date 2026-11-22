using System.Collections.Concurrent;

namespace School.Training;

public sealed class SimulationSessions(IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, TerminalLease> _leases = new();
    private readonly CancellationToken _applicationStopping = lifetime.ApplicationStopping;

    public bool TryAcquire(string deviceId, string userId, string owner, string securityStamp, CancellationToken requestAborted, out TerminalLease lease, string? learnerNodeId = null)
    {
        lease = new TerminalLease(deviceId, userId, owner, securityStamp, CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _applicationStopping), learnerNodeId);
        if (lease.Token.IsCancellationRequested || !_leases.TryAdd(deviceId, lease))
        {
            lease.Dispose();
            return false;
        }
        // Stop may race the first check. A linked, canceled lease must never become usable.
        if (lease.Token.IsCancellationRequested)
        {
            Release(lease);
            return false;
        }
        return true;
    }

    public string? Owner(string deviceId) => _leases.TryGetValue(deviceId, out var lease) ? lease.Owner : null;
    public int Count(IEnumerable<string> deviceIds) => deviceIds.Count(x => _leases.ContainsKey(x));

    public int Reclaim(IEnumerable<string> deviceIds)
    {
        var count = 0;
        foreach (var id in deviceIds)
        {
            if (_leases.TryGetValue(id, out var lease))
            {
                lease.Cancel();
                count++;
            }
        }
        return count;
    }

    public int RevokeUser(string userId)
    {
        var count = 0;
        foreach (var lease in _leases.Values)
        {
            if (lease.UserId != userId) continue;
            lease.Cancel();
            count++;
        }
        return count;
    }

    public void RevokeNodeLearners(string nodeId, string? userId = null)
    {
        foreach (var lease in _leases.Values)
            if (lease.LearnerNodeId == nodeId && (userId is null || lease.UserId == userId)) lease.Cancel();
    }

    public void Release(TerminalLease lease)
    {
        _leases.TryRemove(new KeyValuePair<string, TerminalLease>(lease.DeviceId, lease));
        lease.Dispose();
    }
}

public sealed class TerminalLease(string deviceId, string userId, string owner, string securityStamp, CancellationTokenSource cancellation, string? learnerNodeId = null) : IDisposable
{
    private readonly CancellationToken _token = cancellation.Token;
    public string DeviceId { get; } = deviceId;
    public string UserId { get; } = userId;
    public string Owner { get; } = owner;
    public string SecurityStamp { get; } = securityStamp;
    public string? LearnerNodeId { get; } = learnerNodeId;
    public CancellationToken Token => _token;
    public void Cancel() { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    public void Dispose() => cancellation.Dispose();
}
