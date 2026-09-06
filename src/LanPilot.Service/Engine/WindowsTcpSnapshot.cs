using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace LanPilot.Service.Engine;

internal static class WindowsTcpSnapshot
{
    // TCP_TABLE_OWNER_PID_CONNECTIONS; Windows documents 24/56-byte DWORD-aligned rows.
    internal static IReadOnlyDictionary<ApplicationDownloadLimiter.FlowKey, string> Read(Func<uint, string?> resolve)
    {
        Dictionary<ApplicationDownloadLimiter.FlowKey, string> result = [];
        foreach (int family in new[] { 2, 23 })
        {
            int size = 0;
            uint status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 4, 0);
            if (status != 122 && status != 0) throw new Win32Exception((int)status);
            for (int attempt = 0; attempt < 3 && size >= 4 && size <= 4 * 1024 * 1024; attempt++)
            {
                int allocated = size;
                IntPtr buffer = Marshal.AllocHGlobal(allocated);
                try
                {
                    status = GetExtendedTcpTable(buffer, ref size, false, family, 4, 0);
                    if (status == 122) continue;
                    if (status != 0) throw new Win32Exception((int)status);
                    byte[] bytes = new byte[Math.Min(allocated, size)];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    Parse(bytes, family == 23, resolve, result);
                    break;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (status != 0) throw new InvalidDataException("TCP identity snapshot exceeded its bounded buffer.");
        }
        return result;
    }

    internal static void Parse(ReadOnlySpan<byte> bytes, bool ipv6, Func<uint, string?> resolve,
        Dictionary<ApplicationDownloadLimiter.FlowKey, string> result)
    {
        if (bytes.Length < 4) throw new InvalidDataException("Truncated TCP table.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        int rowSize = ipv6 ? 56 : 24;
        if (count > 65536 || count > (bytes.Length - 4) / rowSize) throw new InvalidDataException("Invalid TCP row count.");
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> row = bytes.Slice(4 + i * rowSize, rowSize);
            uint pid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(ipv6 ? 52 : 20));
            IPAddress local = new(row.Slice(ipv6 ? 0 : 4, ipv6 ? 16 : 4));
            IPAddress remote = new(row.Slice(ipv6 ? 24 : 12, ipv6 ? 16 : 4));
            // Link-local IPv6 requires an interface scope that packet tuples don't carry.
            if (local.IsIPv6LinkLocal || remote.IsIPv6LinkLocal) continue;
            string? id = resolve(pid);
            if (id is null) continue;
            ushort localPort = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(ipv6 ? 20 : 8));
            ushort remotePort = BinaryPrimitives.ReadUInt16BigEndian(row.Slice(ipv6 ? 44 : 16));
            result[new(local, remote, localPort, remotePort, 6)] = id;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool ordered,
        int family, int tableClass, uint reserved);
}
