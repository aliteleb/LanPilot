using System.Runtime.InteropServices;

namespace Divert.Windows;

internal enum WINDIVERT_LAYER
{
    WINDIVERT_LAYER_NETWORK = 0,
    WINDIVERT_LAYER_NETWORK_FORWARD = 1,
    WINDIVERT_LAYER_FLOW = 2,
    WINDIVERT_LAYER_SOCKET = 3,
    WINDIVERT_LAYER_REFLECT = 4,
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct WINDIVERT_DATA_NETWORK
{
    [FieldOffset(0)]
    public uint IfIdx;

    [FieldOffset(4)]
    public uint SubIfIdx;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal unsafe struct WINDIVERT_DATA_FLOW
{
    [FieldOffset(0)]
    public ulong EndpointId;

    [FieldOffset(8)]
    public ulong ParentEndpointId;

    [FieldOffset(16)]
    public uint ProcessId;

    [FieldOffset(20)]
    public fixed uint LocalAddr[4];

    [FieldOffset(36)]
    public fixed uint RemoteAddr[4];

    [FieldOffset(52)]
    public ushort LocalPort;

    [FieldOffset(54)]
    public ushort RemotePort;

    [FieldOffset(56)]
    public byte Protocol;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal unsafe struct WINDIVERT_DATA_SOCKET
{
    [FieldOffset(0)]
    public ulong EndpointId;

    [FieldOffset(8)]
    public ulong ParentEndpointId;

    [FieldOffset(16)]
    public uint ProcessId;

    [FieldOffset(20)]
    public fixed uint LocalAddr[4];

    [FieldOffset(36)]
    public fixed uint RemoteAddr[4];

    [FieldOffset(52)]
    public ushort LocalPort;

    [FieldOffset(54)]
    public ushort RemotePort;

    [FieldOffset(56)]
    public byte Protocol;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct WINDIVERT_DATA_REFLECT
{
    [FieldOffset(0)]
    public long Timestamp;

    [FieldOffset(8)]
    public uint ProcessId;

    [FieldOffset(12)]
    public WINDIVERT_LAYER Layer;

    [FieldOffset(16)]
    public ulong Flags;

    [FieldOffset(24)]
    public short Priority;
}

[Flags]
internal enum WINDIVERT_ADDRESS_BITS : byte
{
    Sniffed = 1 << 0,
    Outbound = 1 << 1,
    Loopback = 1 << 2,
    Impostor = 1 << 3,
    IPv6 = 1 << 4,
    IPChecksum = 1 << 5,
    TCPChecksum = 1 << 6,
    UDPChecksum = 1 << 7,
}

[StructLayout(LayoutKind.Explicit, Size = 80)]
internal unsafe struct WINDIVERT_ADDRESS
{
    [FieldOffset(0)]
    public long Timestamp;

    [FieldOffset(8)]
    public byte Layer;

    [FieldOffset(9)]
    public byte Event;

    [FieldOffset(10)]
    public WINDIVERT_ADDRESS_BITS Bits;

    [FieldOffset(11)]
    public byte Reserved1;

    [FieldOffset(12)]
    public uint Reserved2;

    [FieldOffset(16)]
    public WINDIVERT_DATA_NETWORK Network;

    [FieldOffset(16)]
    public WINDIVERT_DATA_FLOW Flow;

    [FieldOffset(16)]
    public WINDIVERT_DATA_SOCKET Socket;

    [FieldOffset(16)]
    public WINDIVERT_DATA_REFLECT Reflect;

    [FieldOffset(16)]
    public fixed byte Reserved3[64];
}

internal enum WINDIVERT_EVENT
{
    WINDIVERT_EVENT_NETWORK_PACKET = 0,
    WINDIVERT_EVENT_FLOW_ESTABLISHED = 1,
    WINDIVERT_EVENT_FLOW_DELETED = 2,
    WINDIVERT_EVENT_SOCKET_BIND = 3,
    WINDIVERT_EVENT_SOCKET_CONNECT = 4,
    WINDIVERT_EVENT_SOCKET_LISTEN = 5,
    WINDIVERT_EVENT_SOCKET_ACCEPT = 6,
    WINDIVERT_EVENT_SOCKET_CLOSE = 7,
    WINDIVERT_EVENT_REFLECT_OPEN = 8,
    WINDIVERT_EVENT_REFLECT_CLOSE = 9,
}

internal enum WINDIVERT_PARAM
{
    WINDIVERT_PARAM_QUEUE_LENGTH = 0,
    WINDIVERT_PARAM_QUEUE_TIME = 1,
    WINDIVERT_PARAM_QUEUE_SIZE = 2,
    WINDIVERT_PARAM_VERSION_MAJOR = 3,
    WINDIVERT_PARAM_VERSION_MINOR = 4,
}

internal enum WINDIVERT_SHUTDOWN
{
    WINDIVERT_SHUTDOWN_RECV = 0x1,
    WINDIVERT_SHUTDOWN_SEND = 0x2,
    WINDIVERT_SHUTDOWN_BOTH = 0x3,
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct WINDIVERT_IOCTL
{
    [FieldOffset(0)]
    public WINDIVERT_PARAM GetParam;

    [FieldOffset(0)]
    public ulong Value;

    [FieldOffset(8)]
    public WINDIVERT_PARAM SetParam;
}
