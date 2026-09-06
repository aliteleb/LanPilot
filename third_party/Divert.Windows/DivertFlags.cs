namespace Divert.Windows;

/// <summary>
/// Flags for configuring the behavior of the WinDivert handle.
/// </summary>
[Flags]
public enum DivertFlags
{
    /// <summary>
    /// Default mode: packets are dropped and captured.
    /// </summary>
    None = 0,

    /// <summary>
    /// Packet sniffing mode: packets are captured but not dropped.
    /// </summary>
    Sniff = 0x0001,

    /// <summary>
    /// Packets are silently dropped.
    /// </summary>
    Drop = 0x0002,

    /// <summary>
    /// Receive-only mode: disables sending packets.
    /// </summary>
    ReceiveOnly = 0x0004,

    /// <summary>
    /// Same as <see cref="ReceiveOnly"/>.
    /// </summary>
    ReadOnly = ReceiveOnly,

    /// <summary>
    /// Send-only mode: disables receiving packets.
    /// </summary>
    SendOnly = 0x0008,

    /// <summary>
    /// Same as <see cref="SendOnly"/>.
    /// </summary>
    WriteOnly = SendOnly,

    /// <summary>
    /// Prevents automatic installation of the WinDivert driver.
    /// </summary>
    NoInstall = 0x0010,

    /// <summary>
    /// Captures fragmented packets.
    /// </summary>
    Fragments = 0x0020,
}
