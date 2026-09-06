using System.Runtime.InteropServices;

namespace Divert.Windows;

internal sealed class CString(string str) : IDisposable
{
    internal IntPtr Pointer { get; } = Marshal.StringToHGlobalAnsi(str);

    private bool disposed;

    public void Dispose()
    {
        if (!disposed)
        {
            Marshal.FreeHGlobal(Pointer);
            disposed = true;
        }
    }
}
