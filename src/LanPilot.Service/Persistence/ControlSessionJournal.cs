using System.Text.Json;
using LanPilot.Contracts;

namespace LanPilot.Service.Persistence;

public sealed record ControlSession(
    NetworkAdapterInfo Adapter,
    NetworkProfile Network,
    IReadOnlyList<DeviceSnapshot> Targets,
    DateTimeOffset StartedAt);

public sealed class ControlSessionJournal
{
    private readonly string _path;

    public ControlSessionJournal() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanPilot")) { }

    public ControlSessionJournal(string root)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "active-control-session.json");
    }

    public async Task SaveAsync(ControlSession session, CancellationToken cancellationToken)
    {
        string temporary = _path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(session, PipeProtocol.JsonOptions),
            cancellationToken);
        File.Move(temporary, _path, true);
    }

    public async Task<ControlSession?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;
        string json = await File.ReadAllTextAsync(_path, cancellationToken);
        return JsonSerializer.Deserialize<ControlSession>(json, PipeProtocol.JsonOptions);
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
