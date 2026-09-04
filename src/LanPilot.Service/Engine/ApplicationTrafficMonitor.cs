using System.Collections.Concurrent;
using System.Diagnostics;
using Divert.Windows;

namespace LanPilot.Service.Engine;

/// <summary>
/// Passively measures local application traffic. It never diverts or modifies
/// packets: FLOW events associate a five-tuple with a process, while a low
/// priority NETWORK sniff handle counts bytes in both directions.
/// </summary>
public sealed class ApplicationTrafficMonitor(ILogger<ApplicationTrafficMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ApplicationDownloadLimiter.FlowKey, string> _flows = new();
    private readonly ConcurrentDictionary<string, MutableCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, ProcessIdentity> _processCache = new();
    private IReadOnlyDictionary<string, ApplicationTrafficRate> _currentRates =
        new Dictionary<string, ApplicationTrafficRate>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ApplicationTrafficRate> CurrentRates =>
        Volatile.Read(ref _currentRates);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using DivertService flowService = new(
                "!loopback and (tcp or udp)",
                DivertLayer.Flow,
                priority: -100,
                flags: DivertFlags.Sniff | DivertFlags.ReceiveOnly)
            {
                QueueTime = DivertService.MaxQueueTime
            };
            using DivertService packetService = new(
                "!loopback and (tcp or udp)",
                DivertLayer.Network,
                priority: -100,
                flags: DivertFlags.Sniff | DivertFlags.ReceiveOnly);

            Task flowTask = ObserveFlowsAsync(flowService, stoppingToken);
            Task packetTask = CountPacketsAsync(packetService, stoppingToken);
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stoppingToken)) PublishRates();
            await Task.WhenAll(flowTask, packetTask);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Monitoring is optional and must never take the control service down.
            logger.LogWarning(ex, "Per-application traffic monitoring is unavailable.");
        }
    }

    private async Task ObserveFlowsAsync(DivertService service, CancellationToken cancellationToken)
    {
        DivertAddress[] addresses = new DivertAddress[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await service.ReceiveAsync(Memory<byte>.Empty, addresses, cancellationToken);
                DivertAddress address = addresses[0];
                DivertAddress.FlowData flow = address.GetFlowData();
                ApplicationDownloadLimiter.FlowKey key = new(
                    flow.LocalAddress,
                    flow.RemoteAddress,
                    flow.LocalPort,
                    flow.RemotePort,
                    flow.Protocol);
                if (address.Event == DivertEvent.FlowDeleted)
                {
                    _flows.TryRemove(key, out _);
                    continue;
                }

                string? applicationId = ResolveApplicationId(flow.ProcessId);
                if (applicationId is not null) _flows[key] = applicationId;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Application flow monitoring stopped.");
        }
    }

    private async Task CountPacketsAsync(DivertService service, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ushort.MaxValue + 40];
        DivertAddress[] addresses = new DivertAddress[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DivertReceiveResult result = await service.ReceiveAsync(buffer, addresses, cancellationToken);
                bool outbound = addresses[0].IsOutbound;
                if (!ApplicationDownloadLimiter.TryReadFlow(
                        buffer.AsSpan(0, result.DataLength), outbound, out ApplicationDownloadLimiter.FlowKey key) ||
                    !_flows.TryGetValue(key, out string? applicationId))
                    continue;

                MutableCounter counter = _counters.GetOrAdd(applicationId, static _ => new MutableCounter());
                if (outbound) Interlocked.Add(ref counter.UploadBytes, result.DataLength);
                else Interlocked.Add(ref counter.DownloadBytes, result.DataLength);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Application packet monitoring stopped.");
        }
    }

    private void PublishRates()
    {
        Dictionary<string, ApplicationTrafficRate> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, MutableCounter counter) in _counters)
        {
            long down = Interlocked.Exchange(ref counter.DownloadBytes, 0);
            long up = Interlocked.Exchange(ref counter.UploadBytes, 0);
            if (down != 0 || up != 0)
                snapshot[id] = new ApplicationTrafficRate(down * 8, up * 8);
        }
        Volatile.Write(ref _currentRates, snapshot);

        DateTimeOffset staleBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach ((uint processId, ProcessIdentity identity) in _processCache)
        {
            if (identity.ResolvedAt < staleBefore) _processCache.TryRemove(processId, out _);
        }
    }

    private string? ResolveApplicationId(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue) return null;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_processCache.TryGetValue(processId, out ProcessIdentity cached) &&
            now - cached.ResolvedAt < TimeSpan.FromSeconds(10))
            return cached.ApplicationId;

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return null;
            string id = ApplicationTrafficController.CreateId(path);
            _processCache[processId] = new ProcessIdentity(id, now);
            return id;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class MutableCounter
    {
        public long DownloadBytes;
        public long UploadBytes;
    }

    private readonly record struct ProcessIdentity(string ApplicationId, DateTimeOffset ResolvedAt);
}

public readonly record struct ApplicationTrafficRate(
    long DownloadBitsPerSecond,
    long UploadBitsPerSecond);
