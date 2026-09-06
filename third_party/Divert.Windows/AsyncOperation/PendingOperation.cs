using System.Buffers;

namespace Divert.Windows.AsyncOperation;

internal unsafe struct PendingOperation(
    NativeOverlapped* nativeOverlapped,
    Memory<byte> packetBuffer,
    Memory<DivertAddress> addresses,
    CancellationToken cancellationToken
) : IDisposable
{
    private MemoryHandle packetBufferHandle = packetBuffer.Pin();
    private MemoryHandle addressesHandle = addresses.Pin();

    public readonly NativeOverlapped* NativeOverlapped => nativeOverlapped;
    public readonly MemoryHandle PacketBufferHandle => packetBufferHandle;
    public readonly MemoryHandle AddressesHandle => addressesHandle;

    public readonly Memory<byte> PacketBuffer => packetBuffer;
    public readonly Memory<DivertAddress> Addresses => addresses;
    public readonly CancellationToken CancellationToken => cancellationToken;

    public void Dispose()
    {
        packetBufferHandle.Dispose();
        addressesHandle.Dispose();
    }
}
