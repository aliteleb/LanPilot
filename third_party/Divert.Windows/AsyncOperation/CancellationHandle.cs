using System.Runtime.InteropServices;
using Windows.Win32;

namespace Divert.Windows.AsyncOperation;

internal sealed unsafe class CancellationHandle(SafeHandle safeHandle) : IDisposable
{
    private static class Status
    {
        public const int Idle = 0;
        public const int Canceled = 1;
        public const int Pending = 2;
        public const int Disposed = 3;
    }

    private int status;

    public SafeHandle SafeHandle => safeHandle;

    // From cancellation registration.
    public void RequestOrInvokeCancel(NativeOverlapped* nativeOverlapped)
    {
        int originalStatus = Interlocked.CompareExchange(ref status, Status.Canceled, Status.Idle);
        if (originalStatus is Status.Pending)
        {
            using (safeHandle.DangerousGetHandle(out var handle))
            {
                _ = PInvoke.CancelIoEx(new(handle), nativeOverlapped);
            }
        }
    }

    // After ERROR_IO_PENDING.
    public void CancelWhenRequested(NativeOverlapped* nativeOverlapped)
    {
        int originalStatus = Interlocked.CompareExchange(ref status, Status.Pending, Status.Idle);
        if (originalStatus is Status.Canceled)
        {
            using (safeHandle.DangerousGetHandle(out var handle))
            {
                _ = PInvoke.CancelIoEx(new(handle), nativeOverlapped);
            }
        }
    }

    // From completion callback.
    public void Reset()
    {
        int originalStatus = Interlocked.CompareExchange(ref status, Status.Idle, Status.Pending);
        if (originalStatus is Status.Canceled)
        {
            Interlocked.CompareExchange(ref status, Status.Idle, originalStatus);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref status, Status.Disposed);
    }
}
