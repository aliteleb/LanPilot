using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using LanPilot.Contracts;
using LanPilot.Service.Persistence;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace LanPilot.Service.Engine;

public sealed class TrafficEngine(ILogger<TrafficEngine> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TrafficTarget> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ObservedDevice> _observedByIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TrafficCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private LibPcapLiveDevice? _device;
    private NetworkAdapterInfo? _adapter;
    private NetworkProfile? _network;
    private PhysicalAddress? _localMac;
    private PhysicalAddress? _gatewayMac;
    private Timer? _poisonTimer;
    private bool _managePolicies;

    public bool IsRunning => _device?.Started == true;

    public async Task StartAsync(
        NetworkAdapterInfo adapter,
        NetworkProfile network,
        IEnumerable<DeviceSnapshot> devices,
        CancellationToken cancellationToken,
        bool managePolicies = true)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(true);
            _adapter = adapter;
            _network = network;
            _managePolicies = managePolicies;
            _localMac = GetLocalMac(adapter.Id);
            _gatewayMac = ParseMac(network.GatewayMac);
            if (_localMac is null || _gatewayMac is null)
            {
                throw new InvalidOperationException("The local or gateway MAC address is unavailable.");
            }

            LibPcapLiveDevice? capture = FindCaptureDevice(adapter);
            if (capture is null)
            {
                throw new InvalidOperationException("Npcap could not open the selected network adapter.");
            }

            UpdateTargets(devices);
            capture.OnPacketArrival += OnPacketArrival;
            capture.Open(DeviceModes.MaxResponsiveness, 1);
            string localMacFilter = string.Join(":", _localMac.GetAddressBytes().Select(value => value.ToString("x2")));
            capture.Filter = $"ip and (ether dst {localMacFilter} or (ether src {localMacFilter} and host {adapter.Ipv4Address}))";
            _device = capture;
            capture.StartCapture();
            _poisonTimer = new Timer(_ => MaintainRedirects(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            logger.LogInformation(
                managePolicies ? "Traffic control started on {Adapter}." : "Real-time device monitoring started on {Adapter}.",
                adapter.Name);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void UpdateTargets(IEnumerable<DeviceSnapshot> devices)
    {
        DeviceSnapshot[] current = devices.Where(item => item.IsOnline).ToArray();
        Dictionary<string, ObservedDevice> observed = current
            .Where(item => !item.IsGateway)
            .Select(item => new ObservedDevice(item.Id, IPAddress.Parse(item.Ipv4Address), item.IsLocalComputer))
            .ToDictionary(item => item.IpAddress.ToString(), StringComparer.OrdinalIgnoreCase);
        _observedByIp.Clear();
        foreach ((string ip, ObservedDevice device) in observed) _observedByIp[ip] = device;

        DeviceSnapshot[] active = current
            .Where(item => !item.IsGateway && !item.IsLocalComputer)
            .Select(item => _managePolicies
                ? item
                : item with
                {
                    Policy = item.Policy with
                    {
                        BlockInternet = false,
                        DownloadLimitBitsPerSecond = null,
                        UploadLimitBitsPerSecond = null
                    }
                })
            .ToArray();
        HashSet<string> activeIds = active.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (DeviceSnapshot device in active)
        {
            IPAddress ipAddress = IPAddress.Parse(device.Ipv4Address);
            PhysicalAddress mac = ParseMac(device.MacAddress)
                ?? throw new InvalidDataException("A device MAC address is invalid.");

            if (_targets.TryGetValue(device.Id, out TrafficTarget? existing) &&
                existing.IpAddress.Equals(ipAddress) && existing.Mac.Equals(mac))
            {
                existing.UpdatePolicy(device.Policy);
                continue;
            }

            if (_targets.TryRemove(device.Id, out TrafficTarget? replaced))
            {
                RestoreTarget(replaced);
            }

            _targets[device.Id] = new TrafficTarget(device.Id, ipAddress, mac, device.Policy);
        }

        foreach (string removedId in _targets.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            if (_targets.TryRemove(removedId, out TrafficTarget? removed))
            {
                RestoreTarget(removed);
            }
        }
    }

    public IReadOnlyDictionary<string, TrafficCounter> ReadAndResetCounters()
    {
        Dictionary<string, TrafficCounter> result = [];
        foreach ((string key, TrafficCounter value) in _counters)
        {
            result[key] = value.Reset();
        }
        return result;
    }

    public void ResetCounters(string deviceId) => _counters.TryRemove(deviceId, out _);

    public async Task StopAsync(bool restore, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(restore);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task RestoreAbandonedSessionAsync(
        ControlSession session,
        CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopCoreAsync(false);
            _adapter = session.Adapter;
            _network = session.Network;
            _managePolicies = true;
            _localMac = GetLocalMac(session.Adapter.Id);
            _gatewayMac = ParseMac(session.Network.GatewayMac);
            if (_localMac is null || _gatewayMac is null)
            {
                throw new InvalidOperationException("Recovery could not resolve the local or gateway MAC address.");
            }

            LibPcapLiveDevice? capture = FindCaptureDevice(session.Adapter);
            if (capture is null)
            {
                throw new InvalidOperationException("Recovery could not open the previous network adapter.");
            }

            _device = capture;
            capture.Open(DeviceModes.Promiscuous, 50);
            UpdateTargets(session.Targets.Select(item => item with { IsOnline = true }));
            for (int attempt = 0; attempt < 5; attempt++)
            {
                RestoreTargets();
                await Task.Delay(80, cancellationToken);
            }

            logger.LogInformation(
                "Recovered network state from the interrupted control session started at {StartedAt}.",
                session.StartedAt);
        }
        finally
        {
            await StopCoreAsync(false);
            _lifecycle.Release();
        }
    }

    private Task StopCoreAsync(bool restore)
    {
        _poisonTimer?.Dispose();
        _poisonTimer = null;

        if (_device is not null)
        {
            if (restore)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    RestoreTargets();
                    Thread.Sleep(80);
                }
            }

            try
            {
                if (_device.Started) _device.StopCapture();
                _device.OnPacketArrival -= OnPacketArrival;
                _device.Close();
                _device.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Npcap adapter shutdown reported an error.");
            }
        }

        _device = null;
        _targets.Clear();
        _observedByIp.Clear();
        _managePolicies = false;
        return Task.CompletedTask;
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        try
        {
            RawCapture raw = capture.GetPacket();
            Packet parsed = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            EthernetPacket? ethernet = parsed.Extract<EthernetPacket>();
            IPv4Packet? ipv4 = parsed.Extract<IPv4Packet>();
            if (ethernet is null || ipv4 is null || _device is null || _localMac is null || _gatewayMac is null)
            {
                return;
            }

            if (ethernet.SourceHardwareAddress.Equals(_localMac))
            {
                if (_observedByIp.TryGetValue(ipv4.SourceAddress.ToString(), out ObservedDevice? localSource) &&
                    localSource.IsLocalComputer)
                {
                    AddCounter(localSource.DeviceId, true, raw.Data.Length);
                }
                return;
            }

            bool upload = IsUploadFrame(ethernet.SourceHardwareAddress, _gatewayMac);
            string observedIp = upload
                ? ipv4.SourceAddress.ToString()
                : ipv4.DestinationAddress.ToString();
            if (!_observedByIp.TryGetValue(observedIp, out ObservedDevice? observed)) return;

            if (observed.IsLocalComputer)
            {
                AddCounter(observed.DeviceId, upload, raw.Data.Length);
                return;
            }

            if (!_targets.TryGetValue(observed.DeviceId, out TrafficTarget? target))
            {
                AddCounter(observed.DeviceId, upload, raw.Data.Length);
                return;
            }

            RateLimiter limiter = upload ? target.UploadLimiter : target.DownloadLimiter;
            if (target.Policy.BlockInternet || !limiter.TryConsume(raw.Data.Length))
            {
                return;
            }

            byte[] forwarded = raw.Data.ToArray();
            ReadOnlySpan<byte> destination = upload
                ? _gatewayMac.GetAddressBytes()
                : target.Mac.GetAddressBytes();
            ReadOnlySpan<byte> source = _localMac.GetAddressBytes();
            destination.CopyTo(forwarded.AsSpan(0, 6));
            source.CopyTo(forwarded.AsSpan(6, 6));
            _device.SendPacket(forwarded);

            AddCounter(target.DeviceId, upload, raw.Data.Length);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A captured packet could not be processed.");
        }
    }

    private void AddCounter(string deviceId, bool upload, int bytes) =>
        _counters.GetOrAdd(deviceId, _ => new TrafficCounter()).Add(upload, bytes);

    private void MaintainRedirects()
    {
        try
        {
            if (_device is null || _network is null || _adapter is null || _localMac is null || _gatewayMac is null) return;
            IPAddress gatewayIp = IPAddress.Parse(_network.GatewayIpv4);
            IPAddress localIp = IPAddress.Parse(_adapter.Ipv4Address);
            foreach (TrafficTarget target in _targets.Values)
            {
                _device.SendPacket(BuildArpReply(target.Mac, _localMac, gatewayIp, target.Mac, target.IpAddress));
                _device.SendPacket(BuildArpReply(_gatewayMac, _localMac, target.IpAddress, _gatewayMac, gatewayIp));

                // Raw ARP injection can also be observed by the local Windows
                // stack. Reassert the genuine gateway and device mappings so
                // LanPilot never poisons its own host while redirecting peers.
                _device.SendPacket(BuildArpReply(_localMac, _gatewayMac, gatewayIp, _localMac, localIp));
                _device.SendPacket(BuildArpReply(_localMac, target.Mac, target.IpAddress, _localMac, localIp));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The ARP maintenance cycle failed.");
        }
    }

    private void RestoreTargets()
    {
        foreach (TrafficTarget target in _targets.Values)
        {
            RestoreTarget(target);
        }
    }

    private void RestoreTarget(TrafficTarget target)
    {
        if (_device is null || _network is null || _gatewayMac is null) return;
        IPAddress gatewayIp = IPAddress.Parse(_network.GatewayIpv4);
        _device.SendPacket(BuildArpReply(target.Mac, _gatewayMac, gatewayIp, target.Mac, target.IpAddress));
        _device.SendPacket(BuildArpReply(_gatewayMac, target.Mac, target.IpAddress, _gatewayMac, gatewayIp));
    }

    public static byte[] BuildArpReply(
        PhysicalAddress destinationMac,
        PhysicalAddress sourceMac,
        IPAddress senderIp,
        PhysicalAddress targetMac,
        IPAddress targetIp)
    {
        byte[] frame = new byte[42];
        destinationMac.GetAddressBytes().CopyTo(frame, 0);
        sourceMac.GetAddressBytes().CopyTo(frame, 6);
        frame[12] = 0x08;
        frame[13] = 0x06;
        frame[14] = 0x00;
        frame[15] = 0x01;
        frame[16] = 0x08;
        frame[17] = 0x00;
        frame[18] = 0x06;
        frame[19] = 0x04;
        frame[20] = 0x00;
        frame[21] = 0x02;
        sourceMac.GetAddressBytes().CopyTo(frame, 22);
        senderIp.GetAddressBytes().CopyTo(frame, 28);
        targetMac.GetAddressBytes().CopyTo(frame, 32);
        targetIp.GetAddressBytes().CopyTo(frame, 38);
        return frame;
    }

    public static bool IsUploadFrame(PhysicalAddress sourceMac, PhysicalAddress gatewayMac) =>
        !sourceMac.Equals(gatewayMac);

    private static PhysicalAddress? GetLocalMac(string adapterId) =>
        NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(item => string.Equals(item.Id, adapterId, StringComparison.OrdinalIgnoreCase))
            ?.GetPhysicalAddress();

    private static LibPcapLiveDevice? FindCaptureDevice(NetworkAdapterInfo adapter) =>
        LibPcapLiveDeviceList.Instance.FirstOrDefault(item =>
            item.Name.Contains(adapter.Id, StringComparison.OrdinalIgnoreCase) ||
            item.Description?.Contains(adapter.Description, StringComparison.OrdinalIgnoreCase) == true);

    private static PhysicalAddress? ParseMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = new(value.Where(Uri.IsHexDigit).ToArray());
        return normalized.Length == 12 ? PhysicalAddress.Parse(normalized) : null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(true, CancellationToken.None);
        _lifecycle.Dispose();
    }

    private sealed class TrafficTarget
    {
        public TrafficTarget(string deviceId, IPAddress ipAddress, PhysicalAddress mac, DevicePolicy policy)
        {
            DeviceId = deviceId;
            IpAddress = ipAddress;
            Mac = mac;
            Policy = policy;
            DownloadLimiter = new RateLimiter(policy.DownloadLimitBitsPerSecond);
            UploadLimiter = new RateLimiter(policy.UploadLimitBitsPerSecond);
        }

        public string DeviceId { get; }
        public IPAddress IpAddress { get; }
        public PhysicalAddress Mac { get; }
        public DevicePolicy Policy { get; private set; }
        public RateLimiter DownloadLimiter { get; }
        public RateLimiter UploadLimiter { get; }

        public void UpdatePolicy(DevicePolicy policy)
        {
            Policy = policy;
            DownloadLimiter.UpdateRate(policy.DownloadLimitBitsPerSecond);
            UploadLimiter.UpdateRate(policy.UploadLimitBitsPerSecond);
        }
    }

    private sealed record ObservedDevice(string DeviceId, IPAddress IpAddress, bool IsLocalComputer);
}

public sealed class TrafficCounter
{
    private long _downloadBytes;
    private long _uploadBytes;

    public long DownloadBytes => Interlocked.Read(ref _downloadBytes);
    public long UploadBytes => Interlocked.Read(ref _uploadBytes);

    public void Add(bool upload, int bytes)
    {
        if (upload) Interlocked.Add(ref _uploadBytes, bytes);
        else Interlocked.Add(ref _downloadBytes, bytes);
    }

    public TrafficCounter Reset() =>
        new()
        {
            _downloadBytes = Interlocked.Exchange(ref _downloadBytes, 0),
            _uploadBytes = Interlocked.Exchange(ref _uploadBytes, 0)
        };
}
