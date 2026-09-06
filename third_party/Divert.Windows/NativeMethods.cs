using System.Runtime.InteropServices;

namespace Divert.Windows;

internal static unsafe partial class NativeMethods
{
    private const string dllName = "WinDivert.dll";

    [LibraryImport(dllName, SetLastError = true)]
    public static partial IntPtr WinDivertOpen(IntPtr filter, WINDIVERT_LAYER layer, short priority, ulong flags);

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertRecv(
        IntPtr handle,
        void* pPacket,
        uint packetLen,
        uint* pRecvLen,
        WINDIVERT_ADDRESS* pAddr
    );

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertRecvEx(
        IntPtr handle,
        void* pPacket,
        uint packetLen,
        uint* pRecvLen,
        ulong flags,
        WINDIVERT_ADDRESS* pAddr,
        uint* pAddrLen,
        NativeOverlapped* lpOverlapped
    );

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertSend(
        IntPtr handle,
        void* pPacket,
        uint packetLen,
        uint* pSendLen,
        WINDIVERT_ADDRESS* pAddr
    );

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertSendEx(
        IntPtr handle,
        void* pPacket,
        uint packetLen,
        uint* pSendLen,
        ulong flags,
        WINDIVERT_ADDRESS* pAddr,
        uint addrLen,
        NativeOverlapped* lpOverlapped
    );

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertShutdown(IntPtr handle, WINDIVERT_SHUTDOWN how);

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertClose(IntPtr handle);

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertSetParam(IntPtr handle, WINDIVERT_PARAM param, ulong value);

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertGetParam(IntPtr handle, WINDIVERT_PARAM param, ulong* pValue);

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertHelperCalcChecksums(
        void* pPacket,
        uint packetLen,
        WINDIVERT_ADDRESS* pAddr,
        ulong flags
    );

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertHelperDecrementTTL(void* pPacket, uint packetLen);

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertHelperCompileFilter(
        IntPtr filter,
        WINDIVERT_LAYER layer,
        byte* @object,
        uint objLen,
        IntPtr* errorStr,
        uint* errorPos
    );

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertHelperFormatFilter(
        IntPtr filter,
        WINDIVERT_LAYER layer,
        byte* buffer,
        uint bufLen
    );

    [LibraryImport(dllName, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinDivertHelperEvalFilter(
        IntPtr filter,
        void* pPacket,
        uint packetLen,
        WINDIVERT_ADDRESS* pAddr
    );
}
