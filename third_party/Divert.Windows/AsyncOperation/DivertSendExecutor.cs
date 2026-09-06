using System.Runtime.InteropServices;

namespace Divert.Windows.AsyncOperation;

internal sealed unsafe class DivertSendExecutor : IDivertValueTaskExecutor
{
    public bool Execute(SafeHandle safeHandle, ref readonly PendingOperation pendingOperation)
    {
        using var _ = safeHandle.DangerousGetHandle(out var handle);
        return NativeMethods.WinDivertSendEx(
            handle,
            pendingOperation.PacketBufferHandle.Pointer,
            (uint)pendingOperation.PacketBuffer.Length,
            null,
            0,
            (WINDIVERT_ADDRESS*)pendingOperation.AddressesHandle.Pointer,
            (uint)(pendingOperation.Addresses.Length * sizeof(DivertAddress)),
            pendingOperation.NativeOverlapped
        );
    }

    public ValueTask<int> SendAsync(
        DivertValueTaskSource source,
        ReadOnlyMemory<byte> buffer,
        ReadOnlyMemory<DivertAddress> addresses,
        CancellationToken cancellationToken
    )
    {
        return source.ExecuteAsync(
            this,
            MemoryMarshal.AsMemory(buffer),
            MemoryMarshal.AsMemory(addresses),
            cancellationToken
        );
    }
}
