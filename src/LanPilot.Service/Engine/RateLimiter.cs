using System.Diagnostics;

namespace LanPilot.Service.Engine;

public sealed class RateLimiter
{
    private readonly object _gate = new();
    private long? _rateBitsPerSecond;
    private double _tokens;
    private long _lastTimestamp = Stopwatch.GetTimestamp();

    public RateLimiter(long? rateBitsPerSecond)
    {
        UpdateRate(rateBitsPerSecond);
    }

    public long? RateBitsPerSecond
    {
        get
        {
            lock (_gate) return _rateBitsPerSecond;
        }
    }

    public void UpdateRate(long? rateBitsPerSecond)
    {
        if (rateBitsPerSecond is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rateBitsPerSecond), "A configured rate must be positive.");
        }

        lock (_gate)
        {
            if (_rateBitsPerSecond == rateBitsPerSecond)
            {
                return;
            }

            _rateBitsPerSecond = rateBitsPerSecond;
            _tokens = rateBitsPerSecond is null ? double.MaxValue : rateBitsPerSecond.Value / 8d * 0.25d;
            _lastTimestamp = Stopwatch.GetTimestamp();
        }
    }

    public bool TryConsume(int byteCount)
    {
        lock (_gate)
        {
            if (_rateBitsPerSecond is null)
            {
                return true;
            }

            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
            double bytesPerSecond = _rateBitsPerSecond.Value / 8d;
            double capacity = Math.Max(1500d, bytesPerSecond * 0.25d);
            _tokens = Math.Min(capacity, _tokens + elapsed * bytesPerSecond);
            _lastTimestamp = now;

            if (_tokens < byteCount)
            {
                return false;
            }

            _tokens -= byteCount;
            return true;
        }
    }
}
