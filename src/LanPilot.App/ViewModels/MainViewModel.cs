using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanPilot.App.Services;
using LanPilot.Contracts;
using Microsoft.Win32;
using WpfApplication = System.Windows.Application;

namespace LanPilot.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public const string ProjectUrl = "https://github.com/aliteleb/LanPilot";
    public const string IssuesUrl = "https://github.com/aliteleb/LanPilot/issues";
    public const string AuthorUrl = "https://github.com/aliteleb";

    private readonly LanPilotClient _client;
    private readonly UiSettingsStore _uiSettingsStore;
    private readonly Dictionary<string, DeviceRowViewModel> _deviceRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly bool _firstRunComplete;
    private DashboardSnapshot? _pendingSnapshot;
    private bool _snapshotDispatchQueued;
    private bool _isApplyingSnapshot;
    private int _connectionRecoveryRunning;
    private int _applicationRefreshRunning;

    [ObservableProperty] private string _statusMessage = "Connecting to LanPilot Service…";
    [ObservableProperty] private EngineMode _engineMode = EngineMode.Idle;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isReconnecting;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isScanRequested;
    [ObservableProperty] private bool _isControlActive;
    [ObservableProperty] private bool _npcapAvailable;
    [ObservableProperty] private string _npcapVersion = "Not detected";
    [ObservableProperty] private bool _ipv6Detected;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DeviceRowViewModel? _selectedDevice;
    [ObservableProperty] private NetworkAdapterInfo? _selectedAdapter;
    [ObservableProperty] private bool _autoControl;
    [ObservableProperty] private bool _notifyNewDevices = true;
    [ObservableProperty] private bool _autoAssignNewDevicesToGuests = true;
    [ObservableProperty] private bool _displayRatesAsBytes;
    [ObservableProperty] private bool _activeDeviceMonitoring;
    [ObservableProperty] private int _historyRetentionDays = 30;
    [ObservableProperty] private bool _runAtLogin;
    [ObservableProperty] private string _theme = "Dark";
    [ObservableProperty] private bool _isOnboardingVisible;
    [ObservableProperty] private bool _isDeviceEditorVisible;
    [ObservableProperty] private bool _isResetDeviceConfirmationVisible;
    [ObservableProperty] private DeviceRowViewModel? _pendingResetDevice;
    [ObservableProperty] private bool _isBlockAllConfirmationVisible;
    [ObservableProperty] private bool _authorizedNetwork;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editDeviceType = "Unknown";
    [ObservableProperty] private bool _editBlocked;
    [ObservableProperty] private string _editDownloadLimit = string.Empty;
    [ObservableProperty] private string _editUploadLimit = string.Empty;
    [ObservableProperty] private DevicePriority _editPriority = DevicePriority.Normal;
    [ObservableProperty] private GroupPolicy? _editGroup;
    [ObservableProperty] private bool _isGroupEditorVisible;
    [ObservableProperty] private GroupPolicy? _groupBeingEdited;
    [ObservableProperty] private string _groupEditName = string.Empty;
    [ObservableProperty] private string _groupEditDownloadLimit = string.Empty;
    [ObservableProperty] private string _groupEditUploadLimit = string.Empty;
    [ObservableProperty] private DevicePriority _groupEditPriority = DevicePriority.Normal;
    [ObservableProperty] private bool _groupEditBlocked;
    [ObservableProperty] private bool _isRuleDeleteConfirmationVisible;
    [ObservableProperty] private string _pendingDeleteKind = string.Empty;
    [ObservableProperty] private string _pendingDeleteId = string.Empty;
    [ObservableProperty] private string _pendingDeleteName = string.Empty;
    [ObservableProperty] private bool _isScheduleEditorVisible;
    [ObservableProperty] private ScheduleRule? _scheduleBeingEdited;
    [ObservableProperty] private string _scheduleEditName = string.Empty;
    [ObservableProperty] private string _scheduleTargetType = "Device";
    [ObservableProperty] private DeviceRowViewModel? _scheduleEditDevice;
    [ObservableProperty] private GroupPolicy? _scheduleEditGroup;
    [ObservableProperty] private string _scheduleEditStart = "22:00";
    [ObservableProperty] private string _scheduleEditEnd = "07:00";
    [ObservableProperty] private string _scheduleEditDownloadLimit = string.Empty;
    [ObservableProperty] private string _scheduleEditUploadLimit = string.Empty;
    [ObservableProperty] private bool _scheduleEditBlocked;
    [ObservableProperty] private bool _scheduleEditEnabled = true;
    [ObservableProperty] private bool _scheduleMonday = true;
    [ObservableProperty] private bool _scheduleTuesday = true;
    [ObservableProperty] private bool _scheduleWednesday = true;
    [ObservableProperty] private bool _scheduleThursday = true;
    [ObservableProperty] private bool _scheduleFriday = true;
    [ObservableProperty] private bool _scheduleSaturday = true;
    [ObservableProperty] private bool _scheduleSunday = true;
    [ObservableProperty] private string _presetName = string.Empty;
    [ObservableProperty] private RulePreset? _selectedPreset;
    [ObservableProperty] private bool _isPresetEditorVisible;
    [ObservableProperty] private RulePreset? _presetBeingEdited;
    [ObservableProperty] private string _applicationSearchText = string.Empty;
    [ObservableProperty] private bool _isApplicationListLoading;
    [ObservableProperty] private bool _isApplicationRefreshInProgress;
    [ObservableProperty] private bool _isApplicationEditorVisible;
    [ObservableProperty] private ApplicationRowViewModel? _selectedApplication;
    [ObservableProperty] private string _applicationDownloadLimit = string.Empty;
    [ObservableProperty] private string _applicationUploadLimit = string.Empty;
    [ObservableProperty] private bool _applicationBlocked;

    public MainViewModel(LanPilotClient client, UiSettingsStore uiSettingsStore)
    {
        _client = client;
        _uiSettingsStore = uiSettingsStore;
        UiSettings ui = uiSettingsStore.Load();
        _firstRunComplete = ui.FirstRunComplete;
        Theme = ui.Theme;
        RunAtLogin = SafeGetStartupState(ui.RunAtLogin);
        IsOnboardingVisible = !ui.FirstRunComplete;

        DevicesView = new ListCollectionView(Devices);
        DevicesView.Filter = FilterDevice;
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceRowViewModel.IsOnline), ListSortDirection.Descending));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceRowViewModel.IsGateway), ListSortDirection.Descending));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceRowViewModel.IsLocalComputer), ListSortDirection.Descending));
        DevicesView.SortDescriptions.Add(new SortDescription(nameof(DeviceRowViewModel.DisplayName), ListSortDirection.Ascending));
        ApplicationsView = new ListCollectionView(Applications);
        ApplicationsView.Filter = FilterApplication;
        ApplicationsView.SortDescriptions.Add(new SortDescription(nameof(ApplicationRowViewModel.IsRunning), ListSortDirection.Descending));
        ApplicationsView.SortDescriptions.Add(new SortDescription(nameof(ApplicationRowViewModel.DisplayName), ListSortDirection.Ascending));
        _client.SnapshotReceived += OnSnapshotReceived;
        _client.NotificationReceived += (_, notification) => NotificationRequested?.Invoke(this, notification);
        _client.ConnectionChanged += (_, connected) =>
            _ = WpfApplication.Current.Dispatcher.InvokeAsync(
                () =>
                {
                    IsConnected = connected;
                    if (!connected && !_lifetimeCancellation.IsCancellationRequested)
                        StartConnectionRecovery();
                },
                DispatcherPriority.Background);
    }

    public event EventHandler<NotificationEvent>? NotificationRequested;
    public event EventHandler<string>? ThemeRequested;
    public event Action<DeviceRowViewModel>? DeviceEditRequested;
    public event Action? ApplicationControlRequested;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];
    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<GroupPolicy> Groups { get; } = [];
    public ObservableCollection<ScheduleRule> Schedules { get; } = [];
    public ObservableCollection<RulePreset> Presets { get; } = [];
    public ObservableCollection<ApplicationRowViewModel> Applications { get; } = [];
    public ICollectionView DevicesView { get; }
    public ICollectionView ApplicationsView { get; }
    public IReadOnlyList<string> DeviceTypes { get; } = ["Unknown", "Computer", "Phone", "Tablet", "TV", "Console", "IoT", "Router"];
    public IReadOnlyList<DevicePriority> Priorities { get; } = Enum.GetValues<DevicePriority>();
    public IReadOnlyList<string> ScheduleTargetTypes { get; } = ["Device", "Group"];
    public IEnumerable<DeviceRowViewModel> EditableDevices => Devices.Where(device => device.CanEditPolicy);
    public bool IsScheduleGroupTarget => string.Equals(ScheduleTargetType, "Group", StringComparison.Ordinal);
    public bool HasGroups => Groups.Count > 0;
    public bool HasSchedules => Schedules.Count > 0;
    public bool HasPresets => Presets.Count > 0;
    public string ProductVersion
    {
        get
        {
            Version? version = typeof(MainViewModel).Assembly.GetName().Version;
            return version is null ? "v0.1.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }
    public string CopyrightText => $"Copyright © {DateTime.Now.Year} Ali Teleb";

    public int OnlineCount => Devices.Count(item => item.IsOnline);
    public int BlockedCount => Devices.Count(item => item.BlockInternet);
    public long TotalDownloadBitsPerSecond => Devices.Sum(item => item.DownloadBitsPerSecond);
    public long TotalUploadBitsPerSecond => Devices.Sum(item => item.UploadBitsPerSecond);
    public string TotalDownloadText => FormatRate(TotalDownloadBitsPerSecond);
    public string TotalUploadText => FormatRate(TotalUploadBitsPerSecond);
    public int RunningApplicationCount => Applications.Count(item => item.IsRunning);
    public int RestrictedApplicationCount => Applications.Count(item => item.HasPolicy);
    public int BlockedApplicationCount => Applications.Count(item => item.IsBlocked);
    public long ApplicationDownloadBitsPerSecond => Applications.Sum(item => item.DownloadBitsPerSecond);
    public long ApplicationUploadBitsPerSecond => Applications.Sum(item => item.UploadBitsPerSecond);
    public string ApplicationDownloadText => FormatRate(ApplicationDownloadBitsPerSecond);
    public string ApplicationUploadText => FormatRate(ApplicationUploadBitsPerSecond);
    public string MostActiveApplicationName => MostActiveApplication?.DisplayName ?? "No active application traffic";
    public string MostActiveApplicationTrafficText => MostActiveApplication is ApplicationRowViewModel application
        ? $"↓ {application.DownloadDisplay}   ↑ {application.UploadDisplay}"
        : "Open an application to see its live traffic.";
    public string ControlButtonText => IsControlActive ? "Pause control" : "Start control";
    public bool IsScanning => IsScanRequested || EngineMode == EngineMode.Discovering;
    public bool CanScan => IsConnected && !IsScanning;
    public bool CanRefreshApplications => !IsApplicationRefreshInProgress;

    partial void OnSearchTextChanged(string value) => DevicesView.Refresh();
    partial void OnApplicationSearchTextChanged(string value) => ApplicationsView.Refresh();
    partial void OnIsApplicationRefreshInProgressChanged(bool value) => OnPropertyChanged(nameof(CanRefreshApplications));
    partial void OnSelectedDeviceChanged(DeviceRowViewModel? value) => LoadEditor(value);
    partial void OnScheduleTargetTypeChanged(string value) => OnPropertyChanged(nameof(IsScheduleGroupTarget));
    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(CanScan));
    partial void OnEngineModeChanged(EngineMode value)
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(CanScan));
    }
    partial void OnIsScanRequestedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(CanScan));
    }
    partial void OnThemeChanged(string value)
    {
        if (_isApplyingSnapshot) return;
        UiSettings current = _uiSettingsStore.Load();
        _uiSettingsStore.Save(current with { Theme = value });
        ThemeRequested?.Invoke(this, value);
    }

    partial void OnRunAtLoginChanged(bool value)
    {
        if (_isApplyingSnapshot) return;
        try
        {
            StartupManager.SetEnabled(value);
            UiSettings current = _uiSettingsStore.Load();
            _uiSettingsStore.Save(current with { RunAtLogin = value });
        }
        catch (Exception ex)
        {
            Notify("Startup setting failed", ex.Message, NotificationSeverity.Error);
        }
    }

    [RelayCommand]
    public async Task InitializeAsync()
    {
        bool connected = await TryConnectAndInitializeAsync(
            notifyFailure: true,
            showBusy: true,
            _lifetimeCancellation.Token);
        if (!connected) StartConnectionRecovery();
    }

    private async Task<bool> TryConnectAndInitializeAsync(
        bool notifyFailure,
        bool showBusy,
        CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (showBusy) IsBusy = true;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await ServiceBootstrapper.EnsureRunningAsync(timeout.Token);
            await _client.ConnectAsync(timeout.Token);
            DashboardSnapshot snapshot = await _client.GetSnapshotAsync(timeout.Token);
            IsConnected = true;
            ApplySnapshot(snapshot);
            if (_firstRunComplete && snapshot.Status.NpcapAvailable &&
                snapshot.Status.Mode is EngineMode.Idle or EngineMode.Monitoring)
            {
                OperationResult started = await _client.SetControlAsync(true, timeout.Token);
                if (started.Success && snapshot.Settings.SelectedAdapterId is not null)
                {
                    AutoControl = true;
                    await _client.UpdateNetworkSettingsAsync(
                        new UpdateNetworkSettingsRequest(snapshot.Settings.SelectedAdapterId, true),
                        timeout.Token);
                    await _client.UpdateSettingsAsync(snapshot.Settings with { AutoControl = true }, timeout.Token);
                }
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusMessage = "LanPilot Service is unavailable. Install or start the service, then retry.";
            if (notifyFailure)
                Notify("Service unavailable", ex.Message, NotificationSeverity.Error);
            return false;
        }
        finally
        {
            if (showBusy) IsBusy = false;
            _connectionGate.Release();
        }
    }

    private void StartConnectionRecovery()
    {
        if (_lifetimeCancellation.IsCancellationRequested ||
            Interlocked.Exchange(ref _connectionRecoveryRunning, 1) != 0)
            return;

        _ = RecoverConnectionAsync(_lifetimeCancellation.Token);
    }

    private async Task RecoverConnectionAsync(CancellationToken cancellationToken)
    {
        IsReconnecting = true;
        StatusMessage = "Reconnecting to LanPilot Service…";
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_client.IsConnected)
            {
                if (await TryConnectAndInitializeAsync(false, false, cancellationToken))
                    return;

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            IsReconnecting = false;
            Interlocked.Exchange(ref _connectionRecoveryRunning, 0);
            if (!_client.IsConnected && !cancellationToken.IsCancellationRequested)
                StartConnectionRecovery();
        }
    }

    public void StopConnectionRecovery() => _lifetimeCancellation.Cancel();

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        try
        {
            IsScanRequested = true;
            await RunOperationAsync(
                token => _client.ScanAsync(SelectedAdapter?.Id, token),
                "Network scan");
        }
        finally
        {
            IsScanRequested = false;
        }
    }

    [RelayCommand]
    private async Task ToggleControlAsync()
    {
        await RunOperationAsync(
            token => _client.SetControlAsync(!IsControlActive, token),
            IsControlActive ? "Pause control" : "Start control");
    }

    [RelayCommand]
    private async Task EmergencyPauseAsync()
    {
        await RunOperationAsync(_client.EmergencyPauseAsync, "Emergency pause");
    }

    [RelayCommand]
    private async Task SaveSelectedDeviceAsync()
    {
        if (SelectedDevice is null) return;
        long? down = ParseMegabits(EditDownloadLimit);
        long? up = ParseMegabits(EditUploadLimit);
        if ((!string.IsNullOrWhiteSpace(EditDownloadLimit) && down is null) ||
            (!string.IsNullOrWhiteSpace(EditUploadLimit) && up is null))
        {
            Notify("Invalid limit", "Enter a positive Mbps value or leave the field empty for Unlimited.", NotificationSeverity.Warning);
            return;
        }

        DevicePolicy policy = new(
            SelectedDevice.Id,
            EditBlocked,
            down,
            up,
            EditPriority,
            EditGroup?.Id);
        OperationResult rename = await RunOperationAsync(
            token => _client.RenameDeviceAsync(new RenameDeviceRequest(SelectedDevice.Id, EditName, EditDeviceType), token),
            "Save device",
            false);
        if (!rename.Success) return;
        OperationResult saved = await RunOperationAsync(token => _client.UpdatePolicyAsync(policy, token), "Save policy");
        if (saved.Success) IsDeviceEditorVisible = false;
    }

    [RelayCommand]
    private async Task ToggleDeviceBlockAsync(DeviceRowViewModel? device)
    {
        if (device is null || !device.CanEditPolicy || device.IsBlockPending) return;

        bool blockInternet = !device.BlockInternet;
        DevicePolicy policy = device.Source.Policy with { BlockInternet = blockInternet };
        device.SetBlockPending(true);
        try
        {
            await RunOperationAsync(
                token => _client.UpdatePolicyAsync(policy, token),
                blockInternet ? "Block internet" : "Restore internet",
                false);
        }
        finally
        {
            device.SetBlockPending(false);
        }
    }

    [RelayCommand]
    private void ResetDevice(DeviceRowViewModel? device)
    {
        if (device is null || !device.CanEditPolicy) return;
        PendingResetDevice = device;
        IsResetDeviceConfirmationVisible = true;
    }

    [RelayCommand]
    private void CancelResetDevice()
    {
        IsResetDeviceConfirmationVisible = false;
        PendingResetDevice = null;
    }

    [RelayCommand]
    private async Task ConfirmResetDeviceAsync()
    {
        DeviceRowViewModel? device = PendingResetDevice;
        if (device is null) return;

        IsResetDeviceConfirmationVisible = false;
        PendingResetDevice = null;

        await RunOperationAsync(
            token => _client.ResetDeviceAsync(new ResetDeviceRequest(device.Id), token),
            "Reset device");
    }

    [RelayCommand]
    private void BlockAll()
    {
        if (Devices.All(item => item.IsGateway || item.IsLocalComputer || !item.IsOnline))
        {
            Notify("No eligible devices", "There are no online devices available to block.", NotificationSeverity.Info);
            return;
        }

        IsBlockAllConfirmationVisible = true;
    }

    [RelayCommand]
    private void CancelBlockAll() => IsBlockAllConfirmationVisible = false;

    [RelayCommand]
    private async Task ConfirmBlockAllAsync()
    {
        IsBlockAllConfirmationVisible = false;

        DeviceRowViewModel[] eligible = Devices.Where(item => !item.IsGateway && !item.IsLocalComputer && item.IsOnline).ToArray();
        foreach (DeviceRowViewModel device in eligible)
        {
            DevicePolicy policy = device.Source.Policy with { BlockInternet = true };
            await _client.UpdatePolicyAsync(policy, CancellationToken.None);
        }
        Notify("Internet blocked", $"Applied to {eligible.Length} eligible devices.", NotificationSeverity.Warning);
    }

    [RelayCommand]
    private async Task CreateDefaultGroupsAsync()
    {
        string[] names = ["Family", "Guests", "Work"];
        foreach (string name in names.Where(name => Groups.All(group => !string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))))
        {
            GroupPolicy group = new(Guid.NewGuid().ToString("N"), name, null, null, DevicePriority.Normal, false);
            await _client.SaveGroupAsync(group, CancellationToken.None);
        }
        Notify("Groups ready", "Family, Guests, and Work groups are available.", NotificationSeverity.Success);
    }

    [RelayCommand]
    private void EditGroupPolicy(GroupPolicy? group)
    {
        if (group is null) return;
        GroupBeingEdited = group;
        GroupEditName = group.Name;
        GroupEditDownloadLimit = FormatMegabits(group.DownloadLimitBitsPerSecond);
        GroupEditUploadLimit = FormatMegabits(group.UploadLimitBitsPerSecond);
        GroupEditPriority = group.Priority;
        GroupEditBlocked = group.BlockInternet;
        IsGroupEditorVisible = true;
    }

    [RelayCommand]
    private void NewGroup()
    {
        GroupBeingEdited = null;
        GroupEditName = string.Empty;
        GroupEditDownloadLimit = string.Empty;
        GroupEditUploadLimit = string.Empty;
        GroupEditPriority = DevicePriority.Normal;
        GroupEditBlocked = false;
        IsGroupEditorVisible = true;
    }

    [RelayCommand]
    private void CloseGroupEditor() => IsGroupEditorVisible = false;

    [RelayCommand]
    private async Task SaveGroupPolicyAsync()
    {
        long? down = ParseMegabits(GroupEditDownloadLimit);
        long? up = ParseMegabits(GroupEditUploadLimit);
        if (string.IsNullOrWhiteSpace(GroupEditName) ||
            (!string.IsNullOrWhiteSpace(GroupEditDownloadLimit) && down is null) ||
            (!string.IsNullOrWhiteSpace(GroupEditUploadLimit) && up is null))
        {
            Notify("Invalid group policy", "Enter a group name and positive Mbps limits, or leave limits empty for Unlimited.", NotificationSeverity.Warning);
            return;
        }

        GroupPolicy updated = new(
            GroupBeingEdited?.Id ?? Guid.NewGuid().ToString("N"),
            GroupEditName.Trim(),
            down,
            up,
            GroupEditPriority,
            GroupEditBlocked);
        OperationResult saved = await RunOperationAsync(
            token => _client.SaveGroupAsync(updated, token),
            "Save group policy");
        if (saved.Success) IsGroupEditorVisible = false;
    }

    [RelayCommand]
    private void DeleteGroup(GroupPolicy? group)
    {
        if (group is null) return;
        RequestRuleDelete("group", group.Id, group.Name);
    }

    [RelayCommand]
    private async Task AddEveningScheduleAsync()
    {
        if (SelectedDevice is null)
        {
            Notify("Select a device", "Choose a device before creating a schedule.", NotificationSeverity.Warning);
            return;
        }
        ScheduleRule schedule = new(
            Guid.NewGuid().ToString("N"),
            $"{SelectedDevice.DisplayName} evening limit",
            SelectedDevice.Id,
            null,
            Enum.GetValues<DayOfWeek>(),
            new TimeOnly(22, 0),
            new TimeOnly(7, 0),
            false,
            2_000_000,
            1_000_000,
            true);
        await RunOperationAsync(token => _client.SaveScheduleAsync(schedule, token), "Create schedule");
    }

    [RelayCommand]
    private void NewSchedule()
    {
        ScheduleBeingEdited = null;
        ScheduleEditName = "New schedule";
        ScheduleTargetType = EditableDevices.Any() ? "Device" : "Group";
        ScheduleEditDevice = SelectedDevice?.CanEditPolicy == true ? SelectedDevice : EditableDevices.FirstOrDefault();
        ScheduleEditGroup = Groups.FirstOrDefault();
        ScheduleEditStart = "22:00";
        ScheduleEditEnd = "07:00";
        ScheduleEditDownloadLimit = string.Empty;
        ScheduleEditUploadLimit = string.Empty;
        ScheduleEditBlocked = false;
        ScheduleEditEnabled = true;
        SetScheduleDays(Enum.GetValues<DayOfWeek>());
        IsScheduleEditorVisible = true;
    }

    [RelayCommand]
    private void EditSchedule(ScheduleRule? schedule)
    {
        if (schedule is null) return;
        ScheduleBeingEdited = schedule;
        ScheduleEditName = schedule.Name;
        ScheduleTargetType = schedule.GroupId is not null ? "Group" : "Device";
        ScheduleEditDevice = Devices.FirstOrDefault(device => device.Id == schedule.DeviceId);
        ScheduleEditGroup = Groups.FirstOrDefault(group => group.Id == schedule.GroupId);
        ScheduleEditStart = schedule.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        ScheduleEditEnd = schedule.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        ScheduleEditDownloadLimit = FormatMegabits(schedule.DownloadLimitBitsPerSecond);
        ScheduleEditUploadLimit = FormatMegabits(schedule.UploadLimitBitsPerSecond);
        ScheduleEditBlocked = schedule.BlockInternet;
        ScheduleEditEnabled = schedule.Enabled;
        SetScheduleDays(schedule.Days);
        IsScheduleEditorVisible = true;
    }

    [RelayCommand]
    private void CloseScheduleEditor() => IsScheduleEditorVisible = false;

    [RelayCommand]
    private async Task SaveScheduleEditorAsync()
    {
        long? down = ParseMegabits(ScheduleEditDownloadLimit);
        long? up = ParseMegabits(ScheduleEditUploadLimit);
        DayOfWeek[] days = GetScheduleDays();
        bool targetMissing = IsScheduleGroupTarget ? ScheduleEditGroup is null : ScheduleEditDevice is null;
        if (string.IsNullOrWhiteSpace(ScheduleEditName) || targetMissing || days.Length == 0 ||
            !TryParseScheduleTime(ScheduleEditStart, out TimeOnly start) ||
            !TryParseScheduleTime(ScheduleEditEnd, out TimeOnly end) ||
            (!string.IsNullOrWhiteSpace(ScheduleEditDownloadLimit) && down is null) ||
            (!string.IsNullOrWhiteSpace(ScheduleEditUploadLimit) && up is null))
        {
            Notify("Invalid schedule", "Enter a name, target, at least one day, valid times (HH:mm), and positive limits or Unlimited.", NotificationSeverity.Warning);
            return;
        }

        ScheduleRule schedule = new(
            ScheduleBeingEdited?.Id ?? Guid.NewGuid().ToString("N"),
            ScheduleEditName.Trim(),
            IsScheduleGroupTarget ? null : ScheduleEditDevice!.Id,
            IsScheduleGroupTarget ? ScheduleEditGroup!.Id : null,
            days,
            start,
            end,
            ScheduleEditBlocked,
            down,
            up,
            ScheduleEditEnabled);
        OperationResult saved = await RunOperationAsync(
            token => _client.SaveScheduleAsync(schedule, token),
            ScheduleBeingEdited is null ? "Create schedule" : "Update schedule");
        if (saved.Success) IsScheduleEditorVisible = false;
    }

    [RelayCommand]
    private void DeleteSchedule(ScheduleRule? schedule)
    {
        if (schedule is null) return;
        RequestRuleDelete("schedule", schedule.Id, schedule.Name);
    }

    [RelayCommand]
    private void EditDevice(DeviceRowViewModel? device)
    {
        if (device is null || !device.CanEditPolicy) return;
        SelectedDevice = device;
        IsDeviceEditorVisible = true;
        DeviceEditRequested?.Invoke(device);
    }

    [RelayCommand]
    private void CloseDeviceEditor() => IsDeviceEditorVisible = false;

    [RelayCommand]
    private void ResetEditorToGroupDefaults()
    {
        if (EditGroup is null)
        {
            Notify("No group selected", "Select a group before using its default policy.", NotificationSeverity.Warning);
            return;
        }

        EditBlocked = false;
        EditDownloadLimit = string.Empty;
        EditUploadLimit = string.Empty;
        EditPriority = DevicePriority.Normal;
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (string.IsNullOrWhiteSpace(PresetName))
        {
            Notify("Preset name required", "Enter a name for the current rule configuration.", NotificationSeverity.Warning);
            return;
        }
        OperationResult result = await RunOperationAsync(
            token => _client.SavePresetAsync(new SavePresetRequest(PresetName, PresetBeingEdited?.Id), token),
            PresetBeingEdited is null ? "Create preset" : "Rename preset");
        if (result.Success)
        {
            PresetName = string.Empty;
            PresetBeingEdited = null;
            IsPresetEditorVisible = false;
        }
    }

    [RelayCommand]
    private void NewPreset()
    {
        PresetBeingEdited = null;
        PresetName = string.Empty;
        IsPresetEditorVisible = true;
    }

    [RelayCommand]
    private void EditPreset(RulePreset? preset)
    {
        if (preset is null) return;
        PresetBeingEdited = preset;
        PresetName = preset.Name;
        IsPresetEditorVisible = true;
    }

    [RelayCommand]
    private void ClosePresetEditor()
    {
        IsPresetEditorVisible = false;
        PresetBeingEdited = null;
    }

    [RelayCommand]
    private void DeletePreset(RulePreset? preset)
    {
        if (preset is null) return;
        RequestRuleDelete("preset", preset.Id, preset.Name);
    }

    [RelayCommand]
    private async Task ApplyPresetAsync()
    {
        if (SelectedPreset is null)
        {
            Notify("Select a preset", "Choose a saved preset to apply.", NotificationSeverity.Warning);
            return;
        }
        await RunOperationAsync(
            token => _client.ApplyPresetAsync(new ApplyPresetRequest(SelectedPreset.Id), token),
            "Apply preset");
    }

    [RelayCommand]
    private async Task ApplyPresetItemAsync(RulePreset? preset)
    {
        if (preset is null) return;
        SelectedPreset = preset;
        await RunOperationAsync(
            token => _client.ApplyPresetAsync(new ApplyPresetRequest(preset.Id), token),
            "Apply preset");
    }

    [RelayCommand]
    private Task RefreshApplicationsAsync() => RefreshApplicationsCoreAsync(true);

    [RelayCommand]
    private void OpenApplicationControl() => ApplicationControlRequested?.Invoke();

    [RelayCommand]
    private static void OpenProjectWebsite() => OpenWebAddress(ProjectUrl);

    [RelayCommand]
    private static void OpenIssues() => OpenWebAddress(IssuesUrl);

    [RelayCommand]
    private static void OpenAuthorProfile() => OpenWebAddress(AuthorUrl);

    public Task RefreshApplicationsSilentlyAsync() => RefreshApplicationsCoreAsync(false);

    private async Task RefreshApplicationsCoreAsync(bool notifyErrors)
    {
        if (Interlocked.CompareExchange(ref _applicationRefreshRunning, 1, 0) != 0) return;
        bool showProgress = notifyErrors;
        bool showInitialLoader = notifyErrors && Applications.Count == 0;
        try
        {
            if (showProgress) IsApplicationRefreshInProgress = true;
            if (showInitialLoader) IsApplicationListLoading = true;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            IReadOnlyList<LocalApplicationSnapshot> snapshots = await _client.GetApplicationsAsync(timeout.Token);
            Dictionary<string, ApplicationRowViewModel> current = Applications.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            bool membershipChanged = false;
            bool orderingChanged = false;
            bool filterIdentityChanged = false;
            foreach (LocalApplicationSnapshot snapshot in snapshots)
            {
                if (current.Remove(snapshot.Id, out ApplicationRowViewModel? row))
                {
                    orderingChanged |= row.IsRunning != snapshot.IsRunning;
                    filterIdentityChanged |=
                        !string.Equals(row.DisplayName, snapshot.DisplayName, StringComparison.Ordinal) ||
                        !string.Equals(row.ExecutablePath, snapshot.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                    row.Apply(snapshot);
                }
                else
                {
                    Applications.Add(new ApplicationRowViewModel(snapshot));
                    membershipChanged = true;
                }
            }
            foreach (ApplicationRowViewModel removed in current.Values)
            {
                Applications.Remove(removed);
                membershipChanged = true;
            }

            // Rebuilding the ListCollectionView every second recreates row visuals,
            // interrupts hover/search animations, and causes visible flicker. Rate
            // changes are raised by the existing row and require no view refresh.
            if (membershipChanged || orderingChanged ||
                (filterIdentityChanged && !string.IsNullOrWhiteSpace(ApplicationSearchText)))
                ApplicationsView.Refresh();
            RaiseApplicationDashboardProperties();
        }
        catch (Exception ex)
        {
            if (notifyErrors)
                Notify("Application scan failed", ex.Message, NotificationSeverity.Error);
        }
        finally
        {
            if (showInitialLoader) IsApplicationListLoading = false;
            if (showProgress) IsApplicationRefreshInProgress = false;
            Interlocked.Exchange(ref _applicationRefreshRunning, 0);
        }
    }

    [RelayCommand]
    private void EditApplication(ApplicationRowViewModel? application)
    {
        if (application is null) return;
        SelectedApplication = application;
        ApplicationDownloadLimit = FormatMegabits(application.Policy?.DownloadLimitBitsPerSecond);
        ApplicationUploadLimit = FormatMegabits(application.Policy?.UploadLimitBitsPerSecond);
        ApplicationBlocked = application.Policy?.BlockInternet == true;
        IsApplicationEditorVisible = true;
    }

    [RelayCommand]
    private void CloseApplicationEditor() => IsApplicationEditorVisible = false;

    [RelayCommand]
    private async Task SaveApplicationPolicyAsync()
    {
        if (SelectedApplication is null) return;
        long? download = ParseMegabits(ApplicationDownloadLimit);
        long? upload = ParseMegabits(ApplicationUploadLimit);
        if (!string.IsNullOrWhiteSpace(ApplicationDownloadLimit) && download is null)
        {
            Notify("Invalid download limit", "Enter a positive Mbps value or leave it empty for Unlimited.", NotificationSeverity.Warning);
            return;
        }
        if (!string.IsNullOrWhiteSpace(ApplicationUploadLimit) && upload is null)
        {
            Notify("Invalid upload limit", "Enter a positive Mbps value or leave it empty for Unlimited.", NotificationSeverity.Warning);
            return;
        }

        LocalApplicationPolicy policy = new(
            SelectedApplication.Id,
            SelectedApplication.DisplayName,
            SelectedApplication.ExecutablePath,
            upload,
            ApplicationBlocked,
            download);
        OperationResult result = await RunOperationAsync(
            token => _client.SaveApplicationPolicyAsync(policy, token),
            "Save application policy");
        if (!result.Success) return;
        IsApplicationEditorVisible = false;
        await RefreshApplicationsAsync();
    }

    [RelayCommand]
    private async Task ResetApplicationPolicyAsync(ApplicationRowViewModel? application)
    {
        if (application?.Policy is null) return;
        OperationResult result = await RunOperationAsync(
            token => _client.DeleteApplicationPolicyAsync(application.Id, token),
            "Reset application policy");
        if (result.Success) await RefreshApplicationsAsync();
    }

    [RelayCommand]
    private async Task ToggleApplicationBlockAsync(ApplicationRowViewModel? application)
    {
        if (application is null || application.IsBlockPending) return;
        LocalApplicationPolicy policy = application.Policy ?? new LocalApplicationPolicy(
            application.Id,
            application.DisplayName,
            application.ExecutablePath,
            null,
            false,
            null);
        policy = policy with { BlockInternet = !application.IsBlocked };

        application.SetBlockPending(true);
        try
        {
            OperationResult result = await RunOperationAsync(
                token => _client.SaveApplicationPolicyAsync(policy, token),
                policy.BlockInternet ? "Block application" : "Restore application internet",
                false);
            if (result.Success) await RefreshApplicationsCoreAsync(false);
        }
        finally
        {
            application.SetBlockPending(false);
        }
    }

    [RelayCommand]
    private void CancelRuleDelete()
    {
        IsRuleDeleteConfirmationVisible = false;
        PendingDeleteKind = string.Empty;
        PendingDeleteId = string.Empty;
        PendingDeleteName = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmRuleDeleteAsync()
    {
        string kind = PendingDeleteKind;
        string id = PendingDeleteId;
        IsRuleDeleteConfirmationVisible = false;

        OperationResult result = kind switch
        {
            "group" => await RunOperationAsync(token => _client.DeleteGroupAsync(id, token), "Delete group"),
            "schedule" => await RunOperationAsync(token => _client.DeleteScheduleAsync(id, token), "Delete schedule"),
            "preset" => await RunOperationAsync(token => _client.DeletePresetAsync(id, token), "Delete preset"),
            _ => new OperationResult(false, "Unknown item type.")
        };

        if (result.Success)
        {
            PendingDeleteKind = string.Empty;
            PendingDeleteId = string.Empty;
            PendingDeleteName = string.Empty;
        }
    }

    private void RequestRuleDelete(string kind, string id, string name)
    {
        PendingDeleteKind = kind;
        PendingDeleteId = id;
        PendingDeleteName = name;
        IsRuleDeleteConfirmationVisible = true;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (SelectedAdapter is null) return;
        OperationResult networkResult = await RunOperationAsync(
            token => _client.UpdateNetworkSettingsAsync(new UpdateNetworkSettingsRequest(SelectedAdapter.Id, AutoControl), token),
            "Save network settings",
            false);
        if (!networkResult.Success) return;
        AppSettings settings = new(
            SelectedAdapter.Id,
            AutoControl,
            HistoryRetentionDays,
            NotifyNewDevices,
            DisplayRatesAsBytes,
            false,
            AutoAssignNewDevicesToGuests);
        await RunOperationAsync(token => _client.UpdateSettingsAsync(settings, token), "Save settings");
    }

    [RelayCommand]
    private async Task CompleteOnboardingAsync()
    {
        if (!AuthorizedNetwork)
        {
            Notify("Confirmation required", "Confirm that you own or administer this network.", NotificationSeverity.Warning);
            return;
        }
        if (SelectedAdapter is null)
        {
            Notify("Adapter required", "Select a network adapter.", NotificationSeverity.Warning);
            return;
        }
        await SaveSettingsAsync();
        await RunOperationAsync(token => _client.SetControlAsync(true, token), "Start automatic control", false);
        UiSettings current = _uiSettingsStore.Load();
        _uiSettingsStore.Save(current with { FirstRunComplete = true });
        IsOnboardingVisible = false;
    }

    [RelayCommand]
    private static void OpenNpcapDownload() =>
        Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Export LanPilot backup",
            Filter = "LanPilot backup (*.json)|*.json",
            FileName = $"LanPilot-backup-{DateTime.Now:yyyy-MM-dd}.json"
        };
        if (dialog.ShowDialog() == true)
            await RunOperationAsync(token => _client.ExportAsync(new ExportRequest(dialog.FileName, false), token), "Export backup");
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Title = "Import LanPilot backup", Filter = "LanPilot backup (*.json)|*.json" };
        if (dialog.ShowDialog() == true)
            await RunOperationAsync(token => _client.ImportAsync(new ImportRequest(dialog.FileName), token), "Import backup");
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        Microsoft.Win32.SaveFileDialog dialog = new()
        {
            Title = "Export diagnostics bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"LanPilot-diagnostics-{DateTime.Now:yyyy-MM-dd-HHmm}.zip"
        };
        if (dialog.ShowDialog() == true)
            await RunOperationAsync(
                token => _client.ExportDiagnosticsAsync(new ExportRequest(dialog.FileName, false), token),
                "Export diagnostics");
    }

    private async Task<OperationResult> RunOperationAsync(
        Func<CancellationToken, Task<OperationResult>> operation,
        string title,
        bool notifySuccess = true)
    {
        try
        {
            IsBusy = true;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            OperationResult result = await operation(timeout.Token);
            if (!result.Success || notifySuccess)
            {
                Notify(title, result.Message, result.Success ? NotificationSeverity.Success : NotificationSeverity.Error);
            }
            return result;
        }
        catch (Exception ex)
        {
            Notify(title, ex.Message, NotificationSeverity.Error);
            return new OperationResult(false, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnSnapshotReceived(object? sender, DashboardSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _pendingSnapshot = snapshot;
            if (_snapshotDispatchQueued) return;
            _snapshotDispatchQueued = true;
        }

        _ = WpfApplication.Current.Dispatcher.InvokeAsync(
            ApplyLatestSnapshot,
            DispatcherPriority.Background);
    }

    private void ApplyLatestSnapshot()
    {
        DashboardSnapshot? snapshot;
        lock (_snapshotGate)
        {
            snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
            _snapshotDispatchQueued = false;
        }

        if (snapshot is not null) ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(DashboardSnapshot snapshot)
    {
        _isApplyingSnapshot = true;
        try
        {
            StatusMessage = snapshot.Status.Message;
            EngineMode = snapshot.Status.Mode;
            IsControlActive = snapshot.Status.Mode == EngineMode.Controlling;
            NpcapAvailable = snapshot.Status.NpcapAvailable;
            NpcapVersion = snapshot.Status.NpcapVersion ?? "Not detected";
            Ipv6Detected = snapshot.Status.Ipv6Detected;
            AutoControl = snapshot.Settings.AutoControl;
            NotifyNewDevices = snapshot.Settings.NotifyNewDevices;
            AutoAssignNewDevicesToGuests = snapshot.Settings.AutoAssignNewDevicesToGuests;
            DisplayRatesAsBytes = snapshot.Settings.DisplayRatesAsBytes;
            ActiveDeviceMonitoring = false;
            HistoryRetentionDays = snapshot.Settings.HistoryRetentionDays;

            ReplaceCollection(Adapters, snapshot.Adapters);
            SelectedAdapter = Adapters.FirstOrDefault(item => item.Id == snapshot.Settings.SelectedAdapterId) ?? Adapters.FirstOrDefault();
            string? editGroupId = EditGroup?.Id;
            string? scheduleEditGroupId = ScheduleEditGroup?.Id;
            ReplaceCollection(Groups, snapshot.Groups);
            OnPropertyChanged(nameof(HasGroups));
            if (editGroupId is not null)
            {
                EditGroup = ResolveGroup(editGroupId);
            }
            if (scheduleEditGroupId is not null)
            {
                ScheduleEditGroup = ResolveGroup(scheduleEditGroupId);
            }
            ReplaceCollection(Schedules, snapshot.Schedules);
            ReplaceCollection(Presets, snapshot.Presets);
            OnPropertyChanged(nameof(HasSchedules));
            OnPropertyChanged(nameof(HasPresets));

            HashSet<string> incomingIds = snapshot.Devices.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool deviceMembershipChanged = false;
            bool deviceOrderingChanged = false;
            foreach (string removed in _deviceRows.Keys.Where(id => !incomingIds.Contains(id)).ToArray())
            {
                Devices.Remove(_deviceRows[removed]);
                _deviceRows.Remove(removed);
                deviceMembershipChanged = true;
            }
            foreach (DeviceSnapshot device in snapshot.Devices)
            {
                GroupPolicy? group = ResolveGroup(device.Policy.GroupId);
                if (!_deviceRows.TryGetValue(device.Id, out DeviceRowViewModel? row))
                {
                    row = new DeviceRowViewModel(device, FormatRate, snapshot.Status.Mode == EngineMode.Controlling, group);
                    _deviceRows[device.Id] = row;
                    Devices.Add(row);
                    deviceMembershipChanged = true;
                }
                else
                {
                    deviceOrderingChanged |= row.IsOnline != device.IsOnline;
                    row.Apply(device, FormatRate, snapshot.Status.Mode == EngineMode.Controlling, group);
                }
            }

            if (deviceMembershipChanged || deviceOrderingChanged || !string.IsNullOrWhiteSpace(SearchText)) DevicesView.Refresh();
            OnPropertyChanged(nameof(EditableDevices));
            RaiseDashboardProperties();
            if (SelectedDevice is not null && _deviceRows.TryGetValue(SelectedDevice.Id, out DeviceRowViewModel? selected))
            {
                SelectedDevice = selected;
            }
        }
        finally
        {
            _isApplyingSnapshot = false;
        }
    }

    private void LoadEditor(DeviceRowViewModel? row)
    {
        if (row is null) return;
        EditName = row.DisplayName;
        EditDeviceType = row.DeviceType;
        EditBlocked = row.BlockInternet;
        EditDownloadLimit = FormatMegabits(row.Source.Policy.DownloadLimitBitsPerSecond);
        EditUploadLimit = FormatMegabits(row.Source.Policy.UploadLimitBitsPerSecond);
        EditPriority = row.Source.Policy.Priority;
        EditGroup = Groups.FirstOrDefault(item => item.Id == row.Source.Policy.GroupId);
    }

    private GroupPolicy? ResolveGroup(string? groupId) =>
        Groups.FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.OrdinalIgnoreCase));

    private bool FilterDevice(object item) =>
        item is DeviceRowViewModel row &&
        (string.IsNullOrWhiteSpace(SearchText) ||
         row.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         row.Ipv4Address.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         row.MacAddress.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    private bool FilterApplication(object item) =>
        item is ApplicationRowViewModel row &&
        (string.IsNullOrWhiteSpace(ApplicationSearchText) ||
         row.DisplayName.Contains(ApplicationSearchText, StringComparison.OrdinalIgnoreCase) ||
         row.ExecutablePath.Contains(ApplicationSearchText, StringComparison.OrdinalIgnoreCase));

    private string FormatRate(long bitsPerSecond)
    {
        if (DisplayRatesAsBytes)
        {
            double bytes = bitsPerSecond / 8d;
            return bytes >= 1_000_000 ? $"{bytes / 1_000_000:0.##} MB/s" : $"{bytes / 1_000:0.#} KB/s";
        }
        return bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond / 1_000_000d:0.##} Mbps"
            : $"{bitsPerSecond / 1_000d:0.#} Kbps";
    }

    private static long? ParseMegabits(string value) =>
        string.IsNullOrWhiteSpace(value) ? null :
        double.TryParse(value, out double parsed) && parsed > 0 ? checked((long)(parsed * 1_000_000d)) : null;

    private static string FormatMegabits(long? value) =>
        value is null ? string.Empty : (value.Value / 1_000_000d).ToString("0.###");

    private static bool TryParseScheduleTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(
            value.Trim(),
            ["H:mm", "HH:mm"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out time);

    private DayOfWeek[] GetScheduleDays()
    {
        List<DayOfWeek> days = [];
        if (ScheduleMonday) days.Add(DayOfWeek.Monday);
        if (ScheduleTuesday) days.Add(DayOfWeek.Tuesday);
        if (ScheduleWednesday) days.Add(DayOfWeek.Wednesday);
        if (ScheduleThursday) days.Add(DayOfWeek.Thursday);
        if (ScheduleFriday) days.Add(DayOfWeek.Friday);
        if (ScheduleSaturday) days.Add(DayOfWeek.Saturday);
        if (ScheduleSunday) days.Add(DayOfWeek.Sunday);
        return days.ToArray();
    }

    private void SetScheduleDays(IEnumerable<DayOfWeek> days)
    {
        HashSet<DayOfWeek> selected = days.ToHashSet();
        ScheduleMonday = selected.Contains(DayOfWeek.Monday);
        ScheduleTuesday = selected.Contains(DayOfWeek.Tuesday);
        ScheduleWednesday = selected.Contains(DayOfWeek.Wednesday);
        ScheduleThursday = selected.Contains(DayOfWeek.Thursday);
        ScheduleFriday = selected.Contains(DayOfWeek.Friday);
        ScheduleSaturday = selected.Contains(DayOfWeek.Saturday);
        ScheduleSunday = selected.Contains(DayOfWeek.Sunday);
    }

    private void RaiseDashboardProperties()
    {
        OnPropertyChanged(nameof(OnlineCount));
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(TotalDownloadText));
        OnPropertyChanged(nameof(TotalUploadText));
        OnPropertyChanged(nameof(ControlButtonText));
    }

    private ApplicationRowViewModel? MostActiveApplication => Applications
        .Where(item => item.IsRunning)
        .OrderByDescending(item => item.DownloadBitsPerSecond + item.UploadBitsPerSecond)
        .FirstOrDefault();

    private void RaiseApplicationDashboardProperties()
    {
        OnPropertyChanged(nameof(RunningApplicationCount));
        OnPropertyChanged(nameof(RestrictedApplicationCount));
        OnPropertyChanged(nameof(BlockedApplicationCount));
        OnPropertyChanged(nameof(ApplicationDownloadBitsPerSecond));
        OnPropertyChanged(nameof(ApplicationUploadBitsPerSecond));
        OnPropertyChanged(nameof(ApplicationDownloadText));
        OnPropertyChanged(nameof(ApplicationUploadText));
        OnPropertyChanged(nameof(MostActiveApplicationName));
        OnPropertyChanged(nameof(MostActiveApplicationTrafficText));
    }

    private void Notify(string title, string message, NotificationSeverity severity) =>
        NotificationRequested?.Invoke(this, new NotificationEvent(title, message, severity));

    private static void ReplaceCollection<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        T[] incoming = values.ToArray();
        if (target.Count == incoming.Length && target.SequenceEqual(incoming)) return;

        target.Clear();
        foreach (T value in incoming) target.Add(value);
    }

    private static bool SafeGetStartupState(bool fallback)
    {
        try { return StartupManager.IsEnabled(); } catch { return fallback; }
    }

    private static void OpenWebAddress(string address) =>
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
}

public enum RateTrend
{
    Steady,
    Increasing,
    Decreasing
}

public sealed class ApplicationRowViewModel : ObservableObject
{
    private LocalApplicationSnapshot _source;
    private ImageSource? _applicationIcon;
    private RateTrend _downloadTrend;
    private RateTrend _uploadTrend;
    private bool _isBlockPending;

    public ApplicationRowViewModel(LocalApplicationSnapshot source)
    {
        _source = source;
        _ = LoadIconAsync();
    }

    public string Id => _source.Id;
    public string DisplayName => _source.DisplayName;
    public string ExecutablePath => _source.ExecutablePath;
    public string ExecutableName => Path.GetFileName(_source.ExecutablePath);
    public ImageSource? ApplicationIcon => _applicationIcon;
    public bool HasApplicationIcon => _applicationIcon is not null;
    public bool IsRunning => _source.IsRunning;
    public string StatusText => _source.IsRunning ? "Running" : "Not running";
    public string ProcessText => _source.ProcessIds.Count == 0
        ? "—"
        : string.Join(", ", _source.ProcessIds.Take(3));
    public LocalApplicationPolicy? Policy => _source.Policy;
    public bool HasPolicy => _source.Policy is not null;
    public bool IsBlocked => _source.Policy?.BlockInternet == true;
    public bool IsBlockPending => _isBlockPending;
    public bool CanToggleBlock => !_isBlockPending;
    public string BlockActionText => IsBlocked ? "Unblock" : "Block";
    public long DownloadBitsPerSecond => _source.DownloadBitsPerSecond;
    public long UploadBitsPerSecond => _source.UploadBitsPerSecond;
    public string DownloadDisplay => FormatRate(DownloadBitsPerSecond);
    public string UploadDisplay => FormatRate(UploadBitsPerSecond);
    public RateTrend DownloadTrend => _downloadTrend;
    public RateTrend UploadTrend => _uploadTrend;
    public string LimitsText => $"↓ {FormatLimit(_source.Policy?.DownloadLimitBitsPerSecond)}  ↑ {FormatLimit(_source.Policy?.UploadLimitBitsPerSecond)}";
    public string DownloadLimitText => _source.Policy?.DownloadLimitBitsPerSecond is long downloadLimit
        ? $"{downloadLimit / 1_000_000d:0.##} Mbps"
        : "Unlimited";
    public string UploadLimitText => _source.Policy?.UploadLimitBitsPerSecond is long limit
        ? $"{limit / 1_000_000d:0.##} Mbps"
        : "Unlimited";

    public void Apply(LocalApplicationSnapshot source)
    {
        LocalApplicationSnapshot previous = _source;
        bool identityChanged =
            !string.Equals(previous.DisplayName, source.DisplayName, StringComparison.Ordinal) ||
            !string.Equals(previous.ExecutablePath, source.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        bool runningChanged = previous.IsRunning != source.IsRunning;
        bool processesChanged = !previous.ProcessIds.SequenceEqual(source.ProcessIds);
        bool policyChanged = previous.Policy != source.Policy;
        bool downloadChanged = previous.DownloadBitsPerSecond != source.DownloadBitsPerSecond;
        bool uploadChanged = previous.UploadBitsPerSecond != source.UploadBitsPerSecond;
        RateTrend downloadTrend = CalculateTrend(previous.DownloadBitsPerSecond, source.DownloadBitsPerSecond);
        RateTrend uploadTrend = CalculateTrend(previous.UploadBitsPerSecond, source.UploadBitsPerSecond);
        bool downloadTrendChanged = _downloadTrend != downloadTrend;
        bool uploadTrendChanged = _uploadTrend != uploadTrend;

        _source = source;
        _downloadTrend = downloadTrend;
        _uploadTrend = uploadTrend;

        if (identityChanged)
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(ExecutablePath));
            OnPropertyChanged(nameof(ExecutableName));
        }
        if (runningChanged)
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(StatusText));
        }
        if (processesChanged) OnPropertyChanged(nameof(ProcessText));
        if (policyChanged)
        {
            OnPropertyChanged(nameof(Policy));
            OnPropertyChanged(nameof(HasPolicy));
            OnPropertyChanged(nameof(IsBlocked));
            OnPropertyChanged(nameof(BlockActionText));
            OnPropertyChanged(nameof(LimitsText));
            OnPropertyChanged(nameof(DownloadLimitText));
            OnPropertyChanged(nameof(UploadLimitText));
        }
        if (downloadChanged)
        {
            OnPropertyChanged(nameof(DownloadBitsPerSecond));
            OnPropertyChanged(nameof(DownloadDisplay));
        }
        if (uploadChanged)
        {
            OnPropertyChanged(nameof(UploadBitsPerSecond));
            OnPropertyChanged(nameof(UploadDisplay));
        }
        if (downloadChanged || downloadTrendChanged) OnPropertyChanged(nameof(DownloadTrend));
        if (uploadChanged || uploadTrendChanged) OnPropertyChanged(nameof(UploadTrend));
    }

    public void SetBlockPending(bool value)
    {
        if (_isBlockPending == value) return;
        _isBlockPending = value;
        OnPropertyChanged(nameof(IsBlockPending));
        OnPropertyChanged(nameof(CanToggleBlock));
    }

    private static string FormatRate(long bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000) return $"{bitsPerSecond / 1_000_000d:0.##} Mbps";
        if (bitsPerSecond >= 1_000) return $"{bitsPerSecond / 1_000d:0.#} Kbps";
        return $"{bitsPerSecond} bps";
    }

    private static string FormatLimit(long? bitsPerSecond) => bitsPerSecond switch
    {
        null => "Unlimited",
        >= 1_000_000 => $"{bitsPerSecond.Value / 1_000_000d:0.##}M",
        _ => $"{bitsPerSecond.Value / 1_000d:0.#}K"
    };

    private static RateTrend CalculateTrend(long previous, long current)
    {
        long difference = current - previous;
        long threshold = Math.Max(8_000, previous / 20);
        if (Math.Abs(difference) < threshold) return RateTrend.Steady;
        return difference > 0 ? RateTrend.Increasing : RateTrend.Decreasing;
    }

    private async Task LoadIconAsync()
    {
        ImageSource? icon = await Task.Run(() =>
        {
            try
            {
                using System.Drawing.Icon? extracted = System.Drawing.Icon.ExtractAssociatedIcon(ExecutablePath);
                if (extracted is null) return null;
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                    extracted.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(28, 28));
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        });

        if (icon is null) return;
        _applicationIcon = icon;
        OnPropertyChanged(nameof(ApplicationIcon));
        OnPropertyChanged(nameof(HasApplicationIcon));
    }
}

public sealed class DeviceRowViewModel : ObservableObject
{
    private DeviceSnapshot _source;
    private Func<long, string> _formatter;
    private bool _controlActive;
    private GroupPolicy? _group;
    private bool _isBlockPending;
    private RateTrend _downloadTrend;
    private RateTrend _uploadTrend;

    public DeviceRowViewModel(DeviceSnapshot source, Func<long, string> formatter, bool controlActive, GroupPolicy? group)
    {
        _source = source;
        _formatter = formatter;
        _controlActive = controlActive;
        _group = group;
    }

    public DeviceSnapshot Source => _source;
    public string Id => _source.Id;
    public string DisplayName => _source.DisplayName;
    public string DeviceType => _source.DeviceType;
    public string Ipv4Address => _source.Ipv4Address;
    public string MacAddress => _source.MacAddress;
    public bool IsOnline => _source.IsOnline;
    public bool IsGateway => _source.IsGateway;
    public bool IsLocalComputer => _source.IsLocalComputer;
    public bool IsProtected => _source.IsGateway || _source.IsLocalComputer;
    public bool CanEditPolicy => !IsProtected;
    public bool IsBlockPending => _isBlockPending;
    public bool CanToggleBlock => CanEditPolicy && !_isBlockPending;
    public string DeviceBadge => _source.IsGateway ? "Gateway" : _source.IsLocalComputer ? "This PC" : _source.DeviceType;
    public bool BlockInternet => _source.Policy.BlockInternet;
    public long DownloadBitsPerSecond => _source.DownloadBitsPerSecond;
    public long UploadBitsPerSecond => _source.UploadBitsPerSecond;
    public long TotalDownloadBytes => _source.TotalDownloadBytes;
    public long TotalUploadBytes => _source.TotalUploadBytes;
    public string DownloadRate => _formatter(_source.DownloadBitsPerSecond);
    public string UploadRate => _formatter(_source.UploadBitsPerSecond);
    public string DownloadedData => IsGateway ? "—" : FormatDataSize(_source.TotalDownloadBytes);
    public string UploadedData => IsGateway ? "—" : FormatDataSize(_source.TotalUploadBytes);
    public RateTrend DownloadTrend => _downloadTrend;
    public RateTrend UploadTrend => _uploadTrend;
    public bool IsTrafficMonitored => IsLocalComputer || (_controlActive && !IsGateway);
    public string DownloadDisplay => IsGateway ? "—" : IsTrafficMonitored ? DownloadRate : "Not monitored";
    public string UploadDisplay => IsGateway ? "—" : IsTrafficMonitored ? UploadRate : "Not monitored";
    public string ActionText => "Edit";
    public string MonitoringText => IsGateway
        ? "Protected gateway"
        : IsLocalComputer
            ? "Local traffic"
            : IsTrafficMonitored
                ? "Live monitoring"
                : "Start control to monitor";
    public string StatusText => _source.IsOnline ? "Online" : "Offline";
    public string GroupName => _group?.Name ?? "Unassigned";
    public bool HasDeviceOverrides =>
        _group is not null &&
        (_source.Policy.BlockInternet ||
         _source.Policy.DownloadLimitBitsPerSecond is not null ||
         _source.Policy.UploadLimitBitsPerSecond is not null ||
         _source.Policy.Priority != DevicePriority.Normal);
    public string LimitsText =>
        EffectiveDownloadLimit is null && EffectiveUploadLimit is null
            ? "Unlimited"
            : $"↓ {FormatLimit(EffectiveDownloadLimit)}  ↑ {FormatLimit(EffectiveUploadLimit)}";

    private long? EffectiveDownloadLimit =>
        _source.Policy.DownloadLimitBitsPerSecond ?? _group?.DownloadLimitBitsPerSecond;
    private long? EffectiveUploadLimit =>
        _source.Policy.UploadLimitBitsPerSecond ?? _group?.UploadLimitBitsPerSecond;

    public void Apply(DeviceSnapshot source, Func<long, string> formatter, bool controlActive, GroupPolicy? group)
    {
        DeviceSnapshot previous = _source;
        GroupPolicy? previousGroup = _group;
        bool previousControlActive = _controlActive;
        bool downloadChanged = previous.DownloadBitsPerSecond != source.DownloadBitsPerSecond;
        bool uploadChanged = previous.UploadBitsPerSecond != source.UploadBitsPerSecond;
        bool usageChanged = previous.TotalDownloadBytes != source.TotalDownloadBytes ||
                            previous.TotalUploadBytes != source.TotalUploadBytes;
        bool onlineChanged = previous.IsOnline != source.IsOnline;
        bool identityChanged = previous.DisplayName != source.DisplayName ||
                               previous.DeviceType != source.DeviceType ||
                               previous.Ipv4Address != source.Ipv4Address ||
                               previous.MacAddress != source.MacAddress ||
                               previous.IsGateway != source.IsGateway ||
                               previous.IsLocalComputer != source.IsLocalComputer;
        bool policyChanged = previous.Policy != source.Policy;
        bool groupChanged = previousGroup != group;
        bool controlChanged = previousControlActive != controlActive;

        _source = source;
        _formatter = formatter;
        _controlActive = controlActive;
        _group = group;

        if (downloadChanged)
        {
            _downloadTrend = CalculateTrend(previous.DownloadBitsPerSecond, source.DownloadBitsPerSecond);
            OnPropertyChanged(nameof(DownloadBitsPerSecond));
            OnPropertyChanged(nameof(DownloadRate));
            OnPropertyChanged(nameof(DownloadDisplay));
            OnPropertyChanged(nameof(DownloadTrend));
        }
        if (uploadChanged)
        {
            _uploadTrend = CalculateTrend(previous.UploadBitsPerSecond, source.UploadBitsPerSecond);
            OnPropertyChanged(nameof(UploadBitsPerSecond));
            OnPropertyChanged(nameof(UploadRate));
            OnPropertyChanged(nameof(UploadDisplay));
            OnPropertyChanged(nameof(UploadTrend));
        }
        if (usageChanged)
        {
            OnPropertyChanged(nameof(TotalDownloadBytes));
            OnPropertyChanged(nameof(TotalUploadBytes));
            OnPropertyChanged(nameof(DownloadedData));
            OnPropertyChanged(nameof(UploadedData));
        }
        if (onlineChanged)
        {
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(StatusText));
        }
        if (identityChanged)
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DeviceType));
            OnPropertyChanged(nameof(Ipv4Address));
            OnPropertyChanged(nameof(MacAddress));
            OnPropertyChanged(nameof(IsGateway));
            OnPropertyChanged(nameof(IsLocalComputer));
            OnPropertyChanged(nameof(IsProtected));
            OnPropertyChanged(nameof(CanEditPolicy));
            OnPropertyChanged(nameof(CanToggleBlock));
            OnPropertyChanged(nameof(DeviceBadge));
            OnPropertyChanged(nameof(DownloadedData));
            OnPropertyChanged(nameof(UploadedData));
        }
        if (policyChanged)
        {
            OnPropertyChanged(nameof(BlockInternet));
            OnPropertyChanged(nameof(HasDeviceOverrides));
            OnPropertyChanged(nameof(LimitsText));
        }
        if (groupChanged)
        {
            OnPropertyChanged(nameof(GroupName));
            OnPropertyChanged(nameof(HasDeviceOverrides));
            OnPropertyChanged(nameof(LimitsText));
        }
        if (controlChanged || identityChanged)
        {
            OnPropertyChanged(nameof(IsTrafficMonitored));
            OnPropertyChanged(nameof(DownloadDisplay));
            OnPropertyChanged(nameof(UploadDisplay));
            OnPropertyChanged(nameof(MonitoringText));
        }

        OnPropertyChanged(nameof(Source));
    }

    public void SetBlockPending(bool pending)
    {
        if (_isBlockPending == pending) return;
        _isBlockPending = pending;
        OnPropertyChanged(nameof(IsBlockPending));
        OnPropertyChanged(nameof(CanToggleBlock));
    }

    private static RateTrend CalculateTrend(long previous, long current)
    {
        long threshold = Math.Max(5_000, previous / 20);
        long difference = current - previous;
        if (Math.Abs(difference) < threshold) return RateTrend.Steady;
        return difference > 0 ? RateTrend.Increasing : RateTrend.Decreasing;
    }

    private static string FormatLimit(long? value) =>
        value is null ? "∞" : $"{value.Value / 1_000_000d:0.##}M";

    private static string FormatDataSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1000d && unit < units.Length - 1)
        {
            value /= 1000d;
            unit++;
        }

        string format = value >= 100d || unit == 0 ? "0" : value >= 10d ? "0.0" : "0.00";
        return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
