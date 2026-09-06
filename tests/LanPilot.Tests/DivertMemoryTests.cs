using System.IO;
using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Threading.Tasks.Sources;
using Divert.Windows;
using Divert.Windows.AsyncOperation;

namespace LanPilot.Tests;

public sealed class DivertMemoryTests
{
    // A normal asynchronous file handle lets these tests exercise the real
    // pooling code without loading WinDivert, elevation, or network traffic.
    [Fact]
    public void ReceiveOnly_CompletionsReuseOneOperationInsteadOfGrowingSendPool()
    {
        using var handle = OpenTestHandle();
        using DivertService service = new(handle);
        var receivePool = GetPool(service, "receiveVtsPool");
        var sendPool = GetPool(service, "sendVtsPool");
        DivertValueTaskSource? first = null;

        for (int i = 0; i < 100_000; i++)
        {
            DivertValueTaskSource operation = GetOperation(service, receivePool);
            first ??= operation;
            Assert.Same(first, operation);
            operation.OnCompleted(0, 123);
            Assert.Equal(123, operation.GetResult(unchecked((short)i)));
            Assert.Equal(1, receivePool.Reader.Count);
            Assert.Equal(0, sendPool.Reader.Count);
        }
    }

    [Fact]
    public void SendAndReceive_KeepSeparateBoundedPools()
    {
        using var handle = OpenTestHandle();
        using DivertService service = new(handle);
        var receivePool = GetPool(service, "receiveVtsPool");
        var sendPool = GetPool(service, "sendVtsPool");
        DivertValueTaskSource? firstReceive = null;
        DivertValueTaskSource? firstSend = null;

        for (int i = 0; i < 10_000; i++)
        {
            DivertValueTaskSource receive = GetOperation(service, receivePool);
            DivertValueTaskSource send = GetOperation(service, sendPool);
            firstReceive ??= receive;
            firstSend ??= send;
            Assert.Same(firstReceive, receive);
            Assert.Same(firstSend, send);
            Assert.NotSame(receive, send);
            receive.OnCompleted(0, 100);
            send.OnCompleted(0, 100);
            Assert.Equal(100, receive.GetResult(unchecked((short)i)));
            Assert.Equal(100, send.GetResult(unchecked((short)i)));
            Assert.Equal(1, receivePool.Reader.Count);
            Assert.Equal(1, sendPool.Reader.Count);
        }
    }

    [Fact]
    public void CanceledReceive_ReturnsToReceivePoolAndCanBeReused()
    {
        using var handle = OpenTestHandle();
        using DivertService service = new(handle);
        var pool = GetPool(service, "receiveVtsPool");
        DivertValueTaskSource operation = GetOperation(service, pool);
        operation.OnCompleted(995, 0); // ERROR_OPERATION_ABORTED
        Assert.Throws<OperationCanceledException>(() => operation.GetResult(0));
        Assert.Equal(1, pool.Reader.Count);
        Assert.Same(operation, GetOperation(service, pool));
        operation.OnCompleted(0, 10);
        Assert.Equal(10, operation.GetResult(1));
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenTestHandle()
    {
        // File.OpenHandle eagerly binds asynchronous handles to .NET's IOCP.
        // Open the handle directly so DivertService can own that binding.
        var handle = CreateFileW(
            Path.Combine(Path.GetTempPath(), $"LanPilot-pool-test-{Guid.NewGuid():N}.tmp"),
            0xC0000000, 0, IntPtr.Zero, 1, 0x44000100, IntPtr.Zero);
        Assert.False(handle.IsInvalid, $"CreateFile failed: {Marshal.GetLastPInvokeError()}");
        return handle;
    }

    [Fact]
    public unsafe void IoCompletion_ReleasesNativeStateBeforeReentrantContinuation()
    {
        using var handle = OpenTestHandle();
        using var boundHandle = ThreadPoolBoundHandle.BindHandle(handle);
        CompletionObserver observer = new();
        using IOCompletionOperation<CompletionObserver> operation = new(handle, boundHandle, observer);
        operation.Prepare(CancellationToken.None);
        observer.Callback = () =>
        {
            var field = operation.GetType().GetField("nativeOverlapped", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(Pointer.Unbox(field.GetValue(operation)!) == null,
                "The previous native operation must be released before its continuation runs.");
            operation.Prepare(CancellationToken.None);
        };
        operation.OnCompleted(0, 1);
        observer.Callback = null;
        operation.OnCompleted(0, 2);
    }

    [Fact]
    public void ReceiveCompletion_UnpinsBuffersBeforeReturningOperationToPool()
    {
        using var handle = OpenTestHandle();
        using DivertService service = new(handle, runContinuationsAsynchronously: false);
        using PinTracker tracker = new();
        var pool = GetPool(service, "receiveVtsPool");
        DivertValueTaskSource source = GetOperation(service, pool);
        var result = source.ExecuteAsync(new SuccessfulExecutor(), tracker.Memory, new DivertAddress[1], CancellationToken.None);
        Assert.False(result.IsCompleted);
        int pinsAtContinuation = -1;
        int bytes = -1;
        source.OnCompleted(_ =>
        {
            pinsAtContinuation = tracker.PinCount;
            bytes = source.GetResult(0);
        }, null, 0, ValueTaskSourceOnCompletedFlags.None);
        var operation = (IOCompletionOperation<DivertValueTaskSource>)typeof(DivertValueTaskSource)
            .GetField("ioCompletionOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        operation.OnCompleted(0, 123);
        Assert.Equal(123, bytes);
        Assert.Equal(0, pinsAtContinuation);
        Assert.Equal(0, tracker.PinCount);
    }

    private sealed class CompletionObserver : IOCompletionHandler
    {
        public Action? Callback;
        public void OnCompleted(uint errorCode, uint numBytes) => Callback?.Invoke();
    }

    private sealed class SuccessfulExecutor : IDivertValueTaskExecutor
    {
        public bool Execute(SafeHandle handle, ref readonly PendingOperation operation) => true;
    }

    private sealed class PinTracker : MemoryManager<byte>
    {
        private readonly byte[] _buffer = new byte[16];
        public int PinCount { get; private set; }
        public override Span<byte> GetSpan() => _buffer;
        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            PinCount++;
            return new MemoryHandle(null, default, this);
        }
        public override void Unpin() => PinCount--;
        protected override void Dispose(bool disposing) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    private static Channel<DivertValueTaskSource> GetPool(DivertService service, string name) =>
        (Channel<DivertValueTaskSource>)typeof(DivertService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static DivertValueTaskSource GetOperation(
        DivertService service, Channel<DivertValueTaskSource> pool) =>
        (DivertValueTaskSource)typeof(DivertService)
            .GetMethod("GetVts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [pool])!;
}
