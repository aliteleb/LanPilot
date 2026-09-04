using LanPilot.Service.Engine;

namespace LanPilot.Service;

public sealed class LanPilotWorker(
    LanPilotCoordinator coordinator,
    ApplicationTrafficController applicationTrafficController,
    ILogger<LanPilotWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await coordinator.InitializeAsync(stoppingToken);

        // One consolidated sample per second keeps the UI responsive while
        // still presenting genuinely live per-device throughput.
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await coordinator.TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LanPilot background tick failed.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await applicationTrafficController.StopAsync(cancellationToken);
        await coordinator.EmergencyPauseAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
