using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LanPilot.Contracts;

namespace LanPilot.Service.Engine;

public sealed class NetworkScanner(ILogger<NetworkScanner> logger)
{
    public async Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync(
        string? selectedAdapterId,
        CancellationToken cancellationToken)
    {
        List<NetworkAdapterInfo> result = [];
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(IsSupportedAdapter))
        {
            IPInterfaceProperties properties = nic.GetIPProperties();
            UnicastIPAddressInformation? unicast = properties.UnicastAddresses
                .FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork && item.IPv4Mask is not null);
            GatewayIPAddressInformation? gateway = properties.GatewayAddresses
                .FirstOrDefault(item => item.Address.AddressFamily == AddressFamily.InterNetwork);
            if (unicast is null || gateway is null)
            {
                continue;
            }

            int prefix = NetworkMath.GetPrefixLength(unicast.IPv4Mask);
            string? gatewayMac = await ResolveMacAsync(gateway.Address, cancellationToken, unicast.Address);
            result.Add(new NetworkAdapterInfo(
                nic.Id,
                nic.Name,
                nic.Description,
                unicast.Address.ToString(),
                prefix,
                gateway.Address.ToString(),
                gatewayMac,
                nic.Speed,
                nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                string.Equals(nic.Id, selectedAdapterId, StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> ScanAsync(
        NetworkAdapterInfo adapter,
        NetworkProfile profile,
        IReadOnlyDictionary<string, DeviceSnapshot> existing,
        CancellationToken cancellationToken)
    {
        IPAddress localAddress = IPAddress.Parse(adapter.Ipv4Address);
        IReadOnlyList<IPAddress> hosts = NetworkMath.EnumerateHosts(localAddress, adapter.PrefixLength);
        const int probeConcurrency = 96;
        ThreadPool.GetMinThreads(out int minimumWorkers, out int minimumIo);
        if (minimumWorkers < probeConcurrency)
        {
            ThreadPool.SetMinThreads(probeConcurrency, minimumIo);
        }
        using SemaphoreSlim gate = new(probeConcurrency);
        List<DeviceSnapshot> found = [];
        object sync = new();

        IEnumerable<Task> probes = hosts.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                string? mac = await ResolveMacAsync(address, cancellationToken, localAddress);
                if (mac is null) return;

                bool isLocal = address.Equals(localAddress);
                bool isGateway = address.ToString() == adapter.GatewayAddress;
                string id = BuildDeviceId(profile.Id, mac);
                existing.TryGetValue(id, out DeviceSnapshot? previous);
                string? hostName = previous?.HostName;
                if (hostName is null)
                {
                    hostName = await ResolveHostNameAsync(address, cancellationToken);
                }

                DateTimeOffset now = DateTimeOffset.Now;
                DevicePolicy policy = previous?.Policy ??
                    new DevicePolicy(id, false, null, null, DevicePriority.Normal, null);
                DeviceSnapshot device = new(
                    id,
                    profile.Id,
                    mac,
                    address.ToString(),
                    previous?.DisplayName ?? hostName ?? (isGateway ? "Router" : isLocal ? "This PC" : $"Device {mac[^5..]}"),
                    hostName,
                    previous?.DeviceType ?? (isGateway ? "Router" : isLocal ? "Computer" : "Unknown"),
                    previous?.GroupId,
                    true,
                    isGateway,
                    isLocal,
                    previous?.FirstSeen ?? now,
                    now,
                    previous?.DownloadBitsPerSecond ?? 0,
                    previous?.UploadBitsPerSecond ?? 0,
                    previous?.TotalDownloadBytes ?? 0,
                    previous?.TotalUploadBytes ?? 0,
                    policy);

                lock (sync) found.Add(device);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "ARP probe failed for {Address}.", address);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(probes);
        return found.OrderBy(item => IPAddress.Parse(item.Ipv4Address).GetAddressBytes(), ByteArrayComparer.Instance).ToArray();
    }

    public async Task<IReadOnlyList<DeviceSnapshot>> ProbeKnownDevicesAsync(
        IReadOnlyDictionary<string, DeviceSnapshot> existing,
        CancellationToken cancellationToken,
        IPAddress? localAddress = null)
    {
        DeviceSnapshot[] candidates = existing.Values
            .Where(item => !item.IsGateway && !item.IsLocalComputer)
            .ToArray();
        List<DeviceSnapshot> online = [];
        object sync = new();

        await Task.WhenAll(candidates.Select(async device =>
        {
            if (!IPAddress.TryParse(device.Ipv4Address, out IPAddress? address)) return;
            string? resolvedMac = await ResolveMacAsync(address, cancellationToken, localAddress);
            if (!string.Equals(NormalizeMac(resolvedMac), NormalizeMac(device.MacAddress), StringComparison.OrdinalIgnoreCase)) return;

            lock (sync)
            {
                online.Add(device with { IsOnline = true, LastSeen = DateTimeOffset.Now });
            }
        }));

        return online;
    }

    public static NetworkProfile BuildProfile(NetworkAdapterInfo adapter, NetworkProfile? existing = null)
    {
        string networkAddress = NetworkMath.ToCidr(IPAddress.Parse(adapter.Ipv4Address), adapter.PrefixLength);
        string identity = $"{adapter.Id}|{adapter.GatewayMac ?? adapter.GatewayAddress}|{networkAddress}";
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        DateTimeOffset now = DateTimeOffset.Now;
        return new NetworkProfile(
            id,
            adapter.Id,
            existing?.Name ?? adapter.Name,
            networkAddress,
            adapter.GatewayAddress,
            adapter.GatewayMac,
            existing?.AutoControl ?? false,
            existing?.FirstSeen ?? now,
            now);
    }

    public static string BuildDeviceId(string networkId, string mac) =>
        $"{networkId}:{mac.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant()}";

    private static string? NormalizeMac(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new string(value.Where(Uri.IsHexDigit).ToArray());

    public static async Task<string?> ResolveMacAsync(IPAddress address, CancellationToken cancellationToken, IPAddress? localAddress = null)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] buffer = new byte[6];
            int length = buffer.Length;
            int result = SendARP(BitConverter.ToUInt32(address.GetAddressBytes()),
                localAddress is null ? 0 : BitConverter.ToUInt32(localAddress.GetAddressBytes()), buffer, ref length);
            return result == 0 && length >= 6
                ? string.Join(':', buffer.Take(6).Select(value => value.ToString("X2")))
                : null;
        }, cancellationToken);
    }

    private static async Task<string?> ResolveHostNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            IPHostEntry entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken)
                .WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
            return entry.HostName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSupportedAdapter(NetworkInterface nic) =>
        nic.OperationalStatus == OperationalStatus.Up &&
        nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
        !nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase) &&
        !nic.Description.Contains("vpn", StringComparison.OrdinalIgnoreCase);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destinationIp, uint sourceIp, byte[] macAddress, ref int physicalAddressLength);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                int value = left[index].CompareTo(right[index]);
                if (value != 0) return value;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
