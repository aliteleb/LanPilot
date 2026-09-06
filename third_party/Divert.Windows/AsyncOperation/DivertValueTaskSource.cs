using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using Windows.Win32.Foundation;

namespace Divert.Windows.AsyncOperation;

internal sealed unsafe class DivertValueTaskSource : IDisposable, IValueTaskSource<int>, IOCompletionHandler
{
    private readonly Channel<DivertValueTaskSource> pool;
    private readonly IOCompletionOperation<DivertValueTaskSource> ioCompletionOperation;

    private ManualResetValueTaskSourceCore<int> source;
    private PendingOperation pendingOperation;

    public DivertValueTaskSource(
        Channel<DivertValueTaskSource> pool,
        DivertHandle divertHandle,
        ThreadPoolBoundHandle threadPoolBoundHandle,
        bool runContinuationsAsynchronously
    )
    {
        this.pool = pool;
        ioCompletionOperation = new IOCompletionOperation<DivertValueTaskSource>(
            divertHandle,
            threadPoolBoundHandle,
            this
        );
        source = new ManualResetValueTaskSourceCore<int>
        {
            RunContinuationsAsynchronously = runContinuationsAsynchronously,
        };
    }

    private SafeHandle SafeHandle => ioCompletionOperation.SafeHandle;

    public void Dispose()
    {
        pendingOperation.Dispose();
        pendingOperation = default;
        ioCompletionOperation.Dispose();
    }

    public int GetResult(short token)
    {
        try
        {
            return source.GetResult(token);
        }
        finally
        {
            source.Reset();
            if (!pool.Writer.TryWrite(this))
            {
                Dispose();
            }
        }
    }

    public ValueTaskSourceStatus GetStatus(short token) => source.GetStatus(token);

    public void OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags
    ) => source.OnCompleted(continuation, state, token, flags);

    private ref readonly PendingOperation PrepareOperation(
        Memory<byte> packetBuffer,
        Memory<DivertAddress> addresses,
        CancellationToken cancellationToken
    )
    {
        var nativeOverlapped = ioCompletionOperation.Prepare(cancellationToken);
        pendingOperation = new PendingOperation(nativeOverlapped, packetBuffer, addresses, cancellationToken);
        return ref pendingOperation;
    }

    public void OnCompleted(uint errorCode, uint numBytes)
    {
        // LanPilot: unpin and forget buffers before the source can be reused.
        // A using scope around SetResult leaves cleanup racing its continuation.
        var token = pendingOperation.CancellationToken;
        pendingOperation.Dispose();
        pendingOperation = default;
        if (errorCode is (uint)WIN32_ERROR.ERROR_SUCCESS)
        {
            source.SetResult((int)numBytes);
        }
        else if (errorCode is (uint)WIN32_ERROR.ERROR_OPERATION_ABORTED)
        {
            var exception = token.IsCancellationRequested
                ? new OperationCanceledException(token)
                : new OperationCanceledException();
            source.SetException(exception);
        }
        else
        {
            var exception = new Win32Exception((int)errorCode);
            source.SetException(exception);
        }
    }

    public ValueTask<int> ExecuteAsync<TExecutor>(
        TExecutor executor,
        Memory<byte> buffer,
        Memory<DivertAddress> addresses,
        CancellationToken cancellationToken
    )
        where TExecutor : IDivertValueTaskExecutor
    {
        try
        {
            ref readonly var pendingOperation = ref PrepareOperation(buffer, addresses, cancellationToken);
            executor.DelayExecutionInTests();
            bool success = executor.Execute(SafeHandle, in pendingOperation);
            if (!success)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is (int)WIN32_ERROR.ERROR_IO_PENDING)
                {
                    ioCompletionOperation.CancelWhenRequested(pendingOperation.NativeOverlapped);
                }
                else
                {
                    Dispose();
                    return ValueTask.FromException<int>(new Win32Exception(error));
                }
            }
            return new ValueTask<int>(this, source.Version);
        }
        catch
        {
            Dispose();
            throw;
        }
    }
}
