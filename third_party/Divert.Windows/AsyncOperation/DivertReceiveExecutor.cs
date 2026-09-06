using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Divert.Windows.AsyncOperation;

internal sealed class DivertReceiveExecutor : IDivertValueTaskExecutor
{
    private readonly uint[] addressesLengthBuffer = GC.AllocateArray<uint>(1, pinned: true);

    public unsafe bool Execute(SafeHandle safeHandle, ref readonly PendingOperation pendingOperation)
    {
        using var _ = safeHandle.DangerousGetHandle(out var handle);
        addressesLengthBuffer[0] = (uint)(pendingOperation.Addresses.Length * sizeof(DivertAddress));
        return NativeMethods.WinDivertRecvEx(
            handle,
            pendingOperation.PacketBufferHandle.Pointer,
            (uint)pendingOperation.PacketBuffer.Length,
            null,
            0,
            (WINDIVERT_ADDRESS*)pendingOperation.AddressesHandle.Pointer,
            (uint*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(addressesLengthBuffer.AsSpan())),
            pendingOperation.NativeOverlapped
        );
    }

    public async ValueTask<DivertReceiveResult> ReceiveAsync(
        DivertValueTaskSource source,
        Memory<byte> buffer,
        Memory<DivertAddress> addresses,
        CancellationToken cancellationToken
    )
    {
        int dataLength = await source.ExecuteAsync(this, buffer, addresses, cancellationToken).ConfigureAwait(false);
        int addressesLength = (int)addressesLengthBuffer[0] / Marshal.SizeOf<DivertAddress>();
        return new DivertReceiveResult(dataLength, addressesLength);
    }
}
