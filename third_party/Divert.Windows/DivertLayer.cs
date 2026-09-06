namespace Divert.Windows;

/// <summary>
/// Specifies the WinDivert layer.
/// </summary>
public enum DivertLayer
{
    /// <summary>
    /// Network packets to/from the local machine.
    /// </summary>
    Network = WINDIVERT_LAYER.WINDIVERT_LAYER_NETWORK,

    /// <summary>
    /// Network packets being forwarded through the local machine.
    /// </summary>
    Forward = WINDIVERT_LAYER.WINDIVERT_LAYER_NETWORK_FORWARD,

    /// <summary>
    /// Network flow events.
    /// </summary>
    Flow = WINDIVERT_LAYER.WINDIVERT_LAYER_FLOW,

    /// <summary>
    /// Socket events.
    /// </summary>
    Socket = WINDIVERT_LAYER.WINDIVERT_LAYER_SOCKET,

    /// <summary>
    /// WinDivert handle events.
    /// </summary>
    Reflect = WINDIVERT_LAYER.WINDIVERT_LAYER_REFLECT,
}
