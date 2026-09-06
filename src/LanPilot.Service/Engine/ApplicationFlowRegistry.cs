using System.Collections.Concurrent;

namespace LanPilot.Service.Engine;

// One identity source shared by passive accounting and the download limiter.
// Unknown identities are deliberately not inferred from a port or executable name.
public sealed class ApplicationFlowRegistry
{
    private const int MaximumFlows = 65536;
    private readonly ConcurrentDictionary<ApplicationDownloadLimiter.FlowKey, Entry> _events = new();
    private IReadOnlyDictionary<ApplicationDownloadLimiter.FlowKey, string> _tcp = new Dictionary<ApplicationDownloadLimiter.FlowKey, string>();
    internal int Count => _events.Count + Volatile.Read(ref _tcp).Count;

    internal void Register(ApplicationDownloadLimiter.FlowKey key, string? id)
    {
        if (_events.Count < MaximumFlows || _events.ContainsKey(key))
            _events[key] = new(id, Environment.TickCount64);
    }

    internal bool TryGet(ApplicationDownloadLimiter.FlowKey key, out string? id)
    {
        if (_events.TryGetValue(key, out Entry entry))
        {
            id = entry.Id;
            return id is not null;
        }
        return Volatile.Read(ref _tcp).TryGetValue(key, out id);
    }

    internal void ReplaceTcp(IReadOnlyDictionary<ApplicationDownloadLimiter.FlowKey, string> snapshot)
    {
        Volatile.Write(ref _tcp, snapshot);
        long cutoff = Environment.TickCount64 - 300000;
        foreach (var pair in _events)
            if (pair.Value.Tick < cutoff) _events.TryRemove(pair);
    }

    internal void Clear()
    {
        _events.Clear();
        Volatile.Write(ref _tcp, new Dictionary<ApplicationDownloadLimiter.FlowKey, string>());
    }

    private readonly record struct Entry(string? Id, long Tick);
}
