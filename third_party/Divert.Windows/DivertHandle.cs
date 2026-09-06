using Microsoft.Win32.SafeHandles;

namespace Divert.Windows;

/// <summary>
/// Safe handle for a WinDivert handle.
/// </summary>
public sealed class DivertHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Creates a new instance of the <see cref="DivertHandle"/> class.
    /// </summary>
    /// <param name="handle">
    /// The WinDivert handle.
    /// </param>
    /// <param name="ownsHandle">
    /// Whether the handle should be released when the SafeHandle is disposed.
    /// </param>
    public DivertHandle(IntPtr handle, bool ownsHandle = true)
        : base(ownsHandle)
    {
        this.handle = handle;
    }

    /// <summary>
    /// Releases the WinDivert handle.
    /// </summary>
    /// <returns>
    /// true if the handle was released successfully; otherwise, false.
    /// </returns>
    protected override bool ReleaseHandle() => NativeMethods.WinDivertClose(handle);
}
