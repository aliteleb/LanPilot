namespace LanPilot.Service;

// A machine-wide file lease spans interactive and SCM sessions. The OS releases
// the handle on a crash; the file itself is deliberately never deleted.
internal sealed class ServiceInstanceLease : IDisposable
{
    private readonly FileStream _lease;

    internal ServiceInstanceLease(string directory)
    {
        Directory.CreateDirectory(directory);
        _lease = new FileStream(Path.Combine(directory, "service-instance.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Dispose() => _lease.Dispose();
}
