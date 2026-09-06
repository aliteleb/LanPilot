using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Divert.Windows;

/// <summary>
/// Represents the address of a captured or injected packet.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DivertAddress
{
    /// <summary>
    /// Network layer data.
    /// </summary>
    public struct NetworkData
    {
        /// <summary>
        /// The interface index on which the packet was captured or to which it will be injected.
        /// </summary>
        public uint InterfaceIndex;

        /// <summary>
        /// The sub-interface index of the interface.
        /// </summary>
        public uint SubInterfaceIndex;
    }

    /// <summary>
    /// Flow layer data.
    /// </summary>
    public struct FlowData
    {
        /// <summary>
        /// The endpoint ID of the flow.
        /// </summary>
        public ulong EndpointId;

        /// <summary>
        /// The parent endpoint ID of the flow.
        /// </summary>
        public ulong ParentEndpointId;

        /// <summary>
        /// The process ID associated with the flow.
        /// </summary>
        public uint ProcessId;

        /// <summary>
        /// The local IP address of the flow.
        /// </summary>
        public IPAddress LocalAddress;

        /// <summary>
        /// The remote IP address of the flow.
        /// </summary>
        public IPAddress RemoteAddress;

        /// <summary>
        /// The local port of the flow.
        /// </summary>
        public ushort LocalPort;

        /// <summary>
        /// The remote port of the flow.
        /// </summary>
        public ushort RemotePort;

        /// <summary>
        /// The protocol of the flow.
        /// </summary>
        public byte Protocol;
    }

    /// <summary>
    /// Socket layer data.
    /// </summary>
    public struct SocketData
    {
        /// <summary>
        /// The endpoint ID of the socket operation.
        /// </summary>
        public ulong EndpointId;

        /// <summary>
        /// The parent endpoint ID of the socket operation.
        /// </summary>
        public ulong ParentEndpointId;

        /// <summary>
        /// The process ID associated with the socket operation.
        /// </summary>
        public uint ProcessId;

        /// <summary>
        /// The local IP address of the socket operation.
        /// </summary>
        public IPAddress LocalAddress;

        /// <summary>
        /// The remote IP address of the socket operation.
        /// </summary>
        public IPAddress RemoteAddress;

        /// <summary>
        /// The local port of the socket operation.
        /// </summary>
        public ushort LocalPort;

        /// <summary>
        /// The remote port of the socket operation.
        /// </summary>
        public ushort RemotePort;

        /// <summary>
        /// The protocol of the socket operation.
        /// </summary>
        public byte Protocol;
    }

    /// <summary>
    /// Reflect layer data.
    /// </summary>
    public struct ReflectData
    {
        /// <summary>
        /// The timestamp when the handle was opened.
        /// </summary>
        public long Timestamp;

        /// <summary>
        /// The process ID that opened the handle.
        /// </summary>
        public uint ProcessId;

        /// <summary>
        /// The layer parameter used when opening the handle.
        /// </summary>
        public DivertLayer Layer;

        /// <summary>
        /// The flags parameter used when opening the handle.
        /// </summary>
        public DivertFlags Flags;

        /// <summary>
        /// The priority parameter used when opening the handle.
        /// </summary>
        public short Priority;
    }

    private WINDIVERT_ADDRESS address;

    /// <summary>
    /// Initializes a new instance of the <see cref="DivertAddress"/> struct with the specified interface and
    /// sub-interface indices.
    /// </summary>
    /// <param name="interfaceIndex">The interface index.</param>
    /// <param name="subInterfaceIndex">The sub-interface index.</param>
    public DivertAddress(int interfaceIndex, int subInterfaceIndex = 0)
    {
        address = new WINDIVERT_ADDRESS
        {
            Network = new WINDIVERT_DATA_NETWORK
            {
                IfIdx = checked((uint)interfaceIndex),
                SubIfIdx = checked((uint)subInterfaceIndex),
            },
        };
    }

    /// <summary>
    /// Clears all fields of the <see cref="DivertAddress"/>.
    /// </summary>
    public void Reset()
    {
        fixed (WINDIVERT_ADDRESS* pAddress = &address)
        {
            Unsafe.InitBlock(pAddress, 0, (uint)sizeof(WINDIVERT_ADDRESS));
        }
    }

    /// <summary>
    /// Gets the timestamp of the event.
    /// </summary>
    public readonly long Timestamp => address.Timestamp;

    /// <summary>
    /// Gets the layer of the event.
    /// </summary>
    public readonly DivertLayer Layer => (DivertLayer)address.Layer;

    /// <summary>
    /// Gets the event type.
    /// </summary>
    public readonly DivertEvent Event => (DivertEvent)address.Event;

    private readonly bool GetBit(WINDIVERT_ADDRESS_BITS bit)
    {
        return (address.Bits & bit) != 0;
    }

    private void SetBit(WINDIVERT_ADDRESS_BITS bit, bool value)
    {
        if (value)
        {
            address.Bits |= bit;
        }
        else
        {
            address.Bits &= ~bit;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the packet/event was sniffed (not blocked).
    /// </summary>
    public readonly bool IsSniffed => GetBit(WINDIVERT_ADDRESS_BITS.Sniffed);

    /// <summary>
    /// Gets or sets a value indicating whether the packet/event is outbound.
    /// </summary>
    public bool IsOutbound
    {
        readonly get { return GetBit(WINDIVERT_ADDRESS_BITS.Outbound); }
        set { SetBit(WINDIVERT_ADDRESS_BITS.Outbound, value); }
    }

    /// <summary>
    /// Gets a value indicating whether the packet is loopback.
    /// </summary>
    public readonly bool IsLoopback => GetBit(WINDIVERT_ADDRESS_BITS.Loopback);

    /// <summary>
    /// Gets or sets a value indicating impostor packets.
    /// </summary>
    public bool IsImpostor
    {
        readonly get { return GetBit(WINDIVERT_ADDRESS_BITS.Impostor); }
        set { SetBit(WINDIVERT_ADDRESS_BITS.Impostor, value); }
    }

    /// <summary>
    /// Gets a value indicating whether the packet/event is IPv6.
    /// </summary>
    public readonly bool IsIPv6 => GetBit(WINDIVERT_ADDRESS_BITS.IPv6);

    /// <summary>
    /// Gets or sets a value indicating whether the IP checksum is valid.
    /// </summary>
    public bool IsIPChecksumValid
    {
        readonly get { return GetBit(WINDIVERT_ADDRESS_BITS.IPChecksum); }
        set { SetBit(WINDIVERT_ADDRESS_BITS.IPChecksum, value); }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the TCP checksum is valid.
    /// </summary>
    public bool IsTCPChecksumValid
    {
        readonly get { return GetBit(WINDIVERT_ADDRESS_BITS.TCPChecksum); }
        set { SetBit(WINDIVERT_ADDRESS_BITS.TCPChecksum, value); }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the UDP checksum is valid.
    /// </summary>
    public bool IsUDPChecksumValid
    {
        readonly get { return GetBit(WINDIVERT_ADDRESS_BITS.UDPChecksum); }
        set { SetBit(WINDIVERT_ADDRESS_BITS.UDPChecksum, value); }
    }

    /// <summary>
    /// Gets the network data associated with the event.
    /// </summary>
    /// <returns>
    /// The <see cref="NetworkData"/> associated with the event.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The <see cref="Layer"/> is not <see cref="DivertLayer.Network"/> or <see cref="DivertLayer.Forward"/>.
    /// </exception>
    public NetworkData GetNetworkData()
    {
        return Layer switch
        {
            DivertLayer.Network or DivertLayer.Forward => new NetworkData
            {
                InterfaceIndex = address.Network.IfIdx,
                SubInterfaceIndex = address.Network.SubIfIdx,
            },
            _ => throw new InvalidOperationException($"{nameof(Layer)}: {Layer}"),
        };
    }

    private readonly IPAddress GetIPAddress(Span<byte> bytes)
    {
        Span<byte> beBytes = stackalloc byte[bytes.Length];
        bytes.CopyTo(beBytes);
        beBytes.Reverse();
        var address = new IPAddress(beBytes);
        if (!IsIPv6)
        {
            address = address.MapToIPv4();
        }
        return address;
    }

    /// <summary>
    /// Gets the flow data associated with the event.
    /// </summary>
    /// <returns>
    /// The <see cref="FlowData"/> associated with the event.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The <see cref="Layer"/> is not <see cref="DivertLayer.Flow"/>.
    /// </exception>
    public FlowData GetFlowData()
    {
        switch (Layer)
        {
            case DivertLayer.Flow:
                var flow = address.Flow;
                return new FlowData
                {
                    EndpointId = address.Flow.EndpointId,
                    ParentEndpointId = address.Flow.ParentEndpointId,
                    ProcessId = address.Flow.ProcessId,
                    LocalAddress = GetIPAddress(new Span<byte>(flow.LocalAddr, 16)),
                    RemoteAddress = GetIPAddress(new Span<byte>(flow.RemoteAddr, 16)),
                    LocalPort = address.Flow.LocalPort,
                    RemotePort = address.Flow.RemotePort,
                    Protocol = address.Flow.Protocol,
                };
            default:
                throw new InvalidOperationException($"{nameof(Layer)}: {Layer}");
        }
    }

    /// <summary>
    /// Gets the socket data associated with the event.
    /// </summary>
    /// <returns>
    /// The <see cref="SocketData"/> associated with the event.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The <see cref="Layer"/> is not <see cref="DivertLayer.Socket"/>.
    /// </exception>
    public SocketData GetSocketData()
    {
        switch (Layer)
        {
            case DivertLayer.Socket:
                var socket = address.Socket;
                return new SocketData
                {
                    EndpointId = address.Socket.EndpointId,
                    ParentEndpointId = address.Socket.ParentEndpointId,
                    ProcessId = address.Socket.ProcessId,
                    LocalAddress = GetIPAddress(new Span<byte>(socket.LocalAddr, 16)),
                    RemoteAddress = GetIPAddress(new Span<byte>(socket.RemoteAddr, 16)),
                    LocalPort = address.Socket.LocalPort,
                    RemotePort = address.Socket.RemotePort,
                    Protocol = address.Socket.Protocol,
                };
            default:
                throw new InvalidOperationException($"{nameof(Layer)}: {Layer}");
        }
    }

    /// <summary>
    /// Gets the reflect data associated with the event.
    /// </summary>
    /// <returns>
    /// The <see cref="ReflectData"/> associated with the event.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The <see cref="Layer"/> is not <see cref="DivertLayer.Reflect"/>.
    /// </exception>
    public ReflectData GetReflectData()
    {
        return Layer switch
        {
            DivertLayer.Reflect => new ReflectData
            {
                Timestamp = address.Reflect.Timestamp,
                ProcessId = address.Reflect.ProcessId,
                Layer = (DivertLayer)address.Reflect.Layer,
                Flags = (DivertFlags)address.Reflect.Flags,
                Priority = address.Reflect.Priority,
            },
            _ => throw new InvalidOperationException($"{nameof(Layer)}: {Layer}"),
        };
    }
}
