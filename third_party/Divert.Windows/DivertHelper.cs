using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;

namespace Divert.Windows;

/// <summary>
/// WinDivert helper methods.
/// </summary>
public static unsafe class DivertHelper
{
    /// <summary>
    /// Calculates the checksums for the specified packet.
    /// </summary>
    /// <param name="packet">
    /// The packet data.
    /// </param>
    /// <param name="flags">The flags to disable individual checksum calculations.</param>
    /// <returns>true if the checksums were calculated successfully; otherwise, false.</returns>
    public static bool CalculateChecksums(Span<byte> packet, DivertHelperFlags flags = DivertHelperFlags.None)
    {
        fixed (byte* pPacket = packet)
        {
            return NativeMethods.WinDivertHelperCalcChecksums(pPacket, (uint)packet.Length, null, (ulong)flags);
        }
    }

    /// <summary>
    /// Calculates the checksums for the specified packet.
    /// </summary>
    /// <param name="packet">
    /// The packet data.
    /// </param>
    /// <param name="address">
    /// A reference to a <see cref="DivertAddress"/> structure where the corresponding ChecksumValid fields will be set.
    /// </param>
    /// <param name="flags">The flags to disable individual checksum calculations.</param>
    /// <returns>true if the checksums were calculated successfully; otherwise, false.</returns>
    public static bool CalculateChecksums(
        Span<byte> packet,
        ref DivertAddress address,
        DivertHelperFlags flags = DivertHelperFlags.None
    )
    {
        fixed (byte* pPacket = packet)
        fixed (DivertAddress* pAddress = &address)
        {
            return NativeMethods.WinDivertHelperCalcChecksums(
                pPacket,
                (uint)packet.Length,
                (WINDIVERT_ADDRESS*)pAddress,
                (ulong)flags
            );
        }
    }

    /// <summary>
    /// Decrements the TTL field in the IP header of the specified packet.
    /// </summary>
    /// <param name="packet">The packet data.</param>
    /// <returns>true if the result is non-zero; otherwise, false.</returns>
    public static bool DecrementTtl(Span<byte> packet)
    {
        fixed (byte* pPacket = packet)
        {
            return NativeMethods.WinDivertHelperDecrementTTL(pPacket, (uint)packet.Length);
        }
    }

    /// <summary>
    /// Compiles a WinDivert filter string into a compact object representation.
    /// </summary>
    /// <param name="filter">
    /// The filter to compile.
    /// </param>
    /// <param name="layer">The layer.</param>
    /// <param name="bufferLength">The length of the buffer.</param>
    /// <returns>The compiled filter.</returns>
    /// <exception cref="ArgumentException">
    /// The filter is invalid.
    /// </exception>
    public static ReadOnlySpan<byte> CompileFilter(
        DivertFilter filter,
        DivertLayer layer,
        int bufferLength = ushort.MaxValue
    )
    {
        ArgumentNullException.ThrowIfNull(filter);

        using var s = new CString(filter.Clause);
        Span<byte> buffer = GC.AllocateArray<byte>(bufferLength, pinned: true);
        var pBuffer = Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
        IntPtr errorStr;
        uint errorPos;
        bool success = NativeMethods.WinDivertHelperCompileFilter(
            s.Pointer,
            (WINDIVERT_LAYER)layer,
            (byte*)pBuffer,
            (uint)buffer.Length,
            &errorStr,
            &errorPos
        );
        if (!success)
        {
            string? errorString = Marshal.PtrToStringAnsi(errorStr);
            throw new ArgumentException(
                $"{errorString} ({errorPos}): ...{filter.Clause[(int)errorPos..]}",
                nameof(filter)
            );
        }

        return buffer;
    }

    private static bool EvaluateFilter(IntPtr filter, ReadOnlySpan<byte> packet, in DivertAddress address)
    {
        fixed (byte* pPacket = packet)
        fixed (DivertAddress* pAddress = &address)
        {
            bool success = NativeMethods.WinDivertHelperEvalFilter(
                filter,
                pPacket,
                (uint)packet.Length,
                (WINDIVERT_ADDRESS*)pAddress
            );
            if (!success)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error is not (int)WIN32_ERROR.ERROR_SUCCESS)
                {
                    throw new Win32Exception(error);
                }
            }
            return success;
        }
    }

    /// <summary>
    /// Evaluates a compiled WinDivert filter against the specified packet and address.
    /// </summary>
    /// <param name="filter">The compiled filter.</param>
    /// <param name="packet">The packet data.</param>
    /// <param name="address">The address information.</param>
    /// <returns>true if the packet matches the filter; otherwise, false.</returns>
    public static bool EvaluateFilter(ReadOnlySpan<byte> filter, ReadOnlySpan<byte> packet, in DivertAddress address)
    {
        fixed (byte* pFilter = filter)
        fixed (byte* pPacket = packet)
        fixed (DivertAddress* pAddress = &address)
        {
            return EvaluateFilter(new IntPtr(pFilter), packet, address);
        }
    }

    /// <summary>
    /// Evaluates a WinDivert filter string against the specified packet and address.
    /// </summary>
    /// <param name="filter">The filter to evaluate.</param>
    /// <param name="packet">The packet data.</param>
    /// <param name="address">The address information.</param>
    /// <returns>true if the packet matches the filter; otherwise, false.</returns>
    public static bool EvaluateFilter(DivertFilter filter, ReadOnlySpan<byte> packet, in DivertAddress address)
    {
        using var s = new CString(filter.Clause);
        fixed (byte* pPacket = packet)
        fixed (DivertAddress* pAddress = &address)
        {
            return EvaluateFilter(s.Pointer, packet, address);
        }
    }

    /// <summary>
    /// Formats a compiled WinDivert filter into a human-readable string.
    /// </summary>
    /// <param name="filter">The compiled filter.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="maxLength">The maximum length of the formatted string.</param>
    /// <returns>The formatted filter string.</returns>
    public static string FormatFilter(Span<byte> filter, DivertLayer layer, int maxLength = ushort.MaxValue)
    {
        Span<byte> buffer = GC.AllocateArray<byte>(maxLength, pinned: true);
        var pBuffer = Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
        fixed (byte* pFilter = filter)
        {
            bool success = NativeMethods.WinDivertHelperFormatFilter(
                new(pFilter),
                (WINDIVERT_LAYER)layer,
                (byte*)pBuffer,
                (uint)buffer.Length
            );
            if (!success)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return Marshal.PtrToStringAnsi(new IntPtr(pBuffer))!;
        }
    }
}
