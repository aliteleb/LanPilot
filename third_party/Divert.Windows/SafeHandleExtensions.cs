using System.Runtime.InteropServices;

namespace Divert.Windows;

internal ref struct SafeHandleReference<T>(T safeHandle) : IDisposable
    where T : SafeHandle
{
    private bool disposed;

    public void Dispose()
    {
        if (!disposed)
        {
            safeHandle.DangerousRelease();
            disposed = true;
        }
    }
}

internal static class SafeHandleExtensions
{
    public static SafeHandleReference<T> DangerousGetHandle<T>(this T safeHandle, out IntPtr handle)
        where T : SafeHandle
    {
        bool success = false;
        safeHandle.DangerousAddRef(ref success);
        handle = safeHandle.DangerousGetHandle();
        return new SafeHandleReference<T>(safeHandle);
    }
}
