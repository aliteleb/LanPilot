namespace LanPilot.Service.Engine;

public sealed class PacketMemoryBudget
{
    public const long PerApplicationLimit = 4 * 1024 * 1024;
    public const long TotalLimit = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _applications = new(StringComparer.OrdinalIgnoreCase);
    private long _total;
    public long TotalBytes { get { lock (_gate) return _total; } }
    public long ReservedFor(string id) { lock (_gate) return _applications.GetValueOrDefault(id); }

    public bool TryReserve(string id, int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        lock (_gate)
        {
            long current = _applications.GetValueOrDefault(id);
            if (current + bytes > PerApplicationLimit || _total + bytes > TotalLimit) return false;
            _applications[id] = current + bytes;
            _total += bytes;
            return true;
        }
    }

    public void Release(string id, int bytes)
    {
        lock (_gate)
        {
            long current = _applications.GetValueOrDefault(id);
            if (bytes <= 0 || bytes > current) throw new InvalidOperationException("Packet budget released without ownership.");
            if (current == bytes) _applications.Remove(id);
            else _applications[id] = current - bytes;
            _total -= bytes;
        }
    }
}
