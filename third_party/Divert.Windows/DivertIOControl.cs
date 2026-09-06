using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Divert.Windows;

/// <summary>
/// Replaces <see cref="NativeMethods.WinDivertSetParam"/> and <see cref="NativeMethods.WinDivertGetParam"/> as they
/// are not applicable to thread pool bound handles.
/// </summary>
internal static unsafe class DivertIOControl
{
    private static uint CTL_CODE(uint deviceType, uint function, uint method, uint access)
    {
        return (deviceType << 16) | (access << 14) | (function << 2) | method;
    }

    private static readonly uint SetParamControlCode = CTL_CODE(
        PInvoke.FILE_DEVICE_NETWORK,
        0x925,
        PInvoke.METHOD_IN_DIRECT,
        (uint)FileAccess.ReadWrite
    );

    private static readonly uint GetParamControlCode = CTL_CODE(
        PInvoke.FILE_DEVICE_NETWORK,
        0x926,
        PInvoke.METHOD_OUT_DIRECT,
        (uint)FileAccess.Read
    );

    private static void ManualResetCallback(uint errorCode, uint numBytes, NativeOverlapped* pOVERLAP)
    {
        var manualResetEvent = (ManualResetEventSlim)ThreadPoolBoundHandle.GetNativeOverlappedState(pOVERLAP)!;
        manualResetEvent.Set();
    }

    private static readonly IOCompletionCallback manualResetCallback = ManualResetCallback;

    private static ulong DeviceIOControl(ThreadPoolBoundHandle threadPoolBoundHandle, WINDIVERT_IOCTL* ioctl, uint code)
    {
        ulong value;
        using var eventHandle = new ManualResetEventSlim(initialState: false);
        var nativeOverlapped = threadPoolBoundHandle.AllocateNativeOverlapped(manualResetCallback, eventHandle, null);
        try
        {
            using var _ = threadPoolBoundHandle.Handle.DangerousGetHandle(out var handle);
            bool success = PInvoke.DeviceIoControl(
                new HANDLE(handle),
                code,
                ioctl,
                (uint)sizeof(WINDIVERT_IOCTL),
                &value,
                sizeof(ulong),
                null,
                nativeOverlapped
            );
            if (!success)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is not (int)WIN32_ERROR.ERROR_IO_PENDING)
                {
                    throw new Win32Exception(error);
                }
                eventHandle.Wait();
            }
        }
        finally
        {
            threadPoolBoundHandle.FreeNativeOverlapped(nativeOverlapped);
        }

        return value;
    }

    public static void SetParam(ThreadPoolBoundHandle threadPoolBoundHandle, WINDIVERT_PARAM param, ulong value)
    {
        var ioctl = new WINDIVERT_IOCTL { SetParam = param, Value = value };
        DeviceIOControl(threadPoolBoundHandle, &ioctl, SetParamControlCode);
    }

    public static ulong GetParam(ThreadPoolBoundHandle threadPoolBoundHandle, WINDIVERT_PARAM param)
    {
        var ioctl = new WINDIVERT_IOCTL { GetParam = param };
        return DeviceIOControl(threadPoolBoundHandle, &ioctl, GetParamControlCode);
    }
}
