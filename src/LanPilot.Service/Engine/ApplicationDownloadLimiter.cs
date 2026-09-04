using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Divert.Windows;
using LanPilot.Contracts;

namespace LanPilot.Service.Engine;

/// <summary>
/// Applies per-executable inbound limits. WinDivert's FLOW layer supplies the
/// process identity while the NETWORK layer supplies the packets. Unmatched
/// traffic is reinjected immediately. If one limiter queue overloads, only a
/// packet for that limited application is dropped so other traffic stays live.
/// </summary>
public sealed class ApplicationDownloadLimiter(ILogger<ApplicationDownloadLimiter> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, LocalApplicationPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<FlowKey, string> _flows = new();
    private readonly ConcurrentDictionary<string, PolicyQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCts;
    private DivertService? _flowService;
    private DivertService? _packetService;
    private Task? _flowTask;
    private Task? _packetTask;
    private volatile bool _bypassLimits;

    public async Task UpsertAsync(LocalApplicationPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.DownloadLimitBitsPerSecond is long)
            _policies[policy.Id] = policy;
        else
            _policies.TryRemove(policy.Id, out _);

        await ReconcileAsync(cancellationToken);
    }

    public async Task RemoveAsync(string policyId, CancellationToken cancellationToken)
    {
        _policies.TryRemove(policyId, out _);
        await ReconcileAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_policies.IsEmpty)
            {
                await StopCoreAsync();
                return;
            }

            if (_runCts is not null) return;
            StartCore();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void StartCore()
    {
        CancellationTokenSource runCts = new();
        DivertService? flowService = null;
        DivertService? packetService = null;
        try
        {
            flowService = new DivertService(
                "!loopback and (tcp or udp)",
                DivertLayer.Flow,
                flags: DivertFlags.Sniff | DivertFlags.ReceiveOnly)
            {
                QueueTime = DivertService.MaxQueueTime
            };
            packetService = new DivertService(
                "inbound and !loopback and !impostor and (tcp or udp)",
                DivertLayer.Network);

            _runCts = runCts;
            _bypassLimits = false;
            _flowService = flowService;
            _packetService = packetService;
            _flowTask = Task.Run(() => ObserveFlowsAsync(flowService, runCts.Token));
            _packetTask = Task.Run(() => ProcessPacketsAsync(packetService, runCts.Token));
            logger.LogInformation("LanPilot per-application download limiter started with WinDivert {Version}.", packetService.Version);
        }
        catch
        {
            runCts.Dispose();
            flowService?.Dispose();
            packetService?.Dispose();
            throw;
        }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? runCts = _runCts;
        if (runCts is null) return;

        _runCts = null;
        _bypassLimits = true;
        runCts.Cancel();
        Task[] tasks = [_flowTask ?? Task.CompletedTask, _packetTask ?? Task.CompletedTask];
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogDebug("Application limiter receive loops stopped during cancellation.");
        }

        foreach (PolicyQueue queue in _queues.Values) queue.Complete();
        Task[] drainTasks = _queues.Values.Select(queue => queue.Completion).ToArray();
        try
        {
            await Task.WhenAll(drainTasks).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Application limiter queues did not fully drain before shutdown.");
        }

        _queues.Clear();
        _flows.Clear();
        _flowService?.Dispose();
        _packetService?.Dispose();
        _flowService = null;
        _packetService = null;
        _flowTask = null;
        _packetTask = null;
        runCts.Dispose();
        logger.LogInformation("LanPilot per-application download limiter stopped; packet handling is fail-open.");
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
                FlowKey key = new(flow.LocalAddress, flow.RemoteAddress, flow.LocalPort, flow.RemotePort, flow.Protocol);
                if (address.Event == DivertEvent.FlowDeleted)
                {
                    _flows.TryRemove(key, out _);
                    continue;
                }

                string? policyId = ResolvePolicyId(flow.ProcessId);
                if (policyId is not null) _flows[key] = policyId;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Application flow observer stopped unexpectedly.");
            _ = Task.Run(() => StopAfterFaultAsync());
        }
    }

    private async Task ProcessPacketsAsync(DivertService service, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ushort.MaxValue + 40];
        DivertAddress[] addresses = new DivertAddress[1];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DivertReceiveResult result = await service.ReceiveAsync(buffer, addresses, cancellationToken);
                byte[] packet = buffer.AsSpan(0, result.DataLength).ToArray();
                DivertAddress address = addresses[0];

                if (TryReadInboundFlow(packet, out FlowKey key) &&
                    _flows.TryGetValue(key, out string? policyId) &&
                    _policies.TryGetValue(policyId, out LocalApplicationPolicy? policy) &&
                    policy.DownloadLimitBitsPerSecond is long)
                {
                    PolicyQueue queue = _queues.GetOrAdd(policyId, id =>
                        new PolicyQueue(id, service, GetRate, logger));
                    // A full queue means the selected application is exceeding
                    // its limit. Dropping this packet lets TCP apply backpressure
                    // without delaying unrelated Windows traffic.
                    queue.TryEnqueue(new QueuedPacket(packet, address));
                    continue;
                }

                await service.SendAsync(packet, new[] { address }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Application packet limiter stopped unexpectedly.");
            _ = Task.Run(() => StopAfterFaultAsync());
        }
    }

    private long? GetRate(string policyId) =>
        !_bypassLimits && _policies.TryGetValue(policyId, out LocalApplicationPolicy? policy)
            ? policy.DownloadLimitBitsPerSecond
            : null;

    private string? ResolvePolicyId(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue) return null;
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return null;
            string id = ApplicationTrafficController.CreateId(path);
            return _policies.ContainsKey(id) ? id : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private async Task StopAfterFaultAsync()
    {
        try { await StopAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogError(ex, "Could not stop the application limiter after a fault."); }
    }

    internal static bool TryReadInboundFlow(ReadOnlySpan<byte> packet, out FlowKey key)
        => TryReadFlow(packet, false, out key);

    internal static bool TryReadFlow(ReadOnlySpan<byte> packet, bool outbound, out FlowKey key)
    {
        key = default;
        if (packet.Length < 20) return false;
        int version = packet[0] >> 4;
        if (version == 4)
        {
            int headerLength = (packet[0] & 0x0F) * 4;
            if (headerLength < 20 || packet.Length < headerLength + 4) return false;
            byte protocol = packet[9];
            if (protocol is not (6 or 17)) return false;
            IPAddress source = new(packet.Slice(12, 4));
            IPAddress destination = new(packet.Slice(16, 4));
            ushort sourcePort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(headerLength, 2));
            ushort destinationPort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(headerLength + 2, 2));
            IPAddress local = outbound ? source : destination;
            IPAddress remote = outbound ? destination : source;
            ushort localPort = outbound ? sourcePort : destinationPort;
            ushort remotePort = outbound ? destinationPort : sourcePort;
            key = new(local, remote, localPort, remotePort, protocol);
            return true;
        }

        if (version == 6 && packet.Length >= 44)
        {
            byte protocol = packet[6];
            if (protocol is not (6 or 17)) return false;
            IPAddress source = new(packet.Slice(8, 16));
            IPAddress destination = new(packet.Slice(24, 16));
            ushort sourcePort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(40, 2));
            ushort destinationPort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(42, 2));
            IPAddress local = outbound ? source : destination;
            IPAddress remote = outbound ? destination : source;
            ushort localPort = outbound ? sourcePort : destinationPort;
            ushort remotePort = outbound ? destinationPort : sourcePort;
            key = new(local, remote, localPort, remotePort, protocol);
            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _lifecycleGate.Dispose();
    }

    internal readonly record struct FlowKey(
        IPAddress LocalAddress,
        IPAddress RemoteAddress,
        ushort LocalPort,
        ushort RemotePort,
        byte Protocol);

    private sealed record QueuedPacket(byte[] Data, DivertAddress Address);

    private sealed class PolicyQueue
    {
        private readonly string _policyId;
        private readonly DivertService _service;
        private readonly Func<string, long?> _getRate;
        private readonly ILogger _logger;
        // 4096 normal MTU packets are about 6 MB. This absorbs TCP receive-window
        // bursts without retransmission-heavy under-throttling while remaining
        // bounded for UDP and for applications opening many fast connections.
        private readonly Channel<QueuedPacket> _channel = Channel.CreateBounded<QueuedPacket>(new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        private readonly Task _worker;
        private long _windowStartedTimestamp;
        private long _bytesSentInWindow;

        public PolicyQueue(string policyId, DivertService service, Func<string, long?> getRate, ILogger logger)
        {
            _policyId = policyId;
            _service = service;
            _getRate = getRate;
            _logger = logger;
            _worker = Task.Run(ProcessAsync);
        }

        public Task Completion => _worker;
        public bool TryEnqueue(QueuedPacket packet) => _channel.Writer.TryWrite(packet);
        public void Complete() => _channel.Writer.TryComplete();

        private async Task ProcessAsync()
        {
            await foreach (QueuedPacket packet in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    long? rate = _getRate(_policyId);
                    if (rate is long bitsPerSecond)
                    {
                        double bytesPerSecond = bitsPerSecond / 8d;
                        double windowSeconds = Math.Max(0.1d, packet.Data.Length / bytesPerSecond);
                        long windowTicks = Math.Max(1, (long)Math.Ceiling(windowSeconds * Stopwatch.Frequency));
                        long byteBudget = Math.Max(packet.Data.Length, (long)Math.Floor(bytesPerSecond * windowSeconds));
                        long now = Stopwatch.GetTimestamp();
                        if (_windowStartedTimestamp == 0 || now - _windowStartedTimestamp >= windowTicks)
                        {
                            _windowStartedTimestamp = now;
                            _bytesSentInWindow = 0;
                        }

                        if (_bytesSentInWindow > 0 && _bytesSentInWindow + packet.Data.Length > byteBudget)
                        {
                            long remainingTicks = windowTicks - (now - _windowStartedTimestamp);
                            if (remainingTicks > 0)
                                await Task.Delay(TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency));
                            _windowStartedTimestamp = Stopwatch.GetTimestamp();
                            _bytesSentInWindow = 0;
                        }
                        _bytesSentInWindow += packet.Data.Length;
                    }
                    else
                    {
                        _windowStartedTimestamp = 0;
                        _bytesSentInWindow = 0;
                    }

                    await _service.SendAsync(packet.Data, new[] { packet.Address }, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "A queued application packet could not be reinjected.");
                }
            }
        }
    }
}
