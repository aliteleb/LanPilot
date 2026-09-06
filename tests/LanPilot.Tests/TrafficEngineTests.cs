using System.Net;
using System.Net.NetworkInformation;
using LanPilot.Service.Engine;

namespace LanPilot.Tests;

public sealed class TrafficEngineTests
{
    [Fact]
    public void BuildArpReply_WritesExpectedEthernetAndArpFields()
    {
        PhysicalAddress target = PhysicalAddress.Parse("102030405060");
        PhysicalAddress source = PhysicalAddress.Parse("AABBCCDDEEFF");

        byte[] frame = TrafficEngine.BuildArpReply(
            target, source, IPAddress.Parse("192.168.1.1"), target, IPAddress.Parse("192.168.1.22"));

        Assert.Equal(42, frame.Length);
        Assert.Equal([0x08, 0x06], frame[12..14]);
        Assert.Equal([0x00, 0x02], frame[20..22]);
        Assert.Equal(source.GetAddressBytes(), frame[22..28]);
        Assert.Equal(IPAddress.Parse("192.168.1.1").GetAddressBytes(), frame[28..32]);
    }

    [Fact]
    public void RateLimiter_RejectsZeroButAllowsUnlimited()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimiter(0));
        Assert.True(new RateLimiter(null).TryConsume(64_000));
    }

    [Fact]
    public void IsUploadFrame_UsesGatewayAsDownloadSource()
    {
        PhysicalAddress gateway = PhysicalAddress.Parse("AABBCCDDEEFF");
        PhysicalAddress device = PhysicalAddress.Parse("102030405060");

        Assert.False(TrafficEngine.IsUploadFrame(gateway, gateway));
        Assert.True(TrafficEngine.IsUploadFrame(device, gateway));
    }

    [Theory]
    [InlineData(true, "192.0.2.20", "198.51.100.1", "102030405060", true)]
    [InlineData(false, "198.51.100.1", "192.0.2.20", "AABBCCDDEEFF", true)]
    [InlineData(false, "198.51.100.1", "192.0.2.10", "AABBCCDDEEFF", false)]
    [InlineData(true, "192.0.2.20", "192.0.2.10", "102030405060", false)]
    [InlineData(true, "192.0.2.21", "198.51.100.1", "102030405060", false)]
    [InlineData(true, "192.0.2.20", "198.51.100.1", "112233445566", false)]
    [InlineData(false, "198.51.100.1", "192.0.2.20", "112233445566", false)]
    public void ForwardingRequiresMatchingPeerAndNeverReinjectsLocalTraffic(bool upload, string source, string destination, string mac, bool expected)
    {
        Assert.Equal(expected, TrafficEngine.CanForwardDevicePacket(upload, IPAddress.Parse(source), IPAddress.Parse(destination),
            PhysicalAddress.Parse(mac), IPAddress.Parse("192.0.2.20"), PhysicalAddress.Parse("102030405060"),
            PhysicalAddress.Parse("AABBCCDDEEFF"), new HashSet<string> { "192.0.2.10" }));
    }
}
