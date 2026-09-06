namespace LanPilot.Service.Diagnostics;

// Fixed-size counters only: never retain packets, addresses, or flow contents.
public sealed class PacketDiagnostics
{
    private long _received, _sent, _blocked, _limited, _queueFull, _errors;
    private long _lastReceive, _lastSend;

    public void Received()
    {
        Interlocked.Increment(ref _received);
        Interlocked.Exchange(ref _lastReceive, Environment.TickCount64);
    }

    public void Sent()
    {
        Interlocked.Increment(ref _sent);
        Interlocked.Exchange(ref _lastSend, Environment.TickCount64);
    }

    public void Blocked() => Interlocked.Increment(ref _blocked);
    public void Limited() => Interlocked.Increment(ref _limited);
    public void QueueFull() => Interlocked.Increment(ref _queueFull);
    public void Error() => Interlocked.Increment(ref _errors);

    public object Snapshot() => new
    {
        received = Interlocked.Read(ref _received),
        sent = Interlocked.Read(ref _sent),
        blocked = Interlocked.Read(ref _blocked),
        limited = Interlocked.Read(ref _limited),
        queueFull = Interlocked.Read(ref _queueFull),
        errors = Interlocked.Read(ref _errors),
        lastReceiveAgeMs = Age(Interlocked.Read(ref _lastReceive)),
        lastSendAgeMs = Age(Interlocked.Read(ref _lastSend))
    };

    private static long? Age(long timestamp) => timestamp == 0 ? null : Environment.TickCount64 - timestamp;
}
