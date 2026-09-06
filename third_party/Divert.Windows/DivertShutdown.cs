namespace Divert.Windows;

/// <summary>
/// Specifies how to shut down a WinDivert handle.
/// </summary>
public enum DivertShutdown
{
    /// <summary>
    /// Stops new packets from being queued for receiving.
    /// </summary>
    Receive = WINDIVERT_SHUTDOWN.WINDIVERT_SHUTDOWN_RECV,

    /// <summary>
    /// Stops new packets from being injected.
    /// </summary>
    Send = WINDIVERT_SHUTDOWN.WINDIVERT_SHUTDOWN_SEND,

    /// <summary>
    /// Stops both receiving and sending of packets.
    /// </summary>
    Both = WINDIVERT_SHUTDOWN.WINDIVERT_SHUTDOWN_BOTH,
}
