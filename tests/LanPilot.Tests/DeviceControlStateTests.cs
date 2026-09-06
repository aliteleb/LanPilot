using LanPilot.Contracts;
using Xunit;

namespace LanPilot.Tests;

public sealed class DeviceControlStateTests
{
    [Theory]
    [InlineData(EngineMode.Idle, true, false, "None", false)]
    [InlineData(EngineMode.Controlling, true, true, "None", true)]
    [InlineData(EngineMode.Controlling, true, false, "None", false)]
    [InlineData(EngineMode.Controlling, false, true, "Fault", false)]
    [InlineData(EngineMode.Recovering, true, false, "None", false)]
    public void ApplicationActivationDoesNotImplyDeviceMonitoring(EngineMode mode, bool apps, bool devices, string reason, bool expected)
    {
        DashboardSnapshot snapshot = new(new(mode, "", true, "1.88", false, false, null, DateTimeOffset.UtcNow),
            [], null, [], [], [], [], new(null, false, 30, true, false),
            new(reason, reason != "None", true, apps, DateTimeOffset.UtcNow, DevicesActive: devices));
        Assert.Equal(expected, snapshot.IsDeviceControlActive);
        Assert.Equal(mode == EngineMode.Controlling, (snapshot with { ControlSafety = null }).IsDeviceControlActive);
    }
}
