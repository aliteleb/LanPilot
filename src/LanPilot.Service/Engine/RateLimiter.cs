using System.Diagnostics;

namespace LanPilot.Service.Engine;

public sealed class RateLimiter
{
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private long? _rateBitsPerSecond;
    private double _tokens;
    private long _lastTimestamp;

    public RateLimiter(long? rateBitsPerSecond, TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
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
            _lastTimestamp = _clock.GetTimestamp();
        }
    }

    public bool TryConsume(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        lock (_gate)
        {
            if (_rateBitsPerSecond is null)
            {
                return true;
            }

            long now = _clock.GetTimestamp();
            double elapsed = _clock.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
            double bytesPerSecond = _rateBitsPerSecond.Value / 8d;
            double capacity = Math.Max(1500d, bytesPerSecond * 0.25d);
            _tokens = Math.Min(capacity, _tokens + elapsed * bytesPerSecond);
            _lastTimestamp = now;

            // An oversized frame can spend a full bucket and borrow the rest.
            // The debt is repaid before the next frame; small rates never make
            // a legal Ethernet frame permanently impossible to transmit.
            if (_tokens < Math.Min(byteCount, capacity))
            {
                return false;
            }

            _tokens -= byteCount;
            return true;
        }
    }

    public TimeSpan TimeUntilAvailable(int byteCount)
    {
        lock (_gate)
        {
            if (_rateBitsPerSecond is null) return TimeSpan.Zero;
            double bytesPerSecond = _rateBitsPerSecond.Value / 8d;
            double capacity = Math.Max(1500d, bytesPerSecond * 0.25d);
            double available = Math.Min(capacity, _tokens + _clock.GetElapsedTime(_lastTimestamp, _clock.GetTimestamp()).TotalSeconds * bytesPerSecond);
            return TimeSpan.FromSeconds(Math.Clamp((Math.Min(byteCount, capacity) - available) / bytesPerSecond, 0.001d, 60d));
        }
    }
}
