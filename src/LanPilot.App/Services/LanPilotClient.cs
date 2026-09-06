using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using LanPilot.Contracts;

namespace LanPilot.App.Services;

public sealed class LanPilotClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PipeEnvelope>> _pending = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readerCancellation;

    public event EventHandler<DashboardSnapshot>? SnapshotReceived;
    public event EventHandler<NotificationEvent>? NotificationReceived;
    public event EventHandler<bool>? ConnectionChanged;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return;
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected) return;
            NamedPipeClientStream? stalePipe = _pipe;
            CancellationTokenSource? staleReader = _readerCancellation;
            _pipe = null;
            _readerCancellation = null;
            staleReader?.Cancel();
            if (stalePipe is not null) await stalePipe.DisposeAsync();
            staleReader?.Dispose();

            NamedPipeClientStream pipe = new(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(3000, cancellationToken); }
            catch { pipe.Dispose(); throw; }
            _pipe = pipe;
            // The reader belongs to the connection lifetime, not to the short
            // timeout used by the call that established this connection.
            _readerCancellation = new CancellationTokenSource();
            _ = ReadLoopAsync(pipe, _readerCancellation.Token);
            ConnectionChanged?.Invoke(this, true);
            await SendAsync<OperationResult>(PipeCommands.Subscribe, new { }, cancellationToken);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        SendAsync<DashboardSnapshot>(PipeCommands.SnapshotGet, new { }, cancellationToken);

    public Task<OperationResult> ScanAsync(string? adapterId, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.ScanStart, new ScanRequest(adapterId), cancellationToken);

    public Task<OperationResult> SetControlAsync(bool enabled, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.ControlSet, new ControlRequest(enabled), cancellationToken);

    public Task<OperationResult> EmergencyPauseAsync(CancellationToken cancellationToken) =>
        SendEmergencyAsync(PipeCommands.EmergencyPause, cancellationToken);

    public Task<OperationResult> ExitControlAsync(CancellationToken token) => SendEmergencyAsync(PipeCommands.ControlExit, token);
    public Task<OperationResult> OpenUiAsync(CancellationToken token) => SendAsync<OperationResult>(PipeCommands.ControlOpen, new { }, token);

    private static async Task<OperationResult> SendEmergencyAsync(string command, CancellationToken token)
    {
        await using NamedPipeClientStream pipe = new(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await pipe.ConnectAsync(timeout.Token);
        PipeEnvelope request = PipeProtocol.Request(command, new { });
        await PipeProtocol.WriteAsync(pipe, request, timeout.Token);
        while (await PipeProtocol.ReadAsync(pipe, timeout.Token) is PipeEnvelope response)
        {
            if (response.RequestId != request.RequestId) continue;
            if (!string.IsNullOrWhiteSpace(response.Error)) throw new IOException(response.Error);
            return response.ReadPayload<OperationResult>();
        }
        throw new IOException("Service disconnected before confirming restoration.");
    }

    public Task<OperationResult> UpdatePolicyAsync(DevicePolicy policy, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.DevicePolicySet, policy, cancellationToken);

    public Task<OperationResult> RenameDeviceAsync(RenameDeviceRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.DeviceRename, request, cancellationToken);

    public Task<OperationResult> ResetDeviceAsync(ResetDeviceRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.DeviceReset, request, cancellationToken);

    public Task<OperationResult> SaveGroupAsync(GroupPolicy group, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.GroupSave, group, cancellationToken);

    public Task<OperationResult> DeleteGroupAsync(string groupId, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.GroupDelete, new DeleteEntityRequest(groupId), cancellationToken);

    public Task<OperationResult> SaveScheduleAsync(ScheduleRule schedule, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.ScheduleSave, schedule, cancellationToken);

    public Task<OperationResult> DeleteScheduleAsync(string scheduleId, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.ScheduleDelete, new DeleteEntityRequest(scheduleId), cancellationToken);

    public Task<OperationResult> SavePresetAsync(SavePresetRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.PresetSave, request, cancellationToken);

    public Task<OperationResult> ApplyPresetAsync(ApplyPresetRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.PresetApply, request, cancellationToken);

    public Task<OperationResult> DeletePresetAsync(string presetId, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.PresetDelete, new DeleteEntityRequest(presetId), cancellationToken);

    public Task<IReadOnlyList<LocalApplicationSnapshot>> GetApplicationsAsync(CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<LocalApplicationSnapshot>>(PipeCommands.ApplicationsGet, new { }, cancellationToken);

    public Task<OperationResult> SaveApplicationPolicyAsync(LocalApplicationPolicy policy, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.ApplicationPolicySet, policy, cancellationToken);

    public Task<OperationResult> DeleteApplicationPolicyAsync(string policyId, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.ApplicationPolicyDelete, new DeleteEntityRequest(policyId), cancellationToken);

    public Task<OperationResult> UpdateNetworkSettingsAsync(UpdateNetworkSettingsRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.NetworkSettingsUpdate, request, cancellationToken);

    public Task<OperationResult> UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.SettingsUpdate, settings, cancellationToken);

    public Task<OperationResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.BackupExport, request, cancellationToken);

    public Task<OperationResult> ImportAsync(ImportRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.BackupImport, request, cancellationToken);

    public Task<OperationResult> ExportDiagnosticsAsync(ExportRequest request, CancellationToken cancellationToken) =>
        SendAsync<OperationResult>(PipeCommands.DiagnosticsExport, request, cancellationToken);

    private async Task<T> SendAsync<T>(string command, object payload, CancellationToken cancellationToken)
    {
        if (!IsConnected) await ConnectAsync(cancellationToken);
        NamedPipeClientStream pipe = _pipe ?? throw new IOException("LanPilot Service is not connected.");
        PipeEnvelope request = PipeProtocol.Request(command, payload);
        TaskCompletionSource<PipeEnvelope> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId!] = completion;

        try
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await PipeProtocol.WriteAsync(pipe, request, cancellationToken);
            }
            catch
            {
                _pending.TryRemove(request.RequestId!, out _);
                InvalidateConnection(pipe);
                throw;
            }
            finally
            {
                _writeGate.Release();
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            PipeEnvelope response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (!string.IsNullOrWhiteSpace(response.Error)) throw new IOException(response.Error);
            return response.ReadPayload<T>();
        }
        finally { _pending.TryRemove(request.RequestId!, out _); }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                PipeEnvelope? envelope = await PipeProtocol.ReadAsync(pipe, cancellationToken);
                if (envelope is null) break;
                if (envelope.Kind == PipeMessageKind.Response && envelope.RequestId is not null &&
                    _pending.TryRemove(envelope.RequestId, out TaskCompletionSource<PipeEnvelope>? completion))
                {
                    completion.TrySetResult(envelope);
                }
                else if (envelope.Kind == PipeMessageKind.Event)
                {
                    if (envelope.Name == PipeEvents.SnapshotChanged)
                        SnapshotReceived?.Invoke(this, envelope.ReadPayload<DashboardSnapshot>());
                    else if (envelope.Name == PipeEvents.Notification)
                        NotificationReceived?.Invoke(this, envelope.ReadPayload<NotificationEvent>());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _pipe, null, pipe), pipe))
            {
                ConnectionChanged?.Invoke(this, false);
                FailPending(new IOException("LanPilot Service disconnected."));
            }
            await pipe.DisposeAsync();
        }
    }

    private void InvalidateConnection(NamedPipeClientStream pipe)
    {
        pipe.Dispose();
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _pipe, null, pipe), pipe)) return;
        _readerCancellation?.Cancel();
        ConnectionChanged?.Invoke(this, false);
        FailPending(new IOException("LanPilot Service disconnected."));
    }

    private void FailPending(Exception exception)
    {
        foreach ((string id, TaskCompletionSource<PipeEnvelope> completion) in _pending.ToArray())
        {
            if (_pending.TryRemove(id, out _)) completion.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _readerCancellation?.Cancel();
        if (_pipe is not null) await _pipe.DisposeAsync();
        _readerCancellation?.Dispose();
        _connectGate.Dispose();
        _writeGate.Dispose();
    }
}
