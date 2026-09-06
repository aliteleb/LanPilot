namespace Divert.Windows;

/// <summary>
/// Types of Divert events.
/// </summary>
public enum DivertEvent
{
    /// <summary>
    /// A network packet.
    /// </summary>
    NetworkPacket = WINDIVERT_EVENT.WINDIVERT_EVENT_NETWORK_PACKET,

    /// <summary>
    /// A flow has been established.
    /// </summary>
    FlowEstablished = WINDIVERT_EVENT.WINDIVERT_EVENT_FLOW_ESTABLISHED,

    /// <summary>
    /// A flow has been deleted.
    /// </summary>
    FlowDeleted = WINDIVERT_EVENT.WINDIVERT_EVENT_FLOW_DELETED,

    /// <summary>
    /// A bind() socket operation.
    /// </summary>
    SocketBind = WINDIVERT_EVENT.WINDIVERT_EVENT_SOCKET_BIND,

    /// <summary>
    /// A connect() socket operation.
    /// </summary>
    SocketConnect = WINDIVERT_EVENT.WINDIVERT_EVENT_SOCKET_CONNECT,

    /// <summary>
    /// A listen() socket operation.
    /// </summary>
    SocketListen = WINDIVERT_EVENT.WINDIVERT_EVENT_SOCKET_LISTEN,

    /// <summary>
    /// An accept() socket operation.
    /// </summary>
    SocketAccept = WINDIVERT_EVENT.WINDIVERT_EVENT_SOCKET_ACCEPT,

    /// <summary>
    /// A socket is unbound or a connection is closed.
    /// </summary>
    SocketClose = WINDIVERT_EVENT.WINDIVERT_EVENT_SOCKET_CLOSE,

    /// <summary>
    /// A new WinDivert handle was opened.
    /// </summary>
    ReflectOpen = WINDIVERT_EVENT.WINDIVERT_EVENT_REFLECT_OPEN,

    /// <summary>
    /// A WinDivert handle was closed.
    /// </summary>
    ReflectClose = WINDIVERT_EVENT.WINDIVERT_EVENT_REFLECT_CLOSE,
}
