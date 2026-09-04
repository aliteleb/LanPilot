using LanPilot.Contracts;
using LanPilot.Service.Engine;
using LanPilot.Service.Persistence;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Text.Json;

namespace LanPilot.Tests;

public sealed class ApplicationTrafficControllerTests
{
    [Fact]
    public void CreateId_IsStableAndCaseInsensitive()
    {
        string executable = Environment.ProcessPath!;

        Assert.Equal(
            ApplicationTrafficController.CreateId(executable),
            ApplicationTrafficController.CreateId(executable.ToUpperInvariant()));
    }

    [Fact]
    public void Validate_RejectsAnIdentityThatDoesNotMatchTheExecutable()
    {
        LocalApplicationPolicy policy = new(
            "invalid-id",
            "Test application",
            Environment.ProcessPath!,
            1_000_000,
            false);

        Assert.Throws<InvalidDataException>(() => ApplicationTrafficController.Validate(policy));
    }

    [Fact]
    public async Task ApplicationPolicy_RoundTripsAndCanBeDeleted()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            SqliteStore store = new(root);
            await store.InitializeAsync(CancellationToken.None);
            string executable = Environment.ProcessPath!;
            LocalApplicationPolicy expected = new(
                ApplicationTrafficController.CreateId(executable),
                "Test application",
                executable,
                2_000_000,
                true,
                5_000_000);

            await store.SaveApplicationPolicyAsync(expected, CancellationToken.None);
            Assert.Equal(expected, Assert.Single(await store.LoadApplicationPoliciesAsync(CancellationToken.None)));

            await store.DeleteApplicationPolicyAsync(expected.Id, CancellationToken.None);
            Assert.Empty(await store.LoadApplicationPoliciesAsync(CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ApplicationPolicy_OldJsonDefaultsDownloadToUnlimited()
    {
        const string json = """
            {"Id":"app","DisplayName":"App","ExecutablePath":"C:\\App.exe","UploadLimitBitsPerSecond":1000000,"BlockInternet":false}
            """;

        LocalApplicationPolicy policy = JsonSerializer.Deserialize<LocalApplicationPolicy>(json)!;

        Assert.Null(policy.DownloadLimitBitsPerSecond);
        Assert.Equal(1_000_000, policy.UploadLimitBitsPerSecond);
    }

    [Fact]
    public void DownloadLimiter_ParsesInboundIpv4Tuple()
    {
        byte[] packet = new byte[40];
        packet[0] = 0x45;
        packet[9] = 6;
        IPAddress.Parse("8.8.8.8").GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse("192.168.1.3").GetAddressBytes().CopyTo(packet, 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 443);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 52100);

        Assert.True(ApplicationDownloadLimiter.TryReadInboundFlow(packet, out var flow));
        Assert.Equal(IPAddress.Parse("192.168.1.3"), flow.LocalAddress);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), flow.RemoteAddress);
        Assert.Equal((ushort)52100, flow.LocalPort);
        Assert.Equal((ushort)443, flow.RemotePort);
        Assert.Equal((byte)6, flow.Protocol);
    }

    [Fact]
    public void TrafficMonitor_ParsesOutboundIpv4Tuple()
    {
        byte[] packet = new byte[40];
        packet[0] = 0x45;
        packet[9] = 17;
        IPAddress.Parse("192.168.1.3").GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse("1.1.1.1").GetAddressBytes().CopyTo(packet, 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 53000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 443);

        Assert.True(ApplicationDownloadLimiter.TryReadFlow(packet, true, out var flow));
        Assert.Equal(IPAddress.Parse("192.168.1.3"), flow.LocalAddress);
        Assert.Equal(IPAddress.Parse("1.1.1.1"), flow.RemoteAddress);
        Assert.Equal((ushort)53000, flow.LocalPort);
        Assert.Equal((ushort)443, flow.RemotePort);
        Assert.Equal((byte)17, flow.Protocol);
    }
}
