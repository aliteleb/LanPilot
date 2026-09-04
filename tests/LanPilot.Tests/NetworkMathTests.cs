using System.Net;
using LanPilot.Service.Engine;

namespace LanPilot.Tests;

public sealed class NetworkMathTests
{
    [Fact]
    public void EnumerateHosts_StaysInsideSlash24()
    {
        IReadOnlyList<IPAddress> hosts = NetworkMath.EnumerateHosts(IPAddress.Parse("192.168.7.44"), 24);

        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.7.1", hosts[0].ToString());
        Assert.Equal("192.168.7.254", hosts[^1].ToString());
    }

    [Fact]
    public void EnumerateHosts_RejectsLargeNetworks()
    {
        Assert.Throws<NotSupportedException>(() =>
            NetworkMath.EnumerateHosts(IPAddress.Parse("10.65.11.4"), 16));
    }

    [Fact]
    public void PrefixLength_RejectsNonContiguousMask()
    {
        Assert.Throws<ArgumentException>(() =>
            NetworkMath.GetPrefixLength(IPAddress.Parse("255.0.255.0")));
    }

    [Fact]
    public void DeviceIdentity_IsStableForNetworkAndMac()
    {
        string first = NetworkScanner.BuildDeviceId("home", "AA:BB:CC:DD:EE:FF");
        string second = NetworkScanner.BuildDeviceId("home", "aa:bb:cc:dd:ee:ff");

        Assert.Equal(first, second);
        Assert.NotEqual(first, NetworkScanner.BuildDeviceId("office", "AA:BB:CC:DD:EE:FF"));
    }
}
