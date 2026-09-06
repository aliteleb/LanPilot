using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using LanPilot.Contracts;
using LanPilot.Service.Diagnostics;

namespace LanPilot.Service.Ipc;

public sealed class PipeServer(
    LanPilotCoordinator coordinator,
    DiagnosticRecorder diagnostics,
    ILogger<PipeServer> logger) : BackgroundService
{
    private static readonly HashSet<string> KnownCommands = typeof(PipeCommands).GetFields()
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        coordinator.SnapshotChanged += OnSnapshotChanged;
        coordinator.NotificationRaised += OnNotificationRaised;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = CreatePipe();
                try { await pipe.WaitForConnectionAsync(stoppingToken); }
                catch { pipe.Dispose(); throw; }
                _ = HandleClientAsync(pipe, stoppingToken);
            }
        }
        finally
        {
            coordinator.SnapshotChanged -= OnSnapshotChanged;
            coordinator.NotificationRaised -= OnNotificationRaised;
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        PipeSecurity security = new();
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The service identity is unavailable.");
        security.SetOwner(owner);
        security.AddAccessRule(new PipeAccessRule(
            owner,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
                    PipeProtocol.PipeName,
                    PipeDirection.InOut,
                    4,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                    64 * 1024,
                    64 * 1024,
                    security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        Guid id = Guid.NewGuid();
        ClientConnection client = new(pipe);
        _clients[id] = client;
        diagnostics.Record("LanPilot.Pipe", "Information", "Client connected");
        try
        {
            while (pipe.IsConnected && !serverToken.IsCancellationRequested)
            {
                PipeEnvelope? request = await PipeProtocol.ReadAsync(pipe, serverToken);
                if (request is null) break;
                if (request.Name == PipeCommands.Subscribe) client.Subscribed = true;
                PipeEnvelope response = await DispatchAsync(request, serverToken);
                await client.WriteAsync(response, serverToken);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
            logger.LogDebug(ex, "LanPilot client disconnected.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LanPilot pipe client failed.");
        }
        finally
        {
            _clients.TryRemove(id, out _);
            diagnostics.Record("LanPilot.Pipe", "Information", "Client disconnected");
            await client.DisposeAsync();
        }
    }

    private async Task<PipeEnvelope> DispatchAsync(PipeEnvelope request, CancellationToken cancellationToken)
    {
        if (request.Kind != PipeMessageKind.Request || request.Version != PipeProtocol.Version)
            return PipeProtocol.Error(request, "Unsupported pipe protocol.");

        string command = KnownCommands.Contains(request.Name) ? request.Name : "unknown";
        bool trace = command is not (PipeCommands.SnapshotGet or PipeCommands.ApplicationsGet or PipeCommands.Subscribe);
        long started = Environment.TickCount64;
        if (trace) diagnostics.Record("LanPilot.Command", "Information", $"Started: {command}");
        try
        {
            object result = request.Name switch
            {
                PipeCommands.ControlExit => await coordinator.SuspendAllAsync("UserExit", cancellationToken),
                PipeCommands.ControlOpen => await coordinator.OpenUiAsync(cancellationToken),
                PipeCommands.SnapshotGet => coordinator.GetSnapshot(),
                PipeCommands.ScanStart => await coordinator.ScanAsync(request.ReadPayload<ScanRequest>().AdapterId, cancellationToken),
                PipeCommands.ControlSet => await coordinator.SetControlAsync(request.ReadPayload<ControlRequest>().Enabled, cancellationToken),
                PipeCommands.EmergencyPause => await PauseAsync(cancellationToken),
                PipeCommands.DevicePolicySet => await coordinator.UpdatePolicyAsync(request.ReadPayload<DevicePolicy>(), cancellationToken),
                PipeCommands.DeviceRename => await coordinator.RenameDeviceAsync(request.ReadPayload<RenameDeviceRequest>(), cancellationToken),
                PipeCommands.DeviceReset => await coordinator.ResetDeviceAsync(request.ReadPayload<ResetDeviceRequest>(), cancellationToken),
                PipeCommands.GroupSave => await coordinator.SaveGroupAsync(request.ReadPayload<GroupPolicy>(), cancellationToken),
                PipeCommands.GroupDelete => await coordinator.DeleteGroupAsync(request.ReadPayload<DeleteEntityRequest>().Id, cancellationToken),
                PipeCommands.ScheduleSave => await coordinator.SaveScheduleAsync(request.ReadPayload<ScheduleRule>(), cancellationToken),
                PipeCommands.ScheduleDelete => await coordinator.DeleteScheduleAsync(request.ReadPayload<DeleteEntityRequest>().Id, cancellationToken),
                PipeCommands.PresetSave => await coordinator.SavePresetAsync(request.ReadPayload<SavePresetRequest>(), cancellationToken),
                PipeCommands.PresetApply => await coordinator.ApplyPresetAsync(request.ReadPayload<ApplyPresetRequest>(), cancellationToken),
                PipeCommands.PresetDelete => await coordinator.DeletePresetAsync(request.ReadPayload<DeleteEntityRequest>().Id, cancellationToken),
                PipeCommands.ApplicationsGet => await coordinator.GetApplicationsAsync(cancellationToken),
                PipeCommands.ApplicationPolicySet => await coordinator.SaveApplicationPolicyAsync(request.ReadPayload<LocalApplicationPolicy>(), cancellationToken),
                PipeCommands.ApplicationPolicyDelete => await coordinator.DeleteApplicationPolicyAsync(request.ReadPayload<DeleteEntityRequest>().Id, cancellationToken),
                PipeCommands.NetworkSettingsUpdate => await coordinator.UpdateNetworkSettingsAsync(request.ReadPayload<UpdateNetworkSettingsRequest>(), cancellationToken),
                PipeCommands.SettingsUpdate => await coordinator.UpdateSettingsAsync(request.ReadPayload<AppSettings>(), cancellationToken),
                PipeCommands.BackupExport => await coordinator.ExportAsync(request.ReadPayload<ExportRequest>(), cancellationToken),
                PipeCommands.BackupImport => await coordinator.ImportAsync(request.ReadPayload<ImportRequest>(), cancellationToken),
                PipeCommands.DiagnosticsExport => await coordinator.ExportDiagnosticsAsync(request.ReadPayload<ExportRequest>(), cancellationToken),
                PipeCommands.Subscribe => new OperationResult(true, "Subscribed."),
                _ => new OperationResult(false, $"Unknown command: {request.Name}")
            };
            if (result is OperationResult { Success: false })
                diagnostics.Record("LanPilot.Command", "Warning", $"Rejected: {command}");
            else if (trace)
                diagnostics.Record("LanPilot.Command", "Information", $"Completed: {command}; elapsedMs={Environment.TickCount64 - started}");
            return PipeProtocol.Response(request, result);
        }
        catch (Exception ex)
        {
            diagnostics.Record("LanPilot.Command", "Warning", $"Failed: {command}", ex);
            logger.LogWarning(ex, "Pipe command {Command} failed.", request.Name);
            return PipeProtocol.Error(request, ex.Message);
        }
    }

    private async Task<OperationResult> PauseAsync(CancellationToken cancellationToken)
    {
        await coordinator.EmergencyPauseAsync(cancellationToken);
        return new OperationResult(true, "Emergency pause completed.");
    }

    private void OnSnapshotChanged(object? sender, EventArgs e) =>
        Broadcast(PipeProtocol.Event(PipeEvents.SnapshotChanged, coordinator.GetSnapshot()));

    private void OnNotificationRaised(object? sender, NotificationEvent e) =>
        Broadcast(PipeProtocol.Event(PipeEvents.Notification, e));

    private void Broadcast(PipeEnvelope envelope)
    {
        foreach (ClientConnection client in _clients.Values)
        {
            client.QueueEvent(envelope);
        }
    }

    private sealed class ClientConnection : IAsyncDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        private readonly Channel<PipeEnvelope> _events = Channel.CreateBounded<PipeEnvelope>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
        });
        private readonly Task _eventWriter;
        public bool Subscribed { get; set; }

        public ClientConnection(NamedPipeServerStream stream)
        {
            pipe = stream;
            _eventWriter = PumpEventsAsync();
        }

        public void QueueEvent(PipeEnvelope envelope)
        {
            if (Subscribed) _events.Writer.TryWrite(envelope);
        }

        public async Task WriteAsync(PipeEnvelope envelope, CancellationToken cancellationToken)
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await _writeGate.WaitAsync(timeout.Token);
            try
            {
                await PipeProtocol.WriteAsync(pipe, envelope, timeout.Token);
            }
            catch
            {
                // A cancelled write can have emitted part of a frame. Never reuse it.
                pipe.Dispose();
                throw;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task PumpEventsAsync()
        {
            try
            {
                await foreach (PipeEnvelope envelope in _events.Reader.ReadAllAsync(_closed.Token))
                    await WriteAsync(envelope, _closed.Token);
            }
            catch
            {
                pipe.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            await _closed.CancelAsync();
            try { await pipe.DisposeAsync(); } catch { }
            await _eventWriter;
            _writeGate.Dispose();
            _closed.Dispose();
        }
    }
}
