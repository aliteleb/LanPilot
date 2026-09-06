using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Divert.Windows.AsyncOperation;
using Windows.Win32.Foundation;

namespace Divert.Windows;

/// <summary>
/// Main entry point for WinDivert operations.
/// </summary>
public sealed unsafe class DivertService : IDisposable
{
    /// <summary>
    /// The highest priority for a WinDivert handle.
    /// </summary>
    public const int HighestPriority = Constants.WINDIVERT_PRIORITY_HIGHEST;

    /// <summary>
    /// The lowest priority for a WinDivert handle.
    /// </summary>
    public const int LowestPriority = Constants.WINDIVERT_PRIORITY_LOWEST;

    /// <summary>
    /// The default packet queue length for receive operations.
    /// </summary>
    public const int DefaultQueueLength = Constants.WINDIVERT_PARAM_QUEUE_LENGTH_DEFAULT;

    /// <summary>
    /// The minimum packet queue length for receive operations.
    /// </summary>
    public const int MinQueueLength = Constants.WINDIVERT_PARAM_QUEUE_LENGTH_MIN;

    /// <summary>
    /// The maximum packet queue length for receive operations.
    /// </summary>
    public const int MaxQueueLength = Constants.WINDIVERT_PARAM_QUEUE_LENGTH_MAX;

    /// <summary>
    /// The default packet queue time.
    /// </summary>
    public static TimeSpan DefaultQueueTime => TimeSpan.FromMilliseconds(Constants.WINDIVERT_PARAM_QUEUE_TIME_DEFAULT);

    /// <summary>
    /// The minimum packet queue time.
    /// </summary>
    public static TimeSpan MinQueueTime => TimeSpan.FromMilliseconds(Constants.WINDIVERT_PARAM_QUEUE_TIME_MIN);

    /// <summary>
    /// The maximum packet queue time.
    /// </summary>
    public static TimeSpan MaxQueueTime => TimeSpan.FromMilliseconds(Constants.WINDIVERT_PARAM_QUEUE_TIME_MAX);

    /// <summary>
    /// The default max number of bytes in the packet queue for receive operations.
    /// </summary>
    public const int DefaultQueueSize = Constants.WINDIVERT_PARAM_QUEUE_SIZE_DEFAULT;

    /// <summary>
    /// The minimum max number of bytes in the packet queue for receive operations.
    /// </summary>
    public const int MinQueueSize = Constants.WINDIVERT_PARAM_QUEUE_SIZE_MIN;

    /// <summary>
    /// The maximum max number of bytes in the packet queue for receive operations.
    /// </summary>
    public const int MaxQueueSize = Constants.WINDIVERT_PARAM_QUEUE_SIZE_MAX;

    /// <summary>
    /// The maximum number of packets in a single send or receive operation.
    /// </summary>
    public const int MaxBatchSize = Constants.WINDIVERT_BATCH_MAX;

    private readonly DivertHandle divertHandle;
    private readonly bool runContinuationsAsynchronously;

    private readonly ThreadPoolBoundHandle threadPoolBoundHandle;
    private readonly Channel<DivertValueTaskSource> sendVtsPool;
    private readonly Channel<DivertValueTaskSource> receiveVtsPool;
    private readonly DivertReceiveExecutor receiveExecutor;
    private readonly DivertSendExecutor sendExecutor;

    private DivertService(DivertHandle divertHandle)
    {
        this.divertHandle = divertHandle;
        threadPoolBoundHandle = ThreadPoolBoundHandle.BindHandle(divertHandle);
        sendVtsPool = Channel.CreateUnbounded<DivertValueTaskSource>();
        receiveVtsPool = Channel.CreateUnbounded<DivertValueTaskSource>();
        receiveExecutor = new DivertReceiveExecutor();
        sendExecutor = new DivertSendExecutor();
    }

    private static DivertHandle OpenHandle(
        ReadOnlySpan<byte> filter,
        DivertLayer layer,
        short priority,
        DivertFlags flags
    )
    {
        if (priority < LowestPriority || priority > HighestPriority)
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        var pBuffer = Unsafe.AsPointer(ref MemoryMarshal.GetReference(filter));
        var handle = NativeMethods.WinDivertOpen(new(pBuffer), (WINDIVERT_LAYER)layer, priority, (ulong)flags);
        if (new HANDLE(handle) == HANDLE.INVALID_HANDLE_VALUE)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return new DivertHandle(handle);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DivertService"/> class.
    /// </summary>
    /// <param name="filter">The packet filter.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="priority">The priority of the handle.</param>
    /// <param name="flags">The handle flags.</param>
    /// <param name="runContinuationsAsynchronously">Whether to force continuations to run asynchronously.</param>
    public DivertService(
        DivertFilter filter,
        DivertLayer layer = DivertLayer.Network,
        short priority = 0,
        DivertFlags flags = DivertFlags.None,
        bool runContinuationsAsynchronously = true
    )
        : this(OpenHandle(DivertHelper.CompileFilter(filter, layer), layer, priority, flags))
    {
        this.runContinuationsAsynchronously = runContinuationsAsynchronously;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DivertService"/> class.
    /// </summary>
    /// <param name="filter">The packet filter.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="priority">The priority of the handle.</param>
    /// <param name="flags">The handle flags.</param>
    /// <param name="runContinuationsAsynchronously">Whether to force continuations to run asynchronously.</param>
    public DivertService(
        ReadOnlySpan<byte> filter,
        DivertLayer layer = DivertLayer.Network,
        short priority = 0,
        DivertFlags flags = DivertFlags.None,
        bool runContinuationsAsynchronously = true
    )
        : this(OpenHandle(filter, layer, priority, flags))
    {
        this.runContinuationsAsynchronously = runContinuationsAsynchronously;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DivertService"/> class.
    /// </summary>
    /// <param name="handle">An existing WinDivert handle.</param>
    /// <param name="runContinuationsAsynchronously">Whether to force continuations to run asynchronously.</param>
    public DivertService(SafeHandle handle, bool runContinuationsAsynchronously = true)
        : this(new DivertHandle(handle.DangerousGetHandle(), ownsHandle: false))
    {
        this.runContinuationsAsynchronously = runContinuationsAsynchronously;
    }

    private bool disposed = false;

    private static void DisposeVtsPool(Channel<DivertValueTaskSource> pool)
    {
        pool.Writer.TryComplete();
        while (pool.Reader.TryRead(out var vts))
        {
            vts.Dispose();
        }
    }

    /// <summary>
    /// Releases all resources used by the <see cref="DivertService"/>.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        DisposeVtsPool(sendVtsPool);
        DisposeVtsPool(receiveVtsPool);
        threadPoolBoundHandle.Dispose();
        divertHandle.Dispose();
    }

    /// <summary>
    /// Gets the underlying WinDivert handle.
    /// </summary>
    public DivertHandle SafeHandle => divertHandle;

    /// <summary>Stops accepting packets without freeing outstanding I/O buffers.</summary>
    public void Shutdown(DivertShutdown how)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        bool added = false;
        try
        {
            divertHandle.DangerousAddRef(ref added);
            if (!NativeMethods.WinDivertShutdown(divertHandle.DangerousGetHandle(), (WINDIVERT_SHUTDOWN)how))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally { if (added) divertHandle.DangerousRelease(); }
    }

    private DivertValueTaskSource GetVts(Channel<DivertValueTaskSource> vtsPool)
    {
        if (!vtsPool.Reader.TryRead(out var vts))
        {
            vts = new DivertValueTaskSource(
                vtsPool,
                divertHandle,
                threadPoolBoundHandle,
                runContinuationsAsynchronously
            );
        }
        return vts;
    }

    /// <summary>
    /// Receives one or more packets from the network stack.
    /// </summary>
    /// <param name="buffer">Buffer to receive the packet data.</param>
    /// <param name="addresses">Buffer to receive the packet addresses.</param>
    /// <param name="cancellationToken">Token to observe cancellation requests.</param>
    /// <returns>A ValueTask representing the asynchronous receive operation.</returns>
    public ValueTask<DivertReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        Memory<DivertAddress> addresses,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<DivertReceiveResult>(cancellationToken);
        }

        var vts = GetVts(receiveVtsPool);
        return receiveExecutor.ReceiveAsync(vts, buffer, addresses, cancellationToken);
    }

    /// <summary>
    /// Injects one or more packets into the network stack.
    /// </summary>
    /// <param name="buffer">Buffer containing the packet data.</param>
    /// <param name="addresses">Addresses of the packets to be injected.</param>
    /// <param name="cancellationToken">Token to observe cancellation requests.</param>
    /// <returns>A ValueTask representing the asynchronous send operation.</returns>
    public ValueTask<int> SendAsync(
        ReadOnlyMemory<byte> buffer,
        ReadOnlyMemory<DivertAddress> addresses,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        var vts = GetVts(sendVtsPool);
        return sendExecutor.SendAsync(vts, buffer, addresses, cancellationToken);
    }

    /// <summary>
    /// Gets the version of the WinDivert driver.
    /// </summary>
    public Version Version
    {
        get
        {
            int major = (int)
                DivertIOControl.GetParam(threadPoolBoundHandle, WINDIVERT_PARAM.WINDIVERT_PARAM_VERSION_MAJOR);
            int minor = (int)
                DivertIOControl.GetParam(threadPoolBoundHandle, WINDIVERT_PARAM.WINDIVERT_PARAM_VERSION_MINOR);
            return new Version(major, minor);
        }
    }

    /// <summary>
    /// Gets or sets the length of the receive queue.
    /// </summary>
    public int QueueLength
    {
        get => (int)DivertIOControl.GetParam(threadPoolBoundHandle, WINDIVERT_PARAM.WINDIVERT_PARAM_QUEUE_LENGTH);
        set
        {
            if (value < MinQueueLength || value > MaxQueueLength)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            DivertIOControl.SetParam(threadPoolBoundHandle, WINDIVERT_PARAM.WINDIVERT_PARAM_QUEUE_LENGTH, (ulong)value);
        }
    }

    /// <summary>
    /// Gets or sets the maximum packet queue time.
    /// </summary>
    public TimeSpan QueueTime
    {
        get =>
            TimeSpan.FromMilliseconds(
                DivertIOControl.GetParam(threadPoolBoundHandle, WINDIVERT_PARAM.WINDIVERT_PARAM_QUEUE_TIME)
            );
        set
        {
            if (value < MinQueueTime || value > MaxQueueTime)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            DivertIOControl.SetParam(
                threadPoolBoundHandle,
                WINDIVERT_PARAM.WINDIVERT_PARAM_QUEUE_TIME,
                (ulong)value.TotalMilliseconds
            );
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of bytes in the receive queue.
    /// </summary>
    public int QueueSize
    {
        get => (int)DivertIOControl.GetParam(threadPoolBoundHandle, WINDIVERT_PARAM.WINDIVERT_PARAM_QUEUE_SIZE);
        set
        {
            if (value < MinQueueSize || value > MaxQueueSize)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            DivertIOControl.SetParam(threadPoolBoundHandle, WINDIVERT_PARAM.WINDIVERT_PARAM_QUEUE_SIZE, (ulong)value);
        }
    }
}
