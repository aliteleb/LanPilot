using System.Net;

namespace LanPilot.Service.Engine;

public static class NetworkMath
{
    public static int GetPrefixLength(IPAddress mask)
    {
        byte[] bytes = mask.GetAddressBytes();
        int prefix = 0;
        bool sawZero = false;
        foreach (byte value in bytes)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool set = (value & (1 << bit)) != 0;
                if (set && sawZero)
                {
                    throw new ArgumentException("The IPv4 subnet mask is not contiguous.", nameof(mask));
                }

                sawZero |= !set;
                prefix += set ? 1 : 0;
            }
        }

        return prefix;
    }

    public static IPAddress GetNetworkAddress(IPAddress address, int prefixLength)
    {
        ValidatePrefix(prefixLength);
        byte[] bytes = address.GetAddressBytes();
        uint value = ToUInt32(bytes);
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return FromUInt32(value & mask);
    }

    public static IReadOnlyList<IPAddress> EnumerateHosts(IPAddress address, int prefixLength, int maximumHosts = 254)
    {
        ValidatePrefix(prefixLength);
        if (prefixLength < 24)
        {
            throw new NotSupportedException("LanPilot 0.1 supports IPv4 networks from /24 through /30.");
        }

        uint network = ToUInt32(GetNetworkAddress(address, prefixLength).GetAddressBytes());
        int hostCount = checked((int)((1L << (32 - prefixLength)) - 2));
        hostCount = Math.Min(hostCount, maximumHosts);

        List<IPAddress> result = new(hostCount);
        for (uint offset = 1; offset <= hostCount; offset++)
        {
            result.Add(FromUInt32(network + offset));
        }

        return result;
    }

    public static string ToCidr(IPAddress address, int prefixLength) =>
        $"{GetNetworkAddress(address, prefixLength)}/{prefixLength}";

    private static uint ToUInt32(byte[] bytes)
    {
        if (bytes.Length != 4)
        {
            throw new ArgumentException("Only IPv4 addresses are supported.");
        }

        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    private static void ValidatePrefix(int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }
    }
}
