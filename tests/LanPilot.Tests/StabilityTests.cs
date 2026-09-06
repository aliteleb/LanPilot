using System.Buffers.Binary;
using System.Net;
using LanPilot.Contracts;
using LanPilot.Service.Engine;
using LanPilot.Service.Persistence;

namespace LanPilot.Tests;

public sealed class StabilityTests
{
    private sealed class Clock : TimeProvider
    {
        public long Tick;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Tick;
    }

    [Fact]
    public void LowRateAllowsFullFramesAndRepaysDebt()
    {
        Clock clock = new();
        RateLimiter limiter = new(48000, clock);
        Assert.True(limiter.TryConsume(1514));
        Assert.False(limiter.TryConsume(1514));
        clock.Tick = 253;
        Assert.True(limiter.TryConsume(1514));
        long bytes = 3028;
        for (clock.Tick = 254; clock.Tick <= 60000; clock.Tick++)
            if (limiter.TryConsume(1514)) bytes += 1514;
        Assert.InRange(bytes, 350000, 6000 * 60 + 1514);
    }

    [Fact]
    public void RepeatedSameRateUpdatesDoNotRefillBucket()
    {
        RateLimiter limiter = new(48000, new Clock());
        Assert.True(limiter.TryConsume(1514));
        for (int i = 0; i < 100; i++)
        {
            limiter.UpdateRate(48000);
            Assert.False(limiter.TryConsume(1514));
        }
    }

    [Fact]
    public void QueueBudgetIncludesInFlightPacketsAndIsPerApplication()
    {
        PacketMemoryBudget budget = new();
        for (int i = 0; i < 8; i++) Assert.True(budget.TryReserve("app" + i, (int)PacketMemoryBudget.PerApplicationLimit));
        Assert.Equal(PacketMemoryBudget.TotalLimit, budget.TotalBytes);
        Assert.False(budget.TryReserve("new", 1));
        budget.Release("app0", 1);
        Assert.True(budget.TryReserve("new", 1));
        Assert.False(budget.TryReserve("app1", 1));
        Assert.Throws<InvalidOperationException>(() => budget.Release("missing", 1));
    }

    [Fact]
    public void QueueBudgetRemainsBoundedUnderConcurrentTurnover()
    {
        PacketMemoryBudget budget = new();
        Parallel.For(0, 100000, i =>
        {
            string id = "app" + (i % 32);
            if (budget.TryReserve(id, 65535))
            {
                Assert.InRange(budget.TotalBytes, 0, PacketMemoryBudget.TotalLimit);
                budget.Release(id, 65535);
            }
        });
        Assert.Equal(0, budget.TotalBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0x2000)]
    public void FragmentedIpv4IsNotMisclassified(int flags)
    {
        byte[] packet = Ipv4();
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)flags);
        Assert.False(ApplicationDownloadLimiter.TryReadInboundFlow(packet, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(41)]
    public void InvalidIpv4LengthIsRejected(int length)
    {
        byte[] packet = Ipv4();
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)length);
        Assert.False(ApplicationDownloadLimiter.TryReadInboundFlow(packet, out _));
    }

    [Fact]
    public void Ipv6ExtensionOffsetsAreValidated()
    {
        byte[] packet = new byte[68];
        packet[0] = 0x60; packet[5] = 28; packet[6] = 60;
        packet[40] = 6; packet[41] = 0; packet[60] = 0x50;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(48), 443);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(50), 50000);
        Assert.True(ApplicationDownloadLimiter.TryReadInboundFlow(packet, out var key));
        Assert.Equal(443, key.RemotePort);
        Assert.Equal(50000, key.LocalPort);
        packet[41] = 255;
        Assert.False(ApplicationDownloadLimiter.TryReadInboundFlow(packet, out _));
    }

    [Fact]
    public void SharedIdentityDeletionOverridesTcpBackfill()
    {
        ApplicationFlowRegistry registry = new();
        var key = new ApplicationDownloadLimiter.FlowKey(IPAddress.Loopback, IPAddress.Parse("192.0.2.1"), 1234, 443, 6);
        registry.ReplaceTcp(new Dictionary<ApplicationDownloadLimiter.FlowKey, string> { [key] = "app" });
        Assert.True(registry.TryGet(key, out string? id));
        Assert.Equal("app", id);
        registry.Register(key, null);
        Assert.False(registry.TryGet(key, out _));
        registry.Clear();
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TcpBackfillParsesNetworkOrderPortsAndValidatesCount()
    {
        byte[] table = new byte[28]; table[0] = 1;
        IPAddress.Parse("192.0.2.1").GetAddressBytes().CopyTo(table, 8);
        IPAddress.Parse("192.0.2.2").GetAddressBytes().CopyTo(table, 16);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12), 32123);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(20), 443);
        table[24] = 42;
        Dictionary<ApplicationDownloadLimiter.FlowKey, string> result = [];
        WindowsTcpSnapshot.Parse(table, false, pid => pid == 42 ? "expected" : null, result);
        var row = Assert.Single(result);
        Assert.Equal((ushort)32123, row.Key.LocalPort);
        Assert.Equal("expected", row.Value);
        table[0] = 2;
        Assert.Throws<InvalidDataException>(() => WindowsTcpSnapshot.Parse(table, false, _ => null, result));
    }

    [Fact]
    public async Task FaultSuspensionSurvivesJournalReload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LanPilot.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var state = new ControlSafetyStatus("Fault", true, false, false, DateTimeOffset.UtcNow);
            await new ControlSafetyJournal(directory).SaveAsync(state, CancellationToken.None);
            Assert.Equal(state, await new ControlSafetyJournal(directory).LoadAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static byte[] Ipv4()
    {
        byte[] packet = new byte[40];
        packet[0] = 0x45; packet[3] = 40; packet[9] = 6; packet[32] = 0x50;
        return packet;
    }
}
