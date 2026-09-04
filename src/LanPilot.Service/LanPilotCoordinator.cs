using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using LanPilot.Contracts;
using LanPilot.Service.Engine;
using LanPilot.Service.Persistence;

namespace LanPilot.Service;

public sealed class LanPilotCoordinator(
    SqliteStore store,
    ControlSessionJournal sessionJournal,
    NetworkScanner scanner,
    TrafficEngine trafficEngine,
    PolicyResolver policyResolver,
    ApplicationTrafficController applicationTrafficController,
    ILogger<LanPilotCoordinator> logger)
{
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
    private Task? _deviceRefreshTask;
    private int _controlGeneration;
    private readonly ConcurrentDictionary<string, (long Down, long Up)> _minuteCounters = new();
    private static readonly TimeSpan OnlineRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan KnownDeviceRefreshInterval = TimeSpan.FromSeconds(1);
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
            try
            {
                await applicationTrafficController.ApplyAsync(policy, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not restore the application policy for {Application}.", policy.DisplayName);
            }
        }

        (bool available, string? version) = NpcapDetector.Detect();
        _adapters = await scanner.GetAdaptersAsync(_settings.SelectedAdapterId, cancellationToken);
        NetworkAdapterInfo? selected = SelectAdapter(_settings.SelectedAdapterId);
        bool ipv6 = DetectIpv6();

        if (selected is null)
        {
            SetStatus(EngineMode.Faulted, "No supported IPv4 Ethernet or Wi-Fi adapter was found.", available, version, ipv6);
            return;
        }

        _settings = _settings with { SelectedAdapterId = selected.Id };
        await store.SaveSettingsAsync(_settings, cancellationToken);
        _network = BuildKnownProfile(selected);
        await store.SaveNetworkAsync(_network, cancellationToken);

        ControlSession? abandonedSession = await sessionJournal.LoadAsync(cancellationToken);
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
                logger.LogWarning(ex, "Could not restore an abandoned traffic-control session.");
                NotificationRaised?.Invoke(this, new NotificationEvent(
                    "Network recovery warning",
                    "Automatic ARP recovery could not be completed. Use Emergency Pause after checking the adapter.",
                    NotificationSeverity.Warning));
            }
        }

        SetStatus(
            available ? EngineMode.Idle : EngineMode.DriverUnavailable,
            available ? "Ready. Select Scan to discover devices." : "Npcap is not installed. Discovery-only mode is available.",
            available,
            version,
            ipv6);

        await ScanAsync(selected.Id, cancellationToken);
        if (_settings.AutoControl && _network.AutoControl && available)
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
            _settings);
    }

    public async Task<OperationResult> ScanAsync(string? adapterId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
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
            _gate.Release();
        }
    }

    public async Task<OperationResult> SetControlAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!enabled)
            {
                Interlocked.Increment(ref _controlGeneration);
                await trafficEngine.StopAsync(true, cancellationToken);
                sessionJournal.Clear();
                SetStatus(_status.NpcapAvailable ? EngineMode.Idle : EngineMode.DriverUnavailable, "Traffic control is paused and network state was restored.");
                RaiseSnapshotChanged();
                return new OperationResult(true, "Traffic control paused and ARP state restored.");
            }

            if (trafficEngine.IsRunning && _status.Mode == EngineMode.Controlling)
            {
                return new OperationResult(true, _status.Message);
            }

            if (!_status.NpcapAvailable)
            {
                return new OperationResult(false, "Npcap is required for traffic control.");
            }

            NetworkAdapterInfo? adapter = SelectAdapter(_settings.SelectedAdapterId);
            if (adapter is null || _network is null)
            {
                return new OperationResult(false, "Select an active network adapter first.");
            }

            DeviceSnapshot[] effective = GetEffectiveDevices();
            DeviceSnapshot[] intercepted = effective
                .Where(item => item.IsOnline && !item.IsGateway && !item.IsLocalComputer)
                .ToArray();
            await sessionJournal.SaveAsync(new ControlSession(adapter, _network, intercepted, DateTimeOffset.Now), cancellationToken);
            await trafficEngine.StartAsync(adapter, _network, effective, cancellationToken);
            Interlocked.Increment(ref _controlGeneration);
            _lastOnlineRefresh = DateTimeOffset.Now;
            _lastKnownDeviceRefresh = _lastOnlineRefresh;
            SetStatus(EngineMode.Controlling, BuildControlStatusMessage(effective));
            RaiseSnapshotChanged();
            return new OperationResult(true, _status.Message);
        }
        catch (Exception ex)
        {
            try { await trafficEngine.StopAsync(true, CancellationToken.None); } catch { }
            sessionJournal.Clear();
            logger.LogError(ex, "Traffic control could not start.");
            SetStatus(EngineMode.Faulted, ex.Message);
            NotificationRaised?.Invoke(this,
                new NotificationEvent("Traffic control error", ex.Message, NotificationSeverity.Error));
            return new OperationResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EmergencyPauseAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _controlGeneration);
        await trafficEngine.StopAsync(true, cancellationToken);
        sessionJournal.Clear();
        SetStatus(_status.NpcapAvailable ? EngineMode.Idle : EngineMode.DriverUnavailable, "Emergency pause completed. Network state was restored.");
        RaiseSnapshotChanged();
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
        RefreshEngineTargets();
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
            RefreshEngineTargets();
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
        RefreshEngineTargets();
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

        RefreshEngineTargets();
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
        RefreshEngineTargets();
        RaiseSnapshotChanged();
        return new OperationResult(true, "Schedule saved.");
    }

    public async Task<OperationResult> DeleteScheduleAsync(string scheduleId, CancellationToken cancellationToken)
    {
        if (!_schedules.TryRemove(scheduleId, out _))
            return new OperationResult(false, "Schedule not found.");
        await store.DeleteScheduleAsync(scheduleId, cancellationToken);
        RefreshEngineTargets();
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

        await applicationTrafficController.ApplyAsync(normalized, cancellationToken);
        _applicationPolicies[normalized.Id] = normalized;
        await store.SaveApplicationPolicyAsync(normalized, cancellationToken);
        return new OperationResult(true, "Application network policy saved.");
    }

    public async Task<OperationResult> DeleteApplicationPolicyAsync(string policyId, CancellationToken cancellationToken)
    {
        if (!_applicationPolicies.TryGetValue(policyId, out LocalApplicationPolicy? policy))
            return new OperationResult(false, "Application policy not found.");

        await applicationTrafficController.RemoveAsync(policy, cancellationToken);
        await store.DeleteApplicationPolicyAsync(policyId, cancellationToken);
        _applicationPolicies.TryRemove(policyId, out _);
        return new OperationResult(true, "Application network policy removed.");
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

        RefreshEngineTargets();
        RaiseSnapshotChanged();
        return new OperationResult(true, $"Preset '{preset.Name}' applied.");
    }

    public async Task<OperationResult> UpdateNetworkSettingsAsync(
        UpdateNetworkSettingsRequest request,
        CancellationToken cancellationToken)
    {
        NetworkAdapterInfo? adapter = SelectAdapter(request.AdapterId);
        if (adapter is null) return new OperationResult(false, "Adapter not found.");
        _settings = _settings with { SelectedAdapterId = adapter.Id, AutoControl = request.AutoControl };
        _network = BuildKnownProfile(adapter) with { AutoControl = request.AutoControl };
        _networks[_network.Id] = _network;
        await store.SaveSettingsAsync(_settings, cancellationToken);
        await store.SaveNetworkAsync(_network, cancellationToken);
        RaiseSnapshotChanged();
        return new OperationResult(true, "Network settings saved.");
    }

    public async Task<OperationResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
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
            await using Stream report = reportEntry.Open();
            await JsonSerializer.SerializeAsync(report, new
            {
                formatVersion = 1,
                generatedAt = DateTimeOffset.Now,
                os = Environment.OSVersion.VersionString,
                processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                status = _status,
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

            ZipArchiveEntry noteEntry = archive.CreateEntry("README.txt");
            await using StreamWriter note = new(noteEntry.Open());
            await note.WriteAsync("LanPilot diagnostics bundle. No packet payloads, DNS queries, domains, or browsing content are collected.");
        }

        File.Move(temporary, destination, true);
        return new OperationResult(true, "Diagnostics bundle exported.");
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, TrafficCounter> counters = trafficEngine.ReadAndResetCounters();
        DateTimeOffset now = DateTimeOffset.Now;
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

        if (trafficEngine.IsRunning) RefreshEngineTargets();
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

    private void RefreshEngineTargets()
    {
        if (trafficEngine.IsRunning) trafficEngine.UpdateTargets(GetEffectiveDevices());
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
            RefreshEngineTargets();
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
            IReadOnlyList<DeviceSnapshot> online = await scanner.ProbeKnownDevicesAsync(known, cancellationToken);
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
                RefreshEngineTargets();
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

    private NetworkAdapterInfo? SelectAdapter(string? adapterId) =>
        _adapters.FirstOrDefault(item => string.Equals(item.Id, adapterId, StringComparison.OrdinalIgnoreCase))
        ?? _adapters.FirstOrDefault();

    private NetworkProfile BuildKnownProfile(NetworkAdapterInfo adapter)
    {
        NetworkProfile provisional = NetworkScanner.BuildProfile(adapter);
        return _networks.TryGetValue(provisional.Id, out NetworkProfile? known)
            ? NetworkScanner.BuildProfile(adapter, known)
            : provisional;
    }

    private void SetStatus(
        EngineMode mode,
        string message,
        bool? npcapAvailable = null,
        string? npcapVersion = null,
        bool? ipv6Detected = null)
    {
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
