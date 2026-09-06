using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using LanPilot.Contracts;
using LanPilot.Service.Engine;
using LanPilot.Service.Persistence;
using LanPilot.Service.Diagnostics;

namespace LanPilot.Service;

public sealed class LanPilotCoordinator(
    SqliteStore store,
    ControlSessionJournal sessionJournal,
    NetworkScanner scanner,
    TrafficEngine trafficEngine,
    PolicyResolver policyResolver,
    IApplicationPolicyController applicationTrafficController,
    DiagnosticRecorder diagnostics,
    ApplicationDownloadLimiter applicationLimiter,
    ApplicationTrafficMonitor applicationMonitor,
    ILogger<LanPilotCoordinator> logger,
    ControlSafetyJournal? safetyJournal = null)
{
    private readonly ControlSafetyJournal _safetyJournal = safetyJournal ?? new();
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly object _cancellationGate = new();
    private CancellationTokenSource _controlCancellation = new();
    private CancellationTokenSource? _scanCancellation;
    private ControlSafetyStatus _safety = new("None", false, true, false, DateTimeOffset.UtcNow);
    public bool IsInitialized { get; private set; }
    public bool IsTransitioning { get; private set; }
    public bool IsSuspended => _safety.Reason != "None";
    public bool ExpectsDeviceControl => _status.Mode == EngineMode.Controlling && !IsSuspended;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, DeviceSnapshot> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GroupPolicy> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ScheduleRule> _schedules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RulePreset> _presets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalApplicationPolicy> _applicationPolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkProfile> _networks = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<NetworkAdapterInfo> _adapters = [];
    private NetworkProfile? _network;
    private AppSettings _settings = new(null, false, 30, true, false, false, true);
    private EngineStatus _status = new(
        EngineMode.Idle, "Starting LanPilot service…", false, null, false, false, null, DateTimeOffset.Now);
    private DateTimeOffset _lastRollup = DateTimeOffset.Now;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCounterRead = DateTimeOffset.Now;
    private DateTimeOffset _lastOnlineRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastKnownDeviceRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNpcapRefresh = DateTimeOffset.MinValue;
    private Task? _deviceRefreshTask;
    private Task? _networkRefreshTask;
    private DateTimeOffset _lastNetworkRefresh;
    private int _controlGeneration;
    private long _lastTickStarted, _lastTickCompleted;
    private readonly ConcurrentDictionary<string, (long Down, long Up)> _minuteCounters = new();
    private static readonly TimeSpan OnlineRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KnownDeviceRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NpcapRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OfflineGracePeriod = TimeSpan.FromSeconds(45);

    public event EventHandler? SnapshotChanged;
    public event EventHandler<NotificationEvent>? NotificationRaised;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        _settings = (await store.LoadSettingsAsync(cancellationToken)
            ?? new AppSettings(null, false, 30, true, false, false, true)) with
        {
            // Disabled after transparent ARP monitoring proved disruptive on
            // some networks. Keep the field only for settings compatibility.
            ActiveDeviceMonitoring = false
        };

        foreach (NetworkProfile network in await store.LoadNetworksAsync(cancellationToken)) _networks[network.Id] = network;
        foreach (DeviceSnapshot device in await store.LoadDevicesAsync(cancellationToken)) _devices[device.Id] = device with { IsOnline = false };
        foreach (GroupPolicy group in await store.LoadGroupsAsync(cancellationToken)) _groups[group.Id] = group;
        await EnsureDefaultGroupsAsync(cancellationToken);
        foreach (ScheduleRule schedule in await store.LoadSchedulesAsync(cancellationToken)) _schedules[schedule.Id] = schedule;
        foreach (RulePreset preset in await store.LoadPresetsAsync(cancellationToken)) _presets[preset.Id] = preset;
        foreach (LocalApplicationPolicy policy in await store.LoadApplicationPoliciesAsync(cancellationToken))
        {
            _applicationPolicies[policy.Id] = policy;
        }
        try { _safety = await _safetyJournal.LoadAsync(cancellationToken) ?? _safety; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Control safety state could not be loaded; requiring manual resume.");
            _safety = new("Fault", true, false, false, DateTimeOffset.UtcNow);
        }
        ControlSession? abandonedSession = null;
        bool startupRestored = true;
        try { abandonedSession = await sessionJournal.LoadAsync(cancellationToken); }
        catch (Exception ex)
        {
            startupRestored = false;
            _safety = new("Fault", true, false, false, DateTimeOffset.UtcNow);
            logger.LogError(ex, "The recovery journal is unreadable. Manual recovery is required.");
        }
        if (abandonedSession is not null || _safety.ApplicationsActive)
            _safety = new("Fault", true, false, false, DateTimeOffset.UtcNow);
        if (IsSuspended)
        {
            try { await applicationTrafficController.SuspendAllAsync(cancellationToken); }
            catch (Exception ex) { startupRestored = false; logger.LogWarning(ex, "Startup policy cleanup was incomplete."); }
        }

        (bool available, string? version) = NpcapDetector.Detect();
        _lastNpcapRefresh = DateTimeOffset.Now;
        _adapters = await scanner.GetAdaptersAsync(_settings.SelectedAdapterId, cancellationToken);
        NetworkAdapterInfo? selected = SelectAdapter(_settings.SelectedAdapterId);
        bool ipv6 = DetectIpv6();
        if (abandonedSession is not null && !available) startupRestored = false;

        if (available && abandonedSession is not null)
        {
            SetStatus(EngineMode.Recovering, "Restoring network state from an interrupted control session…", available, version, ipv6);
            try
            {
                await trafficEngine.RestoreAbandonedSessionAsync(abandonedSession, cancellationToken);
                sessionJournal.Clear();
            }
            catch (Exception ex)
            {
                startupRestored = false;
                logger.LogWarning(ex, "Could not restore an abandoned traffic-control session.");
                NotificationRaised?.Invoke(this, new NotificationEvent(
                    "Network recovery warning",
                    "Automatic ARP recovery could not be completed. Use Emergency Pause after checking the adapter.",
                    NotificationSeverity.Warning));
            }
        }
        _safety = _safety with { RestorationComplete = startupRestored, UpdatedAt = DateTimeOffset.UtcNow };
        await _safetyJournal.SaveAsync(_safety, cancellationToken);
        IsInitialized = true;
        if (selected is null)
        {
            SetStatus(EngineMode.DriverUnavailable, "Device control unavailable. Application control can be resumed independently.", available, version, ipv6);
            return;
        }
        _settings = _settings with { SelectedAdapterId = selected.Id };
        await store.SaveSettingsAsync(_settings, cancellationToken);
        _network = BuildKnownProfile(selected);
        await store.SaveNetworkAsync(_network, cancellationToken);

        SetStatus(
            available ? EngineMode.Idle : EngineMode.DriverUnavailable,
            available ? "Ready. Select Scan to discover devices." : "Npcap is not installed. Discovery-only mode is available.",
            available,
            version,
            ipv6);

        await ScanAsync(selected.Id, cancellationToken);
        if (!IsSuspended && _settings.AutoControl && _network.AutoControl && available)
        {
            await SetControlAsync(true, cancellationToken);
        }
    }

    public DashboardSnapshot GetSnapshot()
    {
        DeviceSnapshot[] devices = _devices.Values
            .Where(item => _network is null || item.NetworkId == _network.Id)
            .OrderByDescending(item => item.IsGateway)
            .ThenByDescending(item => item.IsLocalComputer)
            .ThenByDescending(item => item.IsOnline)
            .ThenBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DashboardSnapshot(
            _status,
            _adapters,
            _network,
            devices,
            _groups.Values.OrderBy(item => item.Name).ToArray(),
            _schedules.Values.OrderBy(item => item.Name).ToArray(),
            _presets.Values.OrderByDescending(item => item.CreatedAt).ToArray(),
            _settings, _safety with { DevicesActive = trafficEngine.IsRunning }, applicationMonitor.IsAvailable);
    }

    public async Task<OperationResult> ScanAsync(string? adapterId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        bool controlAcquired = false;
        CancellationTokenSource? scan = null;
        try
        {
            await _controlGate.WaitAsync(cancellationToken);
            controlAcquired = true;
            lock (_cancellationGate)
            {
                scan = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _scanCancellation = scan;
            }
            cancellationToken = scan.Token;
            NetworkAdapterInfo? adapter = SelectAdapter(adapterId);
            if (adapter is null)
            {
                return new OperationResult(false, "The selected adapter is unavailable.");
            }

            if (trafficEngine.IsRunning)
            {
                await trafficEngine.StopAsync(true, cancellationToken);
                sessionJournal.Clear();
            }

            _settings = _settings with { SelectedAdapterId = adapter.Id };
            await store.SaveSettingsAsync(_settings, cancellationToken);
            _network = BuildKnownProfile(adapter);
            await store.SaveNetworkAsync(_network, cancellationToken);
            SetStatus(EngineMode.Discovering, $"Scanning {_network.Ipv4Cidr}…");

            Dictionary<string, DeviceSnapshot> known = _devices.Values
                .Where(item => item.NetworkId == _network.Id)
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<DeviceSnapshot> found = await scanner.ScanAsync(adapter, _network, known, cancellationToken);

            DateTimeOffset now = DateTimeOffset.Now;
            foreach (DeviceSnapshot previous in known.Values)
            {
                _devices[previous.Id] = previous with { IsOnline = false };
            }

            foreach (DeviceSnapshot device in found)
            {
                bool isNew = !known.ContainsKey(device.Id);
                DeviceSnapshot prepared = await PrepareNewDeviceAsync(device, isNew, cancellationToken);
                _devices[prepared.Id] = prepared;
                await store.SaveDeviceAsync(prepared, cancellationToken);
                if (isNew && _settings.NotifyNewDevices && !prepared.IsGateway && !prepared.IsLocalComputer)
                {
                    NotificationRaised?.Invoke(this,
                        new NotificationEvent("New device discovered", $"{prepared.DisplayName} joined this network.", NotificationSeverity.Info));
                }
            }

            SetStatus(
                _status.NpcapAvailable ? EngineMode.Idle : EngineMode.DriverUnavailable,
                $"Scan complete. {found.Count} devices found. Select Start control to track all online devices automatically.");
            RaiseSnapshotChanged();
            return new OperationResult(true, $"Found {found.Count} devices.");
        }
        catch (NotSupportedException ex)
        {
            SetStatus(EngineMode.Faulted, ex.Message);
            return new OperationResult(false, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Network scan failed.");
            SetStatus(EngineMode.Faulted, "The network scan failed. Open Diagnostics for details.");
            return new OperationResult(false, ex.Message);
        }
        finally
        {
            lock (_cancellationGate) { if (ReferenceEquals(_scanCancellation, scan)) _scanCancellation = null; }
            scan?.Dispose();
            if (controlAcquired) _controlGate.Release();
            _gate.Release();
        }
    }

    public async Task<OperationResult> SetControlAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled) return await SuspendAllAsync("UserPause", cancellationToken);
        if (!IsInitialized) return new(false, "Service initialization is still in progress.");
        return await ResumeAsync(true, cancellationToken);
    }

    public async Task<OperationResult> OpenUiAsync(CancellationToken token)
    {
        if (!IsInitialized) return new(false, "Service initialization is still in progress.");
        if (_safety.RequiresManualResume) return new(false, "Control is suspended. Resume manually after checking diagnostics.");
        if (_safety.ApplicationsActive) return new(true, "Application control is already active.");
        return await ResumeAsync(_settings.AutoControl && _network?.AutoControl == true, token);
    }

    private async Task<OperationResult> ResumeAsync(bool devices, CancellationToken cancellationToken)
    {
        int generation = Volatile.Read(ref _controlGeneration);
        await _controlGate.WaitAsync(cancellationToken);
        try
        {
            IsTransitioning = true;
            lock (_cancellationGate)
            {
                if (generation != Volatile.Read(ref _controlGeneration)) return new(false, "Resume superseded by pause.");
                _controlCancellation.Dispose();
                _controlCancellation = new();
            }
            using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _controlCancellation.Token);
            cancellationToken = operation.Token;
            SetStatus(EngineMode.Recovering, "Preparing control; cleaning the previous session…");
            await StopDevicesAndRecoverAsync(cancellationToken);
            // Clean stale Windows rules before applying the saved configuration.
            await applicationTrafficController.SuspendAllAsync(cancellationToken);
            lock (_cancellationGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _controlGeneration)) throw new OperationCanceledException();
                _safety = new("None", false, true, true, DateTimeOffset.UtcNow);
            }
            await _safetyJournal.SaveAsync(_safety, cancellationToken);
            foreach (LocalApplicationPolicy policy in _applicationPolicies.Values)
                await applicationTrafficController.ApplyAsync(policy, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RefreshNpcapStatus(DateTimeOffset.Now, true);
            if (!devices || !_status.NpcapAvailable)
            {
                SetStatus(_status.NpcapAvailable ? EngineMode.Idle : EngineMode.DriverUnavailable,
                    "Application control active. Device control is not active.");
                return new(true, _status.Message);
            }

            NetworkAdapterInfo? adapter = SelectAdapter(_settings.SelectedAdapterId);
            if (adapter is null || _network is null)
            {
                SetStatus(EngineMode.Idle, "Application control active. Select a network adapter for device control.");
                return new(true, _status.Message);
            }

            DeviceSnapshot[] effective = GetEffectiveDevices();
            DeviceSnapshot[] intercepted = effective
                .Where(item => item.IsOnline && !item.IsGateway && !item.IsLocalComputer)
                .ToArray();
            await sessionJournal.SaveAsync(new ControlSession(adapter, _network, intercepted, DateTimeOffset.Now), cancellationToken);
            await trafficEngine.StartAsync(adapter, _network, effective, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _lastOnlineRefresh = DateTimeOffset.Now;
            _lastKnownDeviceRefresh = _lastOnlineRefresh;
            SetStatus(EngineMode.Controlling, BuildControlStatusMessage(effective));
            RaiseSnapshotChanged();
            return new OperationResult(true, _status.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Traffic control could not start.");
            await SuspendCoreAsync(generation == Volatile.Read(ref _controlGeneration) ? "Fault" : _safety.Reason);
            return new OperationResult(false, ex.Message);
        }
        finally
        {
            IsTransitioning = false;
            _controlGate.Release();
        }
    }

    public async Task EmergencyPauseAsync(CancellationToken cancellationToken)
    {
        OperationResult result = await SuspendAllAsync("UserPause", cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Message);
    }

    public async Task<OperationResult> SuspendAllAsync(string reason, CancellationToken cancellationToken, string? failureReason = null)
    {
        lock (_cancellationGate)
        {
            Interlocked.Increment(ref _controlGeneration);
            _safety = new(_safety.Reason == "Fault" ? "Fault" : reason,
                reason is "Fault" or "UserPause" or "NetworkChanged" || _safety.Reason == "Fault", false, _safety.ApplicationsActive, DateTimeOffset.UtcNow,
                failureReason ?? _safety.FailureReason);
            _controlCancellation.Cancel();
            _scanCancellation?.Cancel();
        }
        // Publish intent before waiting for an in-flight command. Persistence failure
        // must never prevent cancellation and independent best-effort cleanup.
        try { await _safetyJournal.SaveAsync(_safety, CancellationToken.None); }
        catch (Exception ex) { logger.LogError(ex, "Could not persist suspension intent."); }
        await _controlGate.WaitAsync(CancellationToken.None);
        try { return await SuspendCoreAsync(_safety.Reason); }
        finally { _controlGate.Release(); }
    }

    private async Task<OperationResult> SuspendCoreAsync(string reason)
    {
        _safety = new(reason, reason is "Fault" or "UserPause" or "NetworkChanged", false, _safety.ApplicationsActive, DateTimeOffset.UtcNow, _safety.FailureReason);
        List<Exception> errors = [];
        try { await _safetyJournal.SaveAsync(_safety, CancellationToken.None); }
        catch (Exception ex) { errors.Add(ex); }
        using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(15));
        // Do not let a failed limiter prevent ARP restoration (or vice versa).
        async Task<Exception?> StopDevices()
        {
            try { await StopDevicesAndRecoverAsync(cleanup.Token); return null; }
            catch (Exception ex) { return ex; }
        }
        async Task<Exception?> StopApplications()
        {
            try { await applicationTrafficController.SuspendAllAsync(cleanup.Token); return null; }
            catch (Exception ex) { return ex; }
        }
        Exception?[] results = await Task.WhenAll(Task.Run(StopDevices), Task.Run(StopApplications));
        errors.AddRange(results.OfType<Exception>());
        _safety = _safety with { RestorationComplete = errors.Count == 0, ApplicationsActive = results[1] is not null, UpdatedAt = DateTimeOffset.UtcNow };
        try { await _safetyJournal.SaveAsync(_safety, CancellationToken.None); }
        catch (Exception ex) { errors.Add(ex); _safety = _safety with { RestorationComplete = false }; }
        foreach (Exception error in errors) logger.LogError(error, "Control restoration step failed.");
        string message = errors.Count == 0 ? "All LanPilot control is suspended. Internet access restored."
            : "Restoration incomplete. Some LanPilot controls may remain active; export diagnostics and retry Emergency pause.";
        SetStatus(errors.Count == 0 ? EngineMode.Idle : EngineMode.Faulted, message);
        diagnostics.Record("LanPilot.Control", errors.Count == 0 ? "Information" : "Error", $"Suspended: {reason}; restored={errors.Count == 0}");
        if (reason == "Fault" || errors.Count != 0)
            NotificationRaised?.Invoke(this, new NotificationEvent("Control suspended", message, NotificationSeverity.Warning));
        RaiseSnapshotChanged();
        return new(errors.Count == 0, message);
    }

    private async Task StopDevicesAndRecoverAsync(CancellationToken token)
    {
        bool wasRunning = trafficEngine.IsRunning;
        await trafficEngine.StopAsync(true, token);
        if (!wasRunning && await sessionJournal.LoadAsync(token) is ControlSession previous)
            await trafficEngine.RestoreAbandonedSessionAsync(previous, token).WaitAsync(TimeSpan.FromSeconds(5), token);
        sessionJournal.Clear();
    }

    public async Task<OperationResult> UpdatePolicyAsync(DevicePolicy policy, CancellationToken cancellationToken)
    {
        if (!_devices.TryGetValue(policy.DeviceId, out DeviceSnapshot? device))
        {
            return new OperationResult(false, "Device not found.");
        }
        if (device.IsGateway || device.IsLocalComputer)
        {
            return new OperationResult(false, "The router and this PC are protected from traffic rules.");
        }
        if (policy.DownloadLimitBitsPerSecond is <= 0 || policy.UploadLimitBitsPerSecond is <= 0)
        {
            return new OperationResult(false, "Configured limits must be positive. Use Unlimited instead of zero.");
        }

        DeviceSnapshot updated = device with { Policy = policy, GroupId = policy.GroupId };
        _devices[updated.Id] = updated;
        await store.SaveDeviceAsync(updated, cancellationToken);
        await RefreshEngineTargetsAsync(cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Device policy saved.");
    }

    public async Task<OperationResult> RenameDeviceAsync(RenameDeviceRequest request, CancellationToken cancellationToken)
    {
        if (!_devices.TryGetValue(request.DeviceId, out DeviceSnapshot? device))
        {
            return new OperationResult(false, "Device not found.");
        }
        string name = request.DisplayName.Trim();
        if (name.Length is < 1 or > 80)
        {
            return new OperationResult(false, "The device name must contain 1 to 80 characters.");
        }

        DeviceSnapshot updated = device with
        {
            DisplayName = name,
            DeviceType = string.IsNullOrWhiteSpace(request.DeviceType) ? "Unknown" : request.DeviceType.Trim()
        };
        _devices[updated.Id] = updated;
        await store.SaveDeviceAsync(updated, cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Device details saved.");
    }

    public async Task<OperationResult> ResetDeviceAsync(ResetDeviceRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_devices.TryGetValue(request.DeviceId, out DeviceSnapshot? device))
                return new OperationResult(false, "Device not found.");
            if (device.IsGateway || device.IsLocalComputer)
                return new OperationResult(false, "The router and this PC cannot be reset.");

            GroupPolicy guests = await GetOrCreateGuestsGroupAsync(cancellationToken);
            DeviceSnapshot reset = ResetDeviceToGroup(device, guests, DateTimeOffset.Now);
            _devices[reset.Id] = reset;
            _minuteCounters.TryRemove(reset.Id, out _);
            trafficEngine.ResetCounters(reset.Id);
            await store.DeleteTrafficHistoryAsync(reset.Id, cancellationToken);
            await store.SaveDeviceAsync(reset, cancellationToken);
            await RefreshEngineTargetsAsync(cancellationToken);
            RaiseSnapshotChanged();
            return new OperationResult(true, $"{reset.DisplayName} was reset and moved to Guests.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResult> SaveGroupAsync(GroupPolicy group, CancellationToken cancellationToken)
    {
        string name = group.Name.Trim();
        if (string.IsNullOrWhiteSpace(group.Id) || name.Length is < 1 or > 80)
            return new OperationResult(false, "A group name between 1 and 80 characters is required.");
        if (group.DownloadLimitBitsPerSecond is <= 0 || group.UploadLimitBitsPerSecond is <= 0)
            return new OperationResult(false, "Group limits must be positive. Use Unlimited instead of zero.");

        GroupPolicy normalized = group with { Name = name };
        _groups[normalized.Id] = normalized;
        await store.SaveGroupAsync(normalized, cancellationToken);
        await RefreshEngineTargetsAsync(cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Group saved.");
    }

    public async Task<OperationResult> DeleteGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        if (!_groups.TryRemove(groupId, out GroupPolicy? removed))
            return new OperationResult(false, "Group not found.");

        await store.DeleteGroupAsync(groupId, cancellationToken);
        GroupPolicy guests = await GetOrCreateGuestsGroupAsync(cancellationToken);

        foreach (DeviceSnapshot device in _devices.Values.Where(item =>
                     string.Equals(item.Policy.GroupId, groupId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            DeviceSnapshot updated = device with
            {
                GroupId = guests.Id,
                Policy = device.Policy with { GroupId = guests.Id }
            };
            _devices[updated.Id] = updated;
            await store.SaveDeviceAsync(updated, cancellationToken);
        }

        foreach (ScheduleRule schedule in _schedules.Values.Where(item =>
                     string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _schedules.TryRemove(schedule.Id, out _);
            await store.DeleteScheduleAsync(schedule.Id, cancellationToken);
        }

        await RefreshEngineTargetsAsync(cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, $"Group '{removed.Name}' deleted. Its devices were moved to Guests.");
    }

    public async Task<OperationResult> SaveScheduleAsync(ScheduleRule schedule, CancellationToken cancellationToken)
    {
        if (schedule.DeviceId is null && schedule.GroupId is null)
            return new OperationResult(false, "A schedule must target a device or group.");
        if (schedule.Days.Length == 0) return new OperationResult(false, "Select at least one day.");
        _schedules[schedule.Id] = schedule;
        await store.SaveScheduleAsync(schedule, cancellationToken);
        await RefreshEngineTargetsAsync(cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Schedule saved.");
    }

    public async Task<OperationResult> DeleteScheduleAsync(string scheduleId, CancellationToken cancellationToken)
    {
        if (!_schedules.TryRemove(scheduleId, out _))
            return new OperationResult(false, "Schedule not found.");
        await store.DeleteScheduleAsync(scheduleId, cancellationToken);
        await RefreshEngineTargetsAsync(cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Schedule deleted.");
    }

    public async Task<OperationResult> SavePresetAsync(SavePresetRequest request, CancellationToken cancellationToken)
    {
        string name = request.Name.Trim();
        if (name.Length is < 1 or > 80)
            return new OperationResult(false, "A preset name between 1 and 80 characters is required.");

        RulePreset preset;
        if (request.PresetId is not null && _presets.TryGetValue(request.PresetId, out RulePreset? existing))
        {
            preset = existing with { Name = name };
        }
        else
        {
            preset = new RulePreset(
                Guid.NewGuid().ToString("N"),
                name,
                _devices.Values.Where(item => !item.IsGateway && !item.IsLocalComputer).Select(item => item.Policy).ToArray(),
                _groups.Values.ToArray(),
                _schedules.Values.ToArray(),
                DateTimeOffset.Now);
        }
        _presets[preset.Id] = preset;
        await store.SavePresetAsync(preset, cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, $"Preset '{name}' saved.");
    }

    public async Task<OperationResult> DeletePresetAsync(string presetId, CancellationToken cancellationToken)
    {
        if (!_presets.TryRemove(presetId, out _))
            return new OperationResult(false, "Preset not found.");
        await store.DeletePresetAsync(presetId, cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Preset deleted.");
    }

    public Task<IReadOnlyList<LocalApplicationSnapshot>> GetApplicationsAsync(CancellationToken cancellationToken) =>
        applicationTrafficController.DiscoverAsync(_applicationPolicies, cancellationToken);

    public async Task<OperationResult> SaveApplicationPolicyAsync(
        LocalApplicationPolicy policy,
        CancellationToken cancellationToken)
    {
        LocalApplicationPolicy normalized = policy with
        {
            DisplayName = policy.DisplayName.Trim(),
            ExecutablePath = Path.GetFullPath(policy.ExecutablePath)
        };
        ApplicationTrafficController.Validate(normalized);
        if (!normalized.BlockInternet &&
            normalized.UploadLimitBitsPerSecond is null &&
            normalized.DownloadLimitBitsPerSecond is null)
        {
            return _applicationPolicies.ContainsKey(normalized.Id)
                ? await DeleteApplicationPolicyAsync(normalized.Id, cancellationToken)
                : new OperationResult(true, "No application restriction is configured.");
        }

        return await ChangeApplicationPolicyAsync(normalized.Id, normalized, cancellationToken);
    }

    public async Task<OperationResult> DeleteApplicationPolicyAsync(string policyId, CancellationToken cancellationToken)
    {
        return await ChangeApplicationPolicyAsync(policyId, null, cancellationToken);
    }

    private async Task<OperationResult> ChangeApplicationPolicyAsync(string id, LocalApplicationPolicy? next, CancellationToken token)
    {
        int generation = Volatile.Read(ref _controlGeneration);
        await _controlGate.WaitAsync(token);
        try
        {
            _applicationPolicies.TryGetValue(id, out LocalApplicationPolicy? previous);
            if (previous is null && next is null) return new(true, "No application restriction is configured.");
            bool apply = !IsSuspended && _safety.ApplicationsActive && generation == Volatile.Read(ref _controlGeneration);
            using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(token, _controlCancellation.Token);
            try
            {
                if (apply)
                {
                    if (next is null) await applicationTrafficController.RemoveAsync(previous!, operation.Token);
                    else await applicationTrafficController.ApplyAsync(next, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                }
                if (next is null) await store.DeleteApplicationPolicyAsync(id, token);
                else await store.SaveApplicationPolicyAsync(next, token);
                if (next is null) _applicationPolicies.TryRemove(id, out _);
                else _applicationPolicies[id] = next;
                return new(true, apply ? "Application policy saved and applied." : "Policy saved. Control remains suspended until resumed.");
            }
            catch (Exception ex)
            {
                if (apply)
                {
                    // Never re-apply a previous blocking rule after an emergency request.
                    if (generation != Volatile.Read(ref _controlGeneration) || IsSuspended)
                        await SuspendCoreAsync(_safety.Reason);
                    else
                    {
                        try
                        {
                            if (previous is null) await applicationTrafficController.RemoveAsync(next!, CancellationToken.None);
                            else await applicationTrafficController.ApplyAsync(previous, CancellationToken.None);
                        }
                        catch (Exception rollback)
                        {
                            logger.LogError(rollback, "Application policy rollback failed.");
                            await SuspendCoreAsync("Fault");
                        }
                    }
                }
                logger.LogWarning(ex, "Application policy transaction failed.");
                return new(false, "Application policy was not saved. Check diagnostics for the failure and restoration result.");
            }
        }
        finally { _controlGate.Release(); }
    }

    public async Task<OperationResult> ApplyPresetAsync(ApplyPresetRequest request, CancellationToken cancellationToken)
    {
        if (!_presets.TryGetValue(request.PresetId, out RulePreset? preset))
            return new OperationResult(false, "Preset not found.");

        foreach (GroupPolicy group in preset.Groups)
        {
            _groups[group.Id] = group;
            await store.SaveGroupAsync(group, cancellationToken);
        }
        foreach (ScheduleRule schedule in preset.Schedules)
        {
            _schedules[schedule.Id] = schedule;
            await store.SaveScheduleAsync(schedule, cancellationToken);
        }
        foreach (DevicePolicy policy in preset.DevicePolicies)
        {
            if (!_devices.TryGetValue(policy.DeviceId, out DeviceSnapshot? device) || device.IsGateway || device.IsLocalComputer)
                continue;
            DeviceSnapshot updated = device with { Policy = policy, GroupId = policy.GroupId };
            _devices[updated.Id] = updated;
            await store.SaveDeviceAsync(updated, cancellationToken);
        }

        await RefreshEngineTargetsAsync(cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, $"Preset '{preset.Name}' applied.");
    }

    public async Task<OperationResult> UpdateNetworkSettingsAsync(
        UpdateNetworkSettingsRequest request,
        CancellationToken cancellationToken)
    {
        NetworkAdapterInfo? adapter = SelectAdapter(request.AdapterId);
        if (adapter is null) return new OperationResult(false, "Adapter not found.");
        if (_network is not null && (_network.Id != NetworkScanner.BuildProfile(adapter).Id || _settings.SelectedAdapterId != adapter.Id))
        {
            return await ChangeNetworkIdentityAsync(adapter, _adapters, request.AutoControl, cancellationToken);
        }
        await _controlGate.WaitAsync(cancellationToken);
        try
        {
            _settings = _settings with { SelectedAdapterId = adapter.Id, AutoControl = request.AutoControl };
            _network = BuildKnownProfile(adapter) with { AutoControl = request.AutoControl };
            _networks[_network.Id] = _network;
            await store.SaveSettingsAsync(_settings, cancellationToken);
            await store.SaveNetworkAsync(_network, cancellationToken);
            RaiseSnapshotChanged();
            return new OperationResult(true, "Network settings saved.");
        }
        finally { _controlGate.Release(); }
    }

    private async Task<OperationResult> ChangeNetworkIdentityAsync(NetworkAdapterInfo? adapter,
        IReadOnlyList<NetworkAdapterInfo> adapters, bool autoControl, CancellationToken token)
    {
        OperationResult stopped = await SuspendAllAsync("NetworkChanged", token);
        if (!stopped.Success) return stopped;
        await _controlGate.WaitAsync(token);
        try
        {
            // Invalidate resumes queued before the new network identity is committed.
            lock (_cancellationGate) { Interlocked.Increment(ref _controlGeneration); _controlCancellation.Cancel(); }
            if (trafficEngine.IsRunning || _safety.ApplicationsActive)
            {
                stopped = await SuspendCoreAsync(_safety.Reason == "Fault" ? "Fault" : "NetworkChanged");
                if (!stopped.Success) return stopped;
            }
            _adapters = adapters;
            _settings = _settings with { SelectedAdapterId = adapter?.Id ?? _settings.SelectedAdapterId, AutoControl = autoControl };
            _network = adapter is null ? null : BuildKnownProfile(adapter) with { AutoControl = autoControl };
            await store.SaveSettingsAsync(_settings, token);
            if (_network is not null)
            {
                _networks[_network.Id] = _network;
                await store.SaveNetworkAsync(_network, token);
            }
            RaiseSnapshotChanged();
            return new(true, "Network changed safely. Scan and resume control manually.");
        }
        finally { _controlGate.Release(); }
    }

    public async Task<OperationResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.SelectedAdapterId is string adapterId && adapterId != _settings.SelectedAdapterId)
        {
            OperationResult changed = await UpdateNetworkSettingsAsync(new(adapterId, settings.AutoControl), cancellationToken);
            if (!changed.Success) return changed;
        }
        _settings = settings with
        {
            HistoryRetentionDays = Math.Clamp(settings.HistoryRetentionDays, 1, 365),
            ActiveDeviceMonitoring = false
        };
        if (_settings.AutoAssignNewDevicesToGuests)
        {
            await GetOrCreateGuestsGroupAsync(cancellationToken);
        }
        await store.SaveSettingsAsync(_settings, cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Settings saved.");
    }

    public async Task<OperationResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        await store.ExportAsync(request.DestinationPath, request.IncludeHistory, cancellationToken);
        return new OperationResult(true, "Backup exported.");
    }

    public async Task<OperationResult> ImportAsync(ImportRequest request, CancellationToken cancellationToken)
    {
        await EmergencyPauseAsync(cancellationToken);
        await store.ImportAsync(request.SourcePath, cancellationToken);
        await ReloadAsync(cancellationToken);
        return new OperationResult(true, "Backup imported.");
    }

    public object GetDiagnosticState(bool includePolicies) => new
    {
        mode = _status.Mode,
        safety = _safety,
        _status.NpcapAvailable,
        _status.NpcapVersion,
        _status.Ipv6Detected,
        settings = _settings,
        networkId = _network?.Id,
        controlGeneration = Volatile.Read(ref _controlGeneration),
        lastTickStarted = Interlocked.Read(ref _lastTickStarted),
        lastTickCompleted = Interlocked.Read(ref _lastTickCompleted),
        currentTick = Environment.TickCount64,
        discoveryTask = _deviceRefreshTask?.Status.ToString(),
        devices = _devices.Count,
        online = _devices.Values.Count(device => device.IsOnline),
        applicationPolicyCount = _applicationPolicies.Count,
        schedules = _schedules.Count,
        // Do not include custom names, executable paths, or traffic destinations.
        effectiveDevicePolicies = GetEffectiveDevices().Take(includePolicies ? 512 : 32).Select(device => new
        {
            device.Id,
            device.IsOnline,
            device.IsLocalComputer,
            device.IsGateway,
            device.Policy
        }).ToArray(),
        applicationPolicies = _applicationPolicies.Values.Take(includePolicies ? 512 : 32).Select(policy => new
        {
            policy.Id,
            policy.BlockInternet,
            policy.DownloadLimitBitsPerSecond,
            policy.UploadLimitBitsPerSecond
        }).ToArray(),
        scheduleRules = includePolicies ? _schedules.Values.Take(512).Select(rule => new
        {
            rule.Id,
            rule.DeviceId,
            rule.GroupId,
            rule.Enabled,
            rule.Start,
            rule.End,
            rule.Days,
            rule.BlockInternet,
            rule.DownloadLimitBitsPerSecond,
            rule.UploadLimitBitsPerSecond,
            active = PolicyResolver.IsActive(rule, DateTimeOffset.Now)
        }).ToArray() : null,
        policyExportLimit = includePolicies ? 512 : 32
    };

    public async Task<OperationResult> ExportDiagnosticsAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        string destination = Path.GetFullPath(request.DestinationPath);
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = destination + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);

        await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
        using (ZipArchive archive = new(file, ZipArchiveMode.Create))
        {
            ZipArchiveEntry reportEntry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
            await using (Stream report = reportEntry.Open())
                await JsonSerializer.SerializeAsync(report, new
                {
                    formatVersion = 2,
                    generatedAt = DateTimeOffset.Now,
                    versions = DiagnosticWorker.CaptureSafely(DiagnosticWorker.CaptureVersions),
                    process = DiagnosticWorker.CaptureSafely(DiagnosticWorker.CaptureProcess),
                    interfaces = DiagnosticWorker.CaptureSafely(DiagnosticWorker.CaptureInterfaces),
                    control = DiagnosticWorker.CaptureSafely(() => GetDiagnosticState(true)),
                    deviceTraffic = DiagnosticWorker.CaptureSafely(trafficEngine.GetDiagnostics),
                    applicationLimiter = DiagnosticWorker.CaptureSafely(applicationLimiter.GetDiagnostics),
                    applicationMonitor = DiagnosticWorker.CaptureSafely(applicationMonitor.GetDiagnostics),
                    os = Environment.OSVersion.VersionString,
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    status = new
                    {
                        _status.Mode,
                        _status.NpcapAvailable,
                        _status.NpcapVersion,
                        _status.Ipv6Detected,
                        _status.AutoControl,
                        _status.ActiveNetworkId,
                        _status.UpdatedAt
                    },
                    activeNetwork = _network,
                    adapters = _adapters,
                    deviceSummary = new
                    {
                        total = _devices.Count,
                        online = _devices.Values.Count(item => item.IsOnline),
                        limitedOrBlocked = _devices.Values.Count(HasActiveControl),
                        blocked = _devices.Values.Count(item => item.Policy.BlockInternet)
                    }
                }, PipeProtocol.JsonOptions, cancellationToken);

            await diagnostics.ExportAsync(archive, cancellationToken);

            await using (Stream windowsReport = archive.CreateEntry("windows-policies.json", CompressionLevel.Optimal).Open())
                await JsonSerializer.SerializeAsync(windowsReport,
                    await WindowsPolicyDiagnostics.CaptureAsync(cancellationToken), PipeProtocol.JsonOptions, cancellationToken);

            ZipArchiveEntry noteEntry = archive.CreateEntry("README.txt");
            await using StreamWriter note = new(noteEntry.Open());
            await note.WriteAsync("LanPilot diagnostics v2. Export during the outage BEFORE pausing/exiting; export again after recovery. " +
                "flight-recorder.json contains up to 30 minutes of 5-second health samples and 1000 recent events. " +
                "history contains up to three 2 MiB rolling service journals, including previous sessions; last seconds may be lost on a crash. " +
                "Packet counters are cumulative per service session, not speed readings. A quiet link alone is not proof of a fault. " +
                "limited means rate-dropped device packets or queued application packets; queueFull counts application queue drops. " +
                "Zero/missing last activity means no activity recorded. Samples and queues are bounded and may be truncated. " +
                "Exception messages/arguments, packet payloads, remote flow addresses, DNS queries, domains, and browsing content are not recorded. " +
                "Local network/adapter identifiers and configured policy IDs are included. Share privately. " +
                "Emergency pause suspends device forwarding and LanPilot application policies; inspect safety.restorationComplete for the cleanup result. " +
                "No connectivity probes are sent and no Windows settings are changed by diagnostics.");
        }

        File.Move(temporary, destination, true);
        return new OperationResult(true, "Diagnostics bundle exported.");
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastTickStarted, Environment.TickCount64);
        IReadOnlyDictionary<string, TrafficCounter> counters = trafficEngine.ReadAndResetCounters();
        DateTimeOffset now = DateTimeOffset.Now;
        RefreshNpcapStatus(now);
        if (now - _lastNetworkRefresh > TimeSpan.FromSeconds(10) && _networkRefreshTask is not { IsCompleted: false })
        {
            _lastNetworkRefresh = now;
            _networkRefreshTask = RefreshNetworkAsync(cancellationToken);
        }
        double elapsedSeconds = Math.Clamp((now - _lastCounterRead).TotalSeconds, 0.1d, 5d);
        _lastCounterRead = now;
        foreach ((string id, TrafficCounter counter) in counters)
        {
            if (!_devices.TryGetValue(id, out DeviceSnapshot? device)) continue;
            DeviceSnapshot updated = device with
            {
                DownloadBitsPerSecond = (long)Math.Round(counter.DownloadBytes * 8d / elapsedSeconds),
                UploadBitsPerSecond = (long)Math.Round(counter.UploadBytes * 8d / elapsedSeconds),
                TotalDownloadBytes = device.TotalDownloadBytes + counter.DownloadBytes,
                TotalUploadBytes = device.TotalUploadBytes + counter.UploadBytes,
                LastSeen = now,
                IsOnline = true
            };
            _devices[id] = updated;
            _minuteCounters.AddOrUpdate(id, (counter.DownloadBytes, counter.UploadBytes),
                (_, current) => (current.Down + counter.DownloadBytes, current.Up + counter.UploadBytes));
        }

        foreach ((string id, DeviceSnapshot device) in _devices)
        {
            if (!counters.ContainsKey(id) && (device.DownloadBitsPerSecond != 0 || device.UploadBitsPerSecond != 0))
            {
                _devices[id] = device with { DownloadBitsPerSecond = 0, UploadBitsPerSecond = 0 };
            }
        }

        if (trafficEngine.IsRunning) ScheduleDeviceRefresh(now, cancellationToken);

        if (trafficEngine.IsRunning) await RefreshEngineTargetsAsync(cancellationToken);
        if (now - _lastRollup >= TimeSpan.FromMinutes(1))
        {
            TrafficSample[] samples = _minuteCounters.Select(item =>
                new TrafficSample(item.Key, new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset),
                    0, 0, item.Value.Down, item.Value.Up)).ToArray();
            _minuteCounters.Clear();
            await store.SaveTrafficSamplesAsync(samples, cancellationToken);
            foreach (TrafficSample sample in samples)
            {
                if (_devices.TryGetValue(sample.DeviceId, out DeviceSnapshot? device))
                    await store.SaveDeviceAsync(device, cancellationToken);
            }
            _lastRollup = now;
        }

        if (now - _lastPrune >= TimeSpan.FromDays(1))
        {
            await store.PruneHistoryAsync(_settings.HistoryRetentionDays, cancellationToken);
            _lastPrune = now;
        }

        RaiseSnapshotChanged();
        Interlocked.Exchange(ref _lastTickCompleted, Environment.TickCount64);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        _settings = await store.LoadSettingsAsync(cancellationToken) ?? _settings;
        _devices.Clear();
        foreach (DeviceSnapshot value in await store.LoadDevicesAsync(cancellationToken)) _devices[value.Id] = value with { IsOnline = false };
        _groups.Clear();
        foreach (GroupPolicy value in await store.LoadGroupsAsync(cancellationToken)) _groups[value.Id] = value;
        _schedules.Clear();
        foreach (ScheduleRule value in await store.LoadSchedulesAsync(cancellationToken)) _schedules[value.Id] = value;
        _presets.Clear();
        foreach (RulePreset value in await store.LoadPresetsAsync(cancellationToken)) _presets[value.Id] = value;
        RaiseSnapshotChanged();
    }

    private async Task RefreshNetworkAsync(CancellationToken token)
    {
        try
        {
            NetworkAdapterInfo? previous = SelectAdapter(_settings.SelectedAdapterId);
            IReadOnlyList<NetworkAdapterInfo> current = await scanner.GetAdaptersAsync(_settings.SelectedAdapterId, token);
            NetworkAdapterInfo? selected = current.FirstOrDefault(adapter => adapter.Id == _settings.SelectedAdapterId);
            bool changed = previous is not null && (selected is null || previous.Ipv4Address != selected.Ipv4Address ||
                previous.GatewayAddress != selected.GatewayAddress || previous.GatewayMac != selected.GatewayMac || previous.PrefixLength != selected.PrefixLength);
            if (changed)
            {
                if (_settings.SelectedAdapterId != previous!.Id) return;
                await ChangeNetworkIdentityAsync(selected, current, false, token);
                return;
            }
            _adapters = current;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { logger.LogWarning(ex, "Network identity refresh failed."); }
    }

    private DeviceSnapshot[] GetEffectiveDevices()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return _devices.Values
            .Where(item => _network is null || item.NetworkId == _network.Id)
            .Select(item =>
            {
                _groups.TryGetValue(item.Policy.GroupId ?? string.Empty, out GroupPolicy? group);
                DevicePolicy effective = policyResolver.Resolve(item.Policy, group, _schedules.Values, now);
                return item with { Policy = effective };
            })
            .ToArray();
    }

    private async Task RefreshEngineTargetsAsync(CancellationToken token)
    {
        await _controlGate.WaitAsync(token);
        try
        {
            if (IsSuspended || !trafficEngine.IsRunning || _network is null) return;
            NetworkAdapterInfo? adapter = SelectAdapter(_settings.SelectedAdapterId);
            if (adapter is null) return;
            DeviceSnapshot[] devices = GetEffectiveDevices();
            // Persist the union: peers that just went offline may still have a poisoned ARP cache.
            ControlSession? previous = await sessionJournal.LoadAsync(token);
            DeviceSnapshot[] targets = (previous?.Targets ?? []).Concat(devices.Where(device => device.IsOnline && !device.IsGateway && !device.IsLocalComputer))
                .GroupBy(device => (device.Id, device.Ipv4Address, device.MacAddress)).Select(group => group.Last()).ToArray();
            if (targets.Length > 4096) throw new InvalidDataException("Recovery target history reached its safety limit; restart control manually after recovery.");
            if (previous is null || targets.Length != previous.Targets.Count)
                await sessionJournal.SaveAsync(new ControlSession(adapter, _network, targets, previous?.StartedAt ?? DateTimeOffset.Now), token);
            if (!IsSuspended) trafficEngine.UpdateTargets(devices);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not safely update forwarding targets.");
            await SuspendCoreAsync("Fault");
        }
        finally { _controlGate.Release(); }
    }

    private void ScheduleDeviceRefresh(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Discovery can take several seconds on a /24 network. It must never
        // block the 500 ms traffic-counter/UI snapshot loop.
        if (_deviceRefreshTask is { IsCompleted: false }) return;

        if (now - _lastOnlineRefresh >= OnlineRefreshInterval)
        {
            _lastOnlineRefresh = now;
            _lastKnownDeviceRefresh = now;
            int generation = Volatile.Read(ref _controlGeneration);
            _deviceRefreshTask = RefreshOnlineDevicesAsync(now, generation, cancellationToken);
        }
        else if (now - _lastKnownDeviceRefresh >= KnownDeviceRefreshInterval)
        {
            _lastKnownDeviceRefresh = now;
            int generation = Volatile.Read(ref _controlGeneration);
            _deviceRefreshTask = RefreshKnownDevicesAsync(now, generation, cancellationToken);
        }
    }

    private async Task RefreshOnlineDevicesAsync(DateTimeOffset now, int generation, CancellationToken cancellationToken)
    {
        NetworkAdapterInfo? adapter = SelectAdapter(_settings.SelectedAdapterId);
        NetworkProfile? network = _network;
        if (adapter is null || network is null) return;

        try
        {
            Dictionary<string, DeviceSnapshot> known = _devices.Values
                .Where(item => item.NetworkId == network.Id)
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<DeviceSnapshot> found = await scanner.ScanAsync(adapter, network, known, cancellationToken);
            if (generation != Volatile.Read(ref _controlGeneration) || !trafficEngine.IsRunning) return;
            HashSet<string> foundIds = found.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (DeviceSnapshot discovered in found)
            {
                bool isNew = !known.ContainsKey(discovered.Id);
                DeviceSnapshot prepared = await PrepareNewDeviceAsync(discovered, isNew, cancellationToken);
                DeviceSnapshot merged = _devices.AddOrUpdate(
                    prepared.Id,
                    prepared,
                    (_, current) => MergeDiscoveredDevice(current, prepared));
                if (isNew)
                {
                    await store.SaveDeviceAsync(merged, cancellationToken);
                    if (_settings.NotifyNewDevices && !merged.IsGateway && !merged.IsLocalComputer)
                    {
                        NotificationRaised?.Invoke(this, new NotificationEvent(
                            "New device discovered",
                            $"{merged.DisplayName} joined this network and is now monitored automatically.",
                            NotificationSeverity.Info));
                    }
                }
            }

            foreach (DeviceSnapshot previous in known.Values.Where(item => !foundIds.Contains(item.Id)))
            {
                if (previous.IsOnline && now - previous.LastSeen >= OfflineGracePeriod)
                {
                    _devices.AddOrUpdate(
                        previous.Id,
                        previous with { IsOnline = false, DownloadBitsPerSecond = 0, UploadBitsPerSecond = 0 },
                        (_, current) => now - current.LastSeen >= OfflineGracePeriod
                            ? current with { IsOnline = false, DownloadBitsPerSecond = 0, UploadBitsPerSecond = 0 }
                            : current);
                }
            }

            DeviceSnapshot[] effective = GetEffectiveDevices();
            await RefreshEngineTargetsAsync(cancellationToken);
            if (generation == Volatile.Read(ref _controlGeneration) && !IsSuspended && trafficEngine.IsRunning)
                SetStatus(EngineMode.Controlling, BuildControlStatusMessage(effective));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Automatic online-device refresh failed; existing traffic control remains active.");
        }
    }

    private async Task RefreshKnownDevicesAsync(DateTimeOffset now, int generation, CancellationToken cancellationToken)
    {
        NetworkProfile? network = _network;
        if (network is null) return;

        try
        {
            Dictionary<string, DeviceSnapshot> known = _devices.Values
                .Where(item => item.NetworkId == network.Id)
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            NetworkAdapterInfo? adapter = SelectAdapter(_settings.SelectedAdapterId);
            if (adapter is null) return;
            IReadOnlyList<DeviceSnapshot> online = await scanner.ProbeKnownDevicesAsync(known, cancellationToken, IPAddress.Parse(adapter.Ipv4Address));
            if (generation != Volatile.Read(ref _controlGeneration) || !trafficEngine.IsRunning) return;
            bool stateChanged = false;
            foreach (DeviceSnapshot device in online)
            {
                if (_devices.TryGetValue(device.Id, out DeviceSnapshot? current))
                {
                    stateChanged |= !current.IsOnline;
                    _devices[device.Id] = current with { IsOnline = true, LastSeen = device.LastSeen };
                }
            }

            if (stateChanged)
            {
                DeviceSnapshot[] effective = GetEffectiveDevices();
                await RefreshEngineTargetsAsync(cancellationToken);
                if (generation == Volatile.Read(ref _controlGeneration) && !IsSuspended && trafficEngine.IsRunning)
                    SetStatus(EngineMode.Controlling, BuildControlStatusMessage(effective));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "The quick known-device presence check failed.");
        }
    }

    private static string BuildControlStatusMessage(IEnumerable<DeviceSnapshot> devices)
    {
        DeviceSnapshot[] online = devices
            .Where(item => item.IsOnline && !item.IsGateway && !item.IsLocalComputer)
            .ToArray();
        int controlledCount = online.Count(HasActiveControl);
        string onlineLabel = online.Length == 1 ? "device" : "devices";
        string controlledLabel = controlledCount == 1 ? "device" : "devices";
        return controlledCount > 0
            ? $"Tracking {online.Length} {onlineLabel} automatically and applying limits or blocking to {controlledCount} {controlledLabel}."
            : $"Tracking {online.Length} online {onlineLabel} automatically. No limits are currently applied.";
    }

    private static bool HasActiveControl(DeviceSnapshot device) =>
        device.Policy.BlockInternet ||
        device.Policy.DownloadLimitBitsPerSecond is not null ||
        device.Policy.UploadLimitBitsPerSecond is not null;

    public static DeviceSnapshot MergeDiscoveredDevice(DeviceSnapshot current, DeviceSnapshot discovered) =>
        discovered with
        {
            DisplayName = current.DisplayName,
            HostName = current.HostName ?? discovered.HostName,
            DeviceType = current.DeviceType,
            GroupId = current.GroupId,
            FirstSeen = current.FirstSeen,
            DownloadBitsPerSecond = current.DownloadBitsPerSecond,
            UploadBitsPerSecond = current.UploadBitsPerSecond,
            TotalDownloadBytes = current.TotalDownloadBytes,
            TotalUploadBytes = current.TotalUploadBytes,
            Policy = current.Policy
        };

    public static DeviceSnapshot AssignDeviceToGroup(DeviceSnapshot device, GroupPolicy group) =>
        device with
        {
            GroupId = group.Id,
            Policy = device.Policy with { GroupId = group.Id }
        };

    public static DeviceSnapshot ResetDeviceToGroup(
        DeviceSnapshot device,
        GroupPolicy group,
        DateTimeOffset resetAt)
    {
        string defaultName = !string.IsNullOrWhiteSpace(device.HostName)
            ? device.HostName
            : device.MacAddress.Length >= 5
                ? $"Device {device.MacAddress[^5..]}"
                : "Unknown device";
        DevicePolicy policy = new(device.Id, false, null, null, DevicePriority.Normal, group.Id);
        return device with
        {
            DisplayName = defaultName,
            DeviceType = "Unknown",
            GroupId = group.Id,
            FirstSeen = resetAt,
            DownloadBitsPerSecond = 0,
            UploadBitsPerSecond = 0,
            TotalDownloadBytes = 0,
            TotalUploadBytes = 0,
            Policy = policy
        };
    }

    private async Task<DeviceSnapshot> PrepareNewDeviceAsync(
        DeviceSnapshot device,
        bool isNew,
        CancellationToken cancellationToken)
    {
        if (!isNew || !_settings.AutoAssignNewDevicesToGuests || device.IsGateway || device.IsLocalComputer)
            return device;

        GroupPolicy guests = await GetOrCreateGuestsGroupAsync(cancellationToken);
        return AssignDeviceToGroup(device, guests);
    }

    private async Task<GroupPolicy> GetOrCreateGuestsGroupAsync(CancellationToken cancellationToken)
    {
        GroupPolicy? existing = _groups.Values.FirstOrDefault(group =>
            string.Equals(group.Name, "Guests", StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        await EnsureDefaultGroupsAsync(cancellationToken);
        return _groups.Values.First(group =>
            string.Equals(group.Name, "Guests", StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureDefaultGroupsAsync(CancellationToken cancellationToken)
    {
        (string Id, string Name)[] defaults =
        [
            ("built-in-family", "Family"),
            ("built-in-guests", "Guests"),
            ("built-in-work", "Work")
        ];

        foreach ((string id, string name) in defaults)
        {
            if (_groups.Values.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            GroupPolicy candidate = new(id, name, null, null, DevicePriority.Normal, false);
            GroupPolicy selected = _groups.GetOrAdd(candidate.Id, candidate);
            if (ReferenceEquals(selected, candidate))
            {
                await store.SaveGroupAsync(selected, cancellationToken);
            }
        }
    }

    private NetworkAdapterInfo? SelectAdapter(string? adapterId) => string.IsNullOrWhiteSpace(adapterId)
        ? _adapters.FirstOrDefault()
        : _adapters.FirstOrDefault(item => string.Equals(item.Id, adapterId, StringComparison.OrdinalIgnoreCase));

    private NetworkProfile BuildKnownProfile(NetworkAdapterInfo adapter)
    {
        NetworkProfile provisional = NetworkScanner.BuildProfile(adapter);
        return _networks.TryGetValue(provisional.Id, out NetworkProfile? known)
            ? NetworkScanner.BuildProfile(adapter, known)
            : provisional;
    }

    private void RefreshNpcapStatus(DateTimeOffset now, bool force = false)
    {
        if (!force && now - _lastNpcapRefresh < NpcapRefreshInterval) return;
        _lastNpcapRefresh = now;

        (bool available, string? version) = NpcapDetector.Detect();
        if (available == _status.NpcapAvailable &&
            string.Equals(version, _status.NpcapVersion, StringComparison.OrdinalIgnoreCase)) return;

        EngineMode mode = _status.Mode;
        string message = _status.Message;
        if (available && mode == EngineMode.DriverUnavailable)
        {
            mode = EngineMode.Idle;
            message = "Npcap detected. Ready to scan and start traffic control.";
        }
        else if (!available && !trafficEngine.IsRunning &&
                 mode is EngineMode.Idle or EngineMode.Monitoring or EngineMode.DriverUnavailable)
        {
            mode = EngineMode.DriverUnavailable;
            message = "Npcap is not installed. Discovery-only mode is available.";
        }

        _status = _status with
        {
            Mode = mode,
            Message = message,
            NpcapAvailable = available,
            NpcapVersion = version,
            UpdatedAt = now
        };
        RaiseSnapshotChanged();
    }

    private void SetStatus(
        EngineMode mode,
        string message,
        bool? npcapAvailable = null,
        string? npcapVersion = null,
        bool? ipv6Detected = null)
    {
        if (_status.Mode != mode)
            diagnostics.Record("LanPilot.Control", "Information", $"Engine transition: {_status.Mode} -> {mode}");
        _status = new EngineStatus(
            mode,
            message,
            npcapAvailable ?? _status.NpcapAvailable,
            npcapVersion ?? _status.NpcapVersion,
            ipv6Detected ?? _status.Ipv6Detected,
            _settings.AutoControl,
            _network?.Id,
            DateTimeOffset.Now);
        RaiseSnapshotChanged();
    }

    private void RaiseSnapshotChanged() => SnapshotChanged?.Invoke(this, EventArgs.Empty);

    private static bool DetectIpv6() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Any(item => item.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
                         !item.Address.IsIPv6LinkLocal);
}
