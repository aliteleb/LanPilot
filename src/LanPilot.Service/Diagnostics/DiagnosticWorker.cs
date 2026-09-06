using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using LanPilot.Service.Engine;

namespace LanPilot.Service.Diagnostics;

public sealed class DiagnosticWorker(
    DiagnosticRecorder recorder,
    LanPilotCoordinator coordinator,
    TrafficEngine traffic,
    ApplicationDownloadLimiter limiter,
    ApplicationTrafficMonitor monitor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        recorder.Record("LanPilot.Service", "Information", "Diagnostics session started");
        try { recorder.Sample(new { startup = CaptureVersions() }); }
        catch (Exception ex) { recorder.Record("LanPilot.Diagnostics", "Warning", "Version snapshot failed", ex); }
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    recorder.Sample(new
                    {
                        process = CaptureSafely(CaptureProcess), control = CaptureSafely(() => coordinator.GetDiagnosticState(false)),
                        deviceTraffic = CaptureSafely(traffic.GetDiagnostics),
                        applicationLimiter = CaptureSafely(limiter.GetDiagnostics), applicationMonitor = CaptureSafely(monitor.GetDiagnostics),
                        interfaces = CaptureSafely(CaptureInterfaces)
                    });
                }
                catch (Exception ex)
                {
                    recorder.Record("LanPilot.Diagnostics", "Warning", "Health sample failed", ex);
                }
                await recorder.FlushAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            recorder.Record("LanPilot.Service", "Information", "Diagnostics worker stopped");
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            try { await recorder.FlushAsync(timeout.Token); }
            catch (OperationCanceledException) { }
        }
    }

    public static object CaptureProcess()
    {
        using Process process = Process.GetCurrentProcess();
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        return new
        {
            pid = process.Id, startedAt = process.StartTime.ToUniversalTime(),
            workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
            cpuTimeMs = process.TotalProcessorTime.TotalMilliseconds, handles = process.HandleCount,
            threads = process.Threads.Count, managedBytes = GC.GetTotalMemory(false),
            heapBytes = gc.HeapSizeBytes, committedBytes = gc.TotalCommittedBytes,
            fragmentedBytes = gc.FragmentedBytes, memoryLoadBytes = gc.MemoryLoadBytes,
            availableMemoryBytes = gc.TotalAvailableMemoryBytes,
            totalAllocatedBytes = GC.GetTotalAllocatedBytes(false),
            collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            threadPoolPending = ThreadPool.PendingWorkItemCount, threadPoolThreads = ThreadPool.ThreadCount
        };
    }

    public static object CaptureInterfaces() => NetworkInterface.GetAllNetworkInterfaces().Take(32).Select(item =>
    {
        try
        {
            IPInterfaceStatistics stats = item.GetIPStatistics();
            return (object)new
            {
                item.Id, type = item.NetworkInterfaceType.ToString(), state = item.OperationalStatus.ToString(),
                item.Speed, stats.BytesReceived, stats.BytesSent, stats.IncomingPacketsDiscarded,
                stats.OutgoingPacketsDiscarded, stats.IncomingPacketsWithErrors, stats.OutgoingPacketsWithErrors,
                gateways = item.GetIPProperties().GatewayAddresses.Select(address => address.Address.ToString()).ToArray()
            };
        }
        catch (NetworkInformationException ex) { return new { item.Id, errorCode = ex.ErrorCode }; }
    }).ToArray();

    public static object CaptureVersions()
    {
        using Process process = Process.GetCurrentProcess();
        string[] names = ["Divert.Windows.dll", "WinDivert.dll", "wpcap.dll", "Packet.dll"];
        var loaded = process.Modules.Cast<ProcessModule>()
            .Where(module => names.Contains(module.ModuleName, StringComparer.OrdinalIgnoreCase))
            .Select(module => new { name = module.ModuleName, version = module.FileVersionInfo.FileVersion }).ToArray();
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new
        {
            service = typeof(DiagnosticWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            buildId = typeof(DiagnosticWorker).Assembly.ManifestModule.ModuleVersionId,
            framework = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), loadedLibraries = loaded,
            installedNpcapFiles = new[] { "Npcap/wpcap.dll", "Npcap/Packet.dll", "wpcap.dll", "Packet.dll", "drivers/npcap.sys" }
                .Select(relative =>
                {
                    string path = Path.Combine(system, relative);
                    return new { relativePath = relative, exists = File.Exists(path),
                        version = File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion : null };
                }).ToArray()
        };
    }

    public static object CaptureSafely(Func<object> capture)
    {
        try { return capture(); }
        catch (Exception ex) { return new { unavailable = true, error = ex.GetType().Name, ex.HResult }; }
    }
}
