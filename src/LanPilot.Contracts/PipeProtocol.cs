using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanPilot.Contracts;

public static class PipeProtocol
{
    public const string PipeName = "LanPilot.Control.v1";
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async ValueTask WriteAsync(Stream stream, PipeEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (body.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("The pipe message exceeds the 1 MB limit.");
        }

        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<PipeEnvelope?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(int)];
        int first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException("The pipe message length is invalid.");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PipeEnvelope>(body, JsonOptions)
            ?? throw new InvalidDataException("The pipe message is empty.");
    }

    public static PipeEnvelope Request<T>(string name, T payload) =>
        new(Version, PipeMessageKind.Request, Guid.NewGuid().ToString("N"), name,
            JsonSerializer.SerializeToElement(payload, JsonOptions), null);

    public static PipeEnvelope Response<T>(PipeEnvelope request, T payload) =>
        new(Version, PipeMessageKind.Response, request.RequestId, request.Name,
            JsonSerializer.SerializeToElement(payload, JsonOptions), null);

    public static PipeEnvelope Error(PipeEnvelope request, string message) =>
        new(Version, PipeMessageKind.Response, request.RequestId, request.Name, null, message);

    public static PipeEnvelope Event<T>(string name, T payload) =>
        new(Version, PipeMessageKind.Event, null, name,
            JsonSerializer.SerializeToElement(payload, JsonOptions), null);
}

public enum PipeMessageKind
{
    Request,
    Response,
    Event
}

public sealed record PipeEnvelope(
    int Version,
    PipeMessageKind Kind,
    string? RequestId,
    string Name,
    JsonElement? Payload,
    string? Error)
{
    public T ReadPayload<T>()
    {
        if (!Payload.HasValue)
        {
            throw new InvalidDataException("The message payload is missing.");
        }

        T? value = Payload.Value.Deserialize<T>(PipeProtocol.JsonOptions);
        return value is null
            ? throw new InvalidDataException("The message payload is invalid.")
            : value;
    }
}

public static class PipeCommands
{
    public const string ControlExit = "control.exit";
    public const string ControlOpen = "control.open";
    public const string SnapshotGet = "snapshot.get";
    public const string ScanStart = "scan.start";
    public const string ControlSet = "control.set";
    public const string EmergencyPause = "control.emergency-pause";
    public const string DevicePolicySet = "device.policy.set";
    public const string DeviceRename = "device.rename";
    public const string DeviceReset = "device.reset";
    public const string GroupSave = "group.save";
    public const string GroupDelete = "group.delete";
    public const string ScheduleSave = "schedule.save";
    public const string ScheduleDelete = "schedule.delete";
    public const string PresetSave = "preset.save";
    public const string PresetApply = "preset.apply";
    public const string PresetDelete = "preset.delete";
    public const string ApplicationsGet = "applications.get";
    public const string ApplicationPolicySet = "application.policy.set";
    public const string ApplicationPolicyDelete = "application.policy.delete";
    public const string NetworkSettingsUpdate = "network.settings.update";
    public const string SettingsUpdate = "settings.update";
    public const string BackupExport = "backup.export";
    public const string BackupImport = "backup.import";
    public const string DiagnosticsExport = "diagnostics.export";
    public const string Subscribe = "events.subscribe";
}

public static class PipeEvents
{
    public const string SnapshotChanged = "snapshot.changed";
    public const string StatusChanged = "status.changed";
    public const string Notification = "notification";
}

public sealed record NotificationEvent(string Title, string Message, NotificationSeverity Severity);

public enum NotificationSeverity
{
    Info,
    Success,
    Warning,
    Error
}
