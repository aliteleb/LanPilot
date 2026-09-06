using System.Text.Json;
using LanPilot.Contracts;

namespace LanPilot.Service.Persistence;

public sealed class ControlSafetyJournal
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ControlSafetyJournal() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LanPilot")) { }
    public ControlSafetyJournal(string directory) => _path = Path.Combine(directory, "control-safety.json");

    public async Task<ControlSafetyStatus?> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return null;
        return JsonSerializer.Deserialize<ControlSafetyStatus>(await File.ReadAllTextAsync(_path, token), PipeProtocol.JsonOptions);
    }

    public async Task SaveAsync(ControlSafetyStatus state, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllTextAsync(_path + ".tmp", JsonSerializer.Serialize(state, PipeProtocol.JsonOptions), token);
            File.Move(_path + ".tmp", _path, true);
        }
        finally { _gate.Release(); }
    }
}
