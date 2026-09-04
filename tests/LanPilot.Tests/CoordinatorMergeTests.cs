using LanPilot.Contracts;
using LanPilot.Service;

namespace LanPilot.Tests;

public sealed class CoordinatorMergeTests
{
    [Fact]
    public void AssignDeviceToGroup_UpdatesSnapshotAndPolicyIdentity()
    {
        DevicePolicy policy = new("device", false, null, null, DevicePriority.Normal, null);
        DeviceSnapshot device = new(
            "device", "network", "AA:BB:CC:DD:EE:FF", "192.168.1.20", "New phone", null, "Phone", null,
            true, false, false, DateTimeOffset.Now, DateTimeOffset.Now, 0, 0, 0, 0, policy);
        GroupPolicy guests = new("guests", "Guests", 2_000_000, 1_000_000, DevicePriority.Low, false);

        DeviceSnapshot assigned = LanPilotCoordinator.AssignDeviceToGroup(device, guests);

        Assert.Equal(guests.Id, assigned.GroupId);
        Assert.Equal(guests.Id, assigned.Policy.GroupId);
        Assert.Null(assigned.Policy.DownloadLimitBitsPerSecond);
        Assert.Null(assigned.Policy.UploadLimitBitsPerSecond);
    }

    [Fact]
    public void ResetDeviceToGroup_ClearsCustomIdentityPolicyAndUsage()
    {
        DateTimeOffset firstSeen = DateTimeOffset.Now.AddDays(-5);
        DateTimeOffset resetAt = DateTimeOffset.Now;
        DevicePolicy customPolicy = new("device", true, 8_000_000, 2_000_000, DevicePriority.High, "family");
        DeviceSnapshot device = new(
            "device", "network", "2A:C1:3B:26:9A:FE", "192.168.1.61", "My phone", null, "Phone", "family",
            true, false, false, firstSeen, DateTimeOffset.Now, 500_000, 100_000, 50_000_000, 5_000_000, customPolicy);
        GroupPolicy guests = new("guests", "Guests", 3_000_000, 1_000_000, DevicePriority.Low, false);

        DeviceSnapshot reset = LanPilotCoordinator.ResetDeviceToGroup(device, guests, resetAt);

        Assert.Equal("Device 9A:FE", reset.DisplayName);
        Assert.Equal("Unknown", reset.DeviceType);
        Assert.Equal(guests.Id, reset.GroupId);
        Assert.Equal(guests.Id, reset.Policy.GroupId);
        Assert.False(reset.Policy.BlockInternet);
        Assert.Null(reset.Policy.DownloadLimitBitsPerSecond);
        Assert.Null(reset.Policy.UploadLimitBitsPerSecond);
        Assert.Equal(DevicePriority.Normal, reset.Policy.Priority);
        Assert.Equal(resetAt, reset.FirstSeen);
        Assert.Equal(0, reset.TotalDownloadBytes);
        Assert.Equal(0, reset.TotalUploadBytes);
    }

    [Fact]
    public void DiscoveryMerge_PreservesLatestUserSettingsAndPolicy()
    {
        DateTimeOffset firstSeen = DateTimeOffset.Now.AddDays(-2);
        DevicePolicy latestPolicy = new("device", true, 2_000_000, 500_000, DevicePriority.High, "family");
        DeviceSnapshot current = new(
            "device", "network", "AA:BB:CC:DD:EE:FF", "192.168.1.20", "My phone", null, "Phone", "family",
            true, false, false, firstSeen, DateTimeOffset.Now.AddSeconds(-1), 120_000, 30_000, 50_000, 10_000, latestPolicy);
        DevicePolicy stalePolicy = new("device", false, null, null, DevicePriority.Normal, null);
        DeviceSnapshot discovered = new(
            "device", "network", "AA:BB:CC:DD:EE:FF", "192.168.1.61", "Device EE:FF", "android", "Unknown", null,
            true, false, false, DateTimeOffset.Now, DateTimeOffset.Now, 0, 0, 0, 0, stalePolicy);

        DeviceSnapshot merged = LanPilotCoordinator.MergeDiscoveredDevice(current, discovered);

        Assert.Equal("192.168.1.61", merged.Ipv4Address);
        Assert.Equal("My phone", merged.DisplayName);
        Assert.Equal("Phone", merged.DeviceType);
        Assert.Equal(latestPolicy, merged.Policy);
        Assert.Equal(120_000, merged.DownloadBitsPerSecond);
        Assert.Equal(firstSeen, merged.FirstSeen);
    }
}
