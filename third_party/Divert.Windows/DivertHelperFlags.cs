namespace Divert.Windows;

/// <summary>
/// Flags to disable individual checksum calculations.
/// </summary>
[Flags]
public enum DivertHelperFlags
{
    /// <summary>
    /// No flags specified.
    /// </summary>
    None = 0,

    /// <summary>
    /// Disables IP checksum calculation.
    /// </summary>
    NoIPChecksum = 1,

    /// <summary>
    /// Disables ICMP checksum calculation.
    /// </summary>
    NoICMPChecksum = 2,

    /// <summary>
    /// Disables ICMPv6 checksum calculation.
    /// </summary>
    NoICMPv6Checksum = 4,

    /// <summary>
    /// Disables TCP checksum calculation.
    /// </summary>
    NoTCPChecksum = 8,

    /// <summary>
    /// Disables UDP checksum calculation.
    /// </summary>
    NoUDPChecksum = 16,
}
