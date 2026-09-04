using LanPilot.Contracts;
using LanPilot.Service.Engine;

namespace LanPilot.Tests;

public sealed class PolicyResolverTests
{
    private readonly PolicyResolver _resolver = new();

    [Fact]
    public void DeviceLimitOverridesGroupDefault()
    {
        DevicePolicy device = new("device", false, 8_000_000, null, DevicePriority.Normal, "group");
        GroupPolicy group = new("group", "Guests", 2_000_000, 1_000_000, DevicePriority.Low, false);

        DevicePolicy result = _resolver.Resolve(device, group, [], DateTimeOffset.Now);

        Assert.Equal(8_000_000, result.DownloadLimitBitsPerSecond);
        Assert.Equal(1_000_000, result.UploadLimitBitsPerSecond);
        Assert.Equal(DevicePriority.Low, result.Priority);
    }

    [Fact]
    public void GroupDefaultsApplyWhenDeviceHasNoOverrides()
    {
        DevicePolicy device = new("device", false, null, null, DevicePriority.Normal, "group");
        GroupPolicy group = new("group", "Guests", 3_000_000, 750_000, DevicePriority.Low, false);

        DevicePolicy result = _resolver.Resolve(device, group, [], DateTimeOffset.Now);

        Assert.Equal(3_000_000, result.DownloadLimitBitsPerSecond);
        Assert.Equal(750_000, result.UploadLimitBitsPerSecond);
        Assert.Equal(DevicePriority.Low, result.Priority);
    }

    [Fact]
    public void ActiveScheduleUsesStrictestLimitAndBlockWins()
    {
        DateTimeOffset now = new(2026, 9, 4, 23, 0, 0, TimeSpan.Zero);
        DevicePolicy device = new("device", false, 8_000_000, 4_000_000, DevicePriority.Normal, null);
        ScheduleRule schedule = new(
            "schedule", "Night", "device", null, [DayOfWeek.Friday],
            new TimeOnly(22, 0), new TimeOnly(7, 0), true, 2_000_000, null, true);

        DevicePolicy result = _resolver.Resolve(device, null, [schedule], now);

        Assert.True(result.BlockInternet);
        Assert.Equal(2_000_000, result.DownloadLimitBitsPerSecond);
        Assert.Equal(4_000_000, result.UploadLimitBitsPerSecond);
    }
}
