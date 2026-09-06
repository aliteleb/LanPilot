namespace LanPilot.Contracts;

public enum EngineMode
{
    DriverUnavailable,
    Idle,
    Discovering,
    Monitoring,
    Controlling,
    Recovering,
    Faulted
}

public enum DevicePriority
{
    Low,
    Normal,
    High
}

public sealed record EngineStatus(
    EngineMode Mode,
    string Message,
    bool NpcapAvailable,
    string? NpcapVersion,
    bool Ipv6Detected,
    bool AutoControl,
    string? ActiveNetworkId,
    DateTimeOffset UpdatedAt);

public sealed record NetworkAdapterInfo(
    string Id,
    string Name,
    string Description,
    string Ipv4Address,
    int PrefixLength,
    string GatewayAddress,
    string? GatewayMac,
    long LinkSpeedBitsPerSecond,
    bool IsWireless,
    bool IsSelected);

public sealed record NetworkProfile(
    string Id,
    string AdapterId,
    string Name,
    string Ipv4Cidr,
    string GatewayIpv4,
    string? GatewayMac,
    bool AutoControl,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

public sealed record DevicePolicy(
    string DeviceId,
    bool BlockInternet,
    long? DownloadLimitBitsPerSecond,
    long? UploadLimitBitsPerSecond,
    DevicePriority Priority,
    string? GroupId);

public sealed record DeviceSnapshot(
    string Id,
    string NetworkId,
    string MacAddress,
    string Ipv4Address,
    string DisplayName,
    string? HostName,
    string DeviceType,
    string? GroupId,
    bool IsOnline,
    bool IsGateway,
    bool IsLocalComputer,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long DownloadBitsPerSecond,
    long UploadBitsPerSecond,
    long TotalDownloadBytes,
    long TotalUploadBytes,
    DevicePolicy Policy);

public sealed record GroupPolicy(
    string Id,
    string Name,
    long? DownloadLimitBitsPerSecond,
    long? UploadLimitBitsPerSecond,
    DevicePriority Priority,
    bool BlockInternet);

public sealed record ScheduleRule(
    string Id,
    string Name,
    string? DeviceId,
    string? GroupId,
    DayOfWeek[] Days,
    TimeOnly Start,
    TimeOnly End,
    bool BlockInternet,
    long? DownloadLimitBitsPerSecond,
    long? UploadLimitBitsPerSecond,
    bool Enabled);

public sealed record RulePreset(
    string Id,
    string Name,
    IReadOnlyList<DevicePolicy> DevicePolicies,
    IReadOnlyList<GroupPolicy> Groups,
    IReadOnlyList<ScheduleRule> Schedules,
    DateTimeOffset CreatedAt);

public sealed record LocalApplicationPolicy(
    string Id,
    string DisplayName,
    string ExecutablePath,
    long? UploadLimitBitsPerSecond,
    bool BlockInternet,
    long? DownloadLimitBitsPerSecond = null);

public sealed record LocalApplicationSnapshot(
    string Id,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<int> ProcessIds,
    bool IsRunning,
    LocalApplicationPolicy? Policy,
    long DownloadBitsPerSecond = 0,
    long UploadBitsPerSecond = 0);

public sealed record TrafficSample(
    string DeviceId,
    DateTimeOffset Timestamp,
    long DownloadBitsPerSecond,
    long UploadBitsPerSecond,
    long DownloadBytes,
    long UploadBytes);

public sealed record AppSettings(
    string? SelectedAdapterId,
    bool AutoControl,
    int HistoryRetentionDays,
    bool NotifyNewDevices,
    bool DisplayRatesAsBytes,
    bool ActiveDeviceMonitoring = false,
    bool AutoAssignNewDevicesToGuests = true);

public sealed record DashboardSnapshot(
    EngineStatus Status,
    IReadOnlyList<NetworkAdapterInfo> Adapters,
    NetworkProfile? Network,
    IReadOnlyList<DeviceSnapshot> Devices,
    IReadOnlyList<GroupPolicy> Groups,
    IReadOnlyList<ScheduleRule> Schedules,
    IReadOnlyList<RulePreset> Presets,
    AppSettings Settings,
    ControlSafetyStatus? ControlSafety = null,
    bool? ApplicationMonitoringAvailable = null);

public sealed record ControlSafetyStatus(string Reason, bool RequiresManualResume,
    bool RestorationComplete, bool ApplicationsActive, DateTimeOffset UpdatedAt,
    string? FailureReason = null, bool DevicesActive = false);

public sealed record ScanRequest(string? AdapterId);
public sealed record ControlRequest(bool Enabled);
public sealed record UpdateNetworkSettingsRequest(string AdapterId, bool AutoControl);
public sealed record RenameDeviceRequest(string DeviceId, string DisplayName, string DeviceType);
public sealed record ResetDeviceRequest(string DeviceId);
public sealed record ExportRequest(string DestinationPath, bool IncludeHistory);
public sealed record ImportRequest(string SourcePath);
public sealed record SavePresetRequest(string Name, string? PresetId = null);
public sealed record ApplyPresetRequest(string PresetId);
public sealed record DeleteEntityRequest(string Id);
public sealed record OperationResult(bool Success, string Message);
