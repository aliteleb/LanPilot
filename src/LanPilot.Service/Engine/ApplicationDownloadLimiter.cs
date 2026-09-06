using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Divert.Windows;
using LanPilot.Contracts;
using LanPilot.Service.Diagnostics;

namespace LanPilot.Service.Engine;

/// <summary>
/// Applies per-executable inbound limits. WinDivert's FLOW layer supplies the
/// process identity while the NETWORK layer supplies the packets. Unmatched
/// traffic is reinjected immediately. If one limiter queue overloads, only a
/// packet for that limited application is dropped so other traffic stays live.
/// </summary>
public sealed class ApplicationDownloadLimiter(ILogger<ApplicationDownloadLimiter> logger, ApplicationFlowRegistry? registry = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<string, LocalApplicationPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ApplicationFlowRegistry _flows = registry ?? new();
    private readonly ConcurrentDictionary<string, PolicyQueue> _queues = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCts;
    private DivertService? _packetService;
    private Task? _packetTask;
    private volatile bool _bypassLimits;
    private readonly PacketDiagnostics _diagnostics = new();
    private readonly PacketMemoryBudget _memoryBudget = new();
    private string? _fault;
    private long _lastQueuePrune;
    public string? Fault => Volatile.Read(ref _fault);

    public object GetDiagnostics() => new
    {
        running = _runCts is not null,
        bypass = _bypassLimits,
        fault = Fault,
        queuedBytes = _memoryBudget.TotalBytes,
        packetTask = _packetTask?.Status.ToString(),
        policies = _policies.Count,
        flows = _flows.Count,
        queueCount = _queues.Count,
        packets = _diagnostics.Snapshot(),
        queues = _queues.Take(128).Select(pair => new
        {
            policyId = pair.Key,
            queuedPackets = pair.Value.QueuedPackets,
            worker = pair.Value.Completion.Status.ToString(),
            limit = GetRate(pair.Key)
        }).ToArray()
    };

    public async Task UpsertAsync(LocalApplicationPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.DownloadLimitBitsPerSecond is long)
            _policies[policy.Id] = policy;
        else
            _policies.TryRemove(policy.Id, out _);
        foreach (PolicyQueue queue in _queues.Values) queue.Wake();

        await ReconcileAsync(cancellationToken);
    }

    public async Task RemoveAsync(string policyId, CancellationToken cancellationToken)
    {
        _policies.TryRemove(policyId, out _);
        // The worker may still own a native send. Keep it tracked until session shutdown.
        if (_queues.TryGetValue(policyId, out PolicyQueue? queue)) queue.Wake();
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
        DivertService? packetService = null;
        try
        {
            packetService = new DivertService(
                "inbound and !loopback and !impostor and (tcp or udp)",
                DivertLayer.Network);

            _runCts = runCts;
            Volatile.Write(ref _fault, null);
            _bypassLimits = false;
            _packetService = packetService;
            _packetTask = Task.Run(() => ProcessPacketsAsync(packetService, runCts.Token));
            logger.LogInformation("LanPilot per-application download limiter started with WinDivert {Version}.", packetService.Version);
        }
        catch
        {
            runCts.Dispose();
            packetService?.Dispose();
            throw;
        }
    }

    private async Task StopCoreAsync()
    {
        CancellationTokenSource? runCts = _runCts;
        _policies.Clear();
        if (runCts is null) { Volatile.Write(ref _fault, null); return; }

        _bypassLimits = true;
        _packetService?.Shutdown(DivertShutdown.Receive);
        foreach (PolicyQueue queue in _queues.Values) queue.Wake();
        Task[] tasks = [_packetTask ?? Task.CompletedTask];
        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException) { }

        foreach (PolicyQueue queue in _queues.Values) queue.Complete();
        Task[] drainTasks = _queues.Values.Select(queue => queue.Completion).ToArray();
        try
        {
            await Task.WhenAll(drainTasks).WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException) { }
        await runCts.CancelAsync();
        try { await Task.WhenAll(tasks.Concat(drainTasks)).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { Volatile.Write(ref _fault, "Application limiter shutdown incomplete; I/O resources retained safely"); throw; }

        _queues.Clear();
        _packetService?.Dispose();
        _packetService = null;
        _packetTask = null;
        _runCts = null;
        Volatile.Write(ref _fault, null);
        _policies.Clear();
        runCts.Dispose();
        logger.LogInformation("LanPilot per-application download limiter stopped; packet handling is fail-open.");
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
                if (Environment.TickCount64 - _lastQueuePrune > 60000)
                {
                    _lastQueuePrune = Environment.TickCount64;
                    // This is the only queue producer. A zero reservation includes
                    // both channel contents and native sends, so retiring is safe.
                    foreach (var entry in _queues)
                        if (!_policies.ContainsKey(entry.Key) && _memoryBudget.ReservedFor(entry.Key) == 0 && _queues.TryRemove(entry))
                            entry.Value.Complete();
                }
                _diagnostics.Received();
                DivertAddress address = addresses[0];

                if (!_bypassLimits && TryReadInboundFlow(buffer.AsSpan(0, result.DataLength), out FlowKey key) &&
                    _flows.TryGet(key, out string? policyId) && policyId is not null &&
                    _policies.TryGetValue(policyId, out LocalApplicationPolicy? policy) &&
                    policy.DownloadLimitBitsPerSecond is long)
                {
                    PolicyQueue queue = _queues.GetOrAdd(policyId, id =>
                        new PolicyQueue(id, service, GetRate, logger, _diagnostics, _memoryBudget, cancellationToken,
                            () => Volatile.Write(ref _fault, "Application queued send failed")));
                    // A full queue means the selected application is exceeding
                    // its limit. Dropping this packet lets TCP apply backpressure
                    // without delaying unrelated Windows traffic.
                    _diagnostics.Limited();
                    if (!_memoryBudget.TryReserve(policyId, result.DataLength)) { _diagnostics.QueueFull(); continue; }
                    bool accepted = false;
                    try { accepted = queue.TryEnqueue(new QueuedPacket(buffer.AsSpan(0, result.DataLength).ToArray(), address)); }
                    finally { if (!accepted) { _memoryBudget.Release(policyId, result.DataLength); _diagnostics.QueueFull(); } }
                    continue;
                }

                await SendBoundedAsync(service, buffer.AsMemory(0, result.DataLength), address, cancellationToken);
                _diagnostics.Sent();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (System.ComponentModel.Win32Exception ex) when (_bypassLimits && ex.NativeErrorCode == 232) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Application packet limiter stopped unexpectedly.");
            _diagnostics.Error();
            Volatile.Write(ref _fault, "Application packet limiter failed");
        }
    }

    private long? GetRate(string policyId) =>
        !_bypassLimits && _policies.TryGetValue(policyId, out LocalApplicationPolicy? policy)
            ? policy.DownloadLimitBitsPerSecond
            : null;

    private static async Task SendBoundedAsync(DivertService service, ReadOnlyMemory<byte> packet,
        DivertAddress address, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await service.SendAsync(packet, new[] { address }, timeout.Token);
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
            int totalLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
            if (totalLength < headerLength + 4 || totalLength > packet.Length) return false;
            // Non-initial fragments do not contain TCP/UDP ports.
            if ((System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(6, 2)) & 0x3FFF) != 0) return false;
            byte protocol = packet[9];
            if (protocol is not (6 or 17)) return false;
            if (!IsValidTransport(packet.Slice(headerLength, totalLength - headerLength), protocol)) return false;
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
            int payloadLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
            if (payloadLength < 4 || payloadLength + 40 > packet.Length) return false;
            int offset = 40;
            int end = payloadLength + 40;
            for (int headers = 0; protocol is not (6 or 17); headers++)
            {
                if (headers == 8 || offset + 2 > end) return false;
                int length;
                if (protocol is 0 or 43 or 60) length = (packet[offset + 1] + 1) * 8;
                else if (protocol == 51) length = (packet[offset + 1] + 2) * 4;
                else if (protocol == 44)
                {
                    if (offset + 8 > end || System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2)) != 0) return false;
                    length = 8; // An atomic fragment has a complete transport header.
                }
                else return false;
                protocol = packet[offset];
                offset += length;
                if (offset > end) return false;
            }
            if (!IsValidTransport(packet.Slice(offset, end - offset), protocol)) return false;
            IPAddress source = new(packet.Slice(8, 16));
            IPAddress destination = new(packet.Slice(24, 16));
            ushort sourcePort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            ushort destinationPort = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
            IPAddress local = outbound ? source : destination;
            IPAddress remote = outbound ? destination : source;
            ushort localPort = outbound ? sourcePort : destinationPort;
            ushort remotePort = outbound ? destinationPort : sourcePort;
            key = new(local, remote, localPort, remotePort, protocol);
            return true;
        }

        return false;
    }

    private static bool IsValidTransport(ReadOnlySpan<byte> transport, byte protocol)
    {
        if (protocol == 6)
            return transport.Length >= 20 && (transport[12] >> 4) * 4 is int header && header >= 20 && header <= transport.Length;
        if (transport.Length < 8) return false;
        int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(transport.Slice(4, 2));
        return length >= 8 && length <= transport.Length;
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
        private readonly PacketDiagnostics _diagnostics;
        private readonly PacketMemoryBudget _memoryBudget;
        private readonly CancellationToken _stoppingToken;
        private readonly Action _onFault;
        private readonly SemaphoreSlim _changed = new(0, 1);
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
        private readonly RateLimiter _limiter = new(null);

        public PolicyQueue(string policyId, DivertService service, Func<string, long?> getRate, ILogger logger,
            PacketDiagnostics diagnostics, PacketMemoryBudget memoryBudget, CancellationToken stoppingToken, Action onFault)
        {
            _policyId = policyId;
            _service = service;
            _getRate = getRate;
            _logger = logger;
            _diagnostics = diagnostics;
            _memoryBudget = memoryBudget;
            _stoppingToken = stoppingToken;
            _onFault = onFault;
            _worker = Task.Run(ProcessAsync);
        }

        public Task Completion => _worker;
        public int QueuedPackets => _channel.Reader.Count;
        public bool TryEnqueue(QueuedPacket packet) => _channel.Writer.TryWrite(packet);
        public void Complete() => _channel.Writer.TryComplete();
        public void Wake() { try { _changed.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }

        private async Task ProcessAsync()
        {
            try
            {
                await foreach (QueuedPacket packet in _channel.Reader.ReadAllAsync(_stoppingToken))
                {
                    try
                    {
                        while (true)
                        {
                            _stoppingToken.ThrowIfCancellationRequested();
                            _limiter.UpdateRate(_getRate(_policyId));
                            if (_limiter.TryConsume(packet.Data.Length)) break;
                            await _changed.WaitAsync(_limiter.TimeUntilAvailable(packet.Data.Length), _stoppingToken);
                        }

                        await SendBoundedAsync(_service, packet.Data, packet.Address, _stoppingToken);
                        _diagnostics.Sent();
                    }
                    catch (Exception ex)
                    {
                        if (ex is OperationCanceledException && _stoppingToken.IsCancellationRequested) break;
                        _logger.LogWarning(ex, "A queued application packet could not be reinjected.");
                        _diagnostics.Error();
                        _onFault();
                        break;
                    }
                    finally { _memoryBudget.Release(_policyId, packet.Data.Length); }
                }
            }
            catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested) { }
            finally
            {
                _channel.Writer.TryComplete();
                while (_channel.Reader.TryRead(out QueuedPacket? packet)) _memoryBudget.Release(_policyId, packet.Data.Length);
                _changed.Dispose();
            }
        }
    }
}
