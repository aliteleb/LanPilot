using LanPilot.Service.Engine;

namespace LanPilot.Service;

public sealed class ControlHealthWorker(LanPilotCoordinator coordinator, TrafficEngine traffic,
    ApplicationDownloadLimiter limiter, ILogger<ControlHealthWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!coordinator.IsInitialized || coordinator.IsSuspended || coordinator.IsTransitioning) continue;
            string? fault = traffic.Fault ?? limiter.Fault;
            if (fault is null && coordinator.ExpectsDeviceControl && !traffic.IsRunning)
                fault = "Device capture stopped unexpectedly";
            if (fault is null) continue;
            logger.LogError("Control health failure: {Fault}", fault);
            try { await coordinator.SuspendAllAsync("Fault", CancellationToken.None, fault); }
            catch (Exception ex) { logger.LogError(ex, "Automatic network recovery was incomplete."); }
        }
    }
}
