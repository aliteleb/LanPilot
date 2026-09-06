using System.Collections.Concurrent;
using System.Diagnostics;
using Divert.Windows;
using LanPilot.Service.Diagnostics;

namespace LanPilot.Service.Engine;

/// <summary>
/// Passively measures local application traffic. It never diverts or modifies
/// packets: FLOW events associate a five-tuple with a process, while a low
/// priority NETWORK sniff handle counts bytes in both directions.
/// </summary>
public sealed class ApplicationTrafficMonitor(ILogger<ApplicationTrafficMonitor> logger, ApplicationFlowRegistry? registry = null) : BackgroundService
{
    private readonly ApplicationFlowRegistry _flows = registry ?? new();
    public bool IsAvailable { get; private set; }
    private long _unclassified;
    private readonly ConcurrentDictionary<string, MutableCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, ProcessIdentity> _processCache = new();
    private readonly PacketDiagnostics _diagnostics = new();
    private Task? _flowTask, _packetTask;

    public object GetDiagnostics() => new
    {
        available = IsAvailable, unclassified = Interlocked.Read(ref _unclassified), flowTask = _flowTask?.Status.ToString(), packetTask = _packetTask?.Status.ToString(),
        flows = _flows.Count, applications = _counters.Count, processCache = _processCache.Count,
        packets = _diagnostics.Snapshot()
    };
    private IReadOnlyDictionary<string, ApplicationTrafficRate> _currentRates =
        new Dictionary<string, ApplicationTrafficRate>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, ApplicationTrafficRate> CurrentRates =>
        Volatile.Read(ref _currentRates);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Monitoring is optional: bounded retries never restart traffic control.
        for (int attempt = 0; attempt < 4 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try { await RunSessionAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Per-application traffic monitoring is unavailable."); }
            finally
            {
                IsAvailable = false;
                _flows.Clear();
                _counters.Clear();
                _processCache.Clear();
                Volatile.Write(ref _currentRates, new Dictionary<string, ApplicationTrafficRate>());
            }
            if (attempt < 3)
                try { await Task.Delay(TimeSpan.FromSeconds(15 * (attempt + 1)), stoppingToken); }
                catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using DivertService flow = new("!loopback and (tcp or udp)", DivertLayer.Flow,
            priority: -100, flags: DivertFlags.Sniff | DivertFlags.ReceiveOnly);
        using DivertService packets = new("!loopback and (tcp or udp)", DivertLayer.Network,
            priority: -100, flags: DivertFlags.Sniff | DivertFlags.ReceiveOnly);
        _flowTask = ObserveFlowsAsync(flow, session.Token);
        _packetTask = CountPacketsAsync(packets, session.Token);
        try
        {
            int ticks = 0;
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(session.Token))
            {
                if (_flowTask.IsCompleted || _packetTask.IsCompleted)
                    throw new IOException("A passive monitoring worker stopped.");
                if (ticks++ % 5 == 0) _flows.ReplaceTcp(WindowsTcpSnapshot.Read(ResolveApplicationId));
                IsAvailable = true;
                PublishRates();
            }
        }
        finally
        {
            IsAvailable = false;
            await session.CancelAsync();
            // Do not release buffers/handles while native receives still own them.
            await Task.WhenAll(_flowTask, _packetTask);
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
                    _flows.Register(key, null);
                    continue;
                }

                string? applicationId = ResolveApplicationId(flow.ProcessId);
                if (applicationId is not null) _flows.Register(key, applicationId);
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
                _diagnostics.Received();
                bool outbound = addresses[0].IsOutbound;
                if (!ApplicationDownloadLimiter.TryReadFlow(
                        buffer.AsSpan(0, result.DataLength), outbound, out ApplicationDownloadLimiter.FlowKey key) ||
                    !_flows.TryGet(key, out string? applicationId) || applicationId is null)
                {
                    Interlocked.Increment(ref _unclassified);
                    continue;
                }

                MutableCounter counter = _counters.GetOrAdd(applicationId, static _ => new MutableCounter());
                Interlocked.Exchange(ref counter.LastSeenTick, Environment.TickCount64);
                if (outbound) Interlocked.Add(ref counter.UploadBytes, result.DataLength);
                else Interlocked.Add(ref counter.DownloadBytes, result.DataLength);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Application packet monitoring stopped.");
            _diagnostics.Error();
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
            else if (Environment.TickCount64 - Interlocked.Read(ref counter.LastSeenTick) > 60000)
                _counters.TryRemove(new KeyValuePair<string, MutableCounter>(id, counter));
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
        public long LastSeenTick;
        public long DownloadBytes;
        public long UploadBytes;
    }

    private readonly record struct ProcessIdentity(string ApplicationId, DateTimeOffset ResolvedAt);
}

public readonly record struct ApplicationTrafficRate(
    long DownloadBitsPerSecond,
    long UploadBitsPerSecond);
