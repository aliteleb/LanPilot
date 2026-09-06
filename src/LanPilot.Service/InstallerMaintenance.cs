using System.IO.Pipes;
using System.ServiceProcess;
using LanPilot.Contracts;
using LanPilot.Service.Engine;
using LanPilot.Service.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LanPilot.Service;

internal static class InstallerMaintenance
{
    // Invoked by the installer from its private temporary payload, never by the UI.
    internal static async Task<int> PrepareAsync()
    {
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(60));
            bool installed;
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanPilotService"))
                installed = key is not null;
            if (installed)
            {
                using ServiceController service = new("LanPilotService");
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    if (service.Status != ServiceControllerStatus.StopPending)
                    {
                        // v0.1.1 understands this command too. A failed acknowledgement aborts replacement.
                        await RequestPauseAsync(deadline.Token);
                        service.Stop();
                    }
                    long waitStarted = Environment.TickCount64;
                    while (service.Status != ServiceControllerStatus.Stopped && Environment.TickCount64 - waitStarted < 30000)
                    {
                        await Task.Delay(250, deadline.Token);
                        service.Refresh();
                    }
                    service.Refresh();
                    if (service.Status != ServiceControllerStatus.Stopped) throw new IOException("Service did not stop.");
                }
            }

            await using ApplicationDownloadLimiter limiter = new(NullLogger<ApplicationDownloadLimiter>.Instance);
            using ApplicationTrafficMonitor monitor = new(NullLogger<ApplicationTrafficMonitor>.Instance);
            ApplicationTrafficController policies = new(limiter, monitor, NullLogger<ApplicationTrafficController>.Instance);
            await policies.SuspendAllAsync(deadline.Token);
            ControlSessionJournal journal = new();
            if (await journal.LoadAsync(deadline.Token) is ControlSession session)
            {
                await using TrafficEngine traffic = new(NullLogger<TrafficEngine>.Instance);
                await traffic.RestoreAbandonedSessionAsync(session, deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                journal.Clear();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LanPilot update aborted: {ex.Message}");
            return 1;
        }
    }

    private static async Task RequestPauseAsync(CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using NamedPipeClientStream pipe = new(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000, timeout.Token);
        PipeEnvelope request = PipeProtocol.Request(PipeCommands.EmergencyPause, new { });
        await PipeProtocol.WriteAsync(pipe, request, timeout.Token);
        while (await PipeProtocol.ReadAsync(pipe, timeout.Token) is PipeEnvelope response)
        {
            if (response.RequestId != request.RequestId) continue;
            if (response.Error is not null || !response.ReadPayload<OperationResult>().Success)
                throw new IOException("The service could not confirm safe suspension.");
            return;
        }
        throw new IOException("The service disconnected before acknowledging suspension.");
    }
}
