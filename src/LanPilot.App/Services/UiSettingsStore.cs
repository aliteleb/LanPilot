using System.IO;
using System.Text.Json;

namespace LanPilot.App.Services;

public sealed record UiSettings(bool FirstRunComplete, string Theme, bool RunAtLogin);

public sealed class UiSettingsStore
{
    private readonly string _path;

    public UiSettingsStore()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanPilot");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "ui-settings.json");
    }

    public UiSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_path)) ?? Default
                : Default;
        }
        catch
        {
            return Default;
        }
    }

    public void Save(UiSettings settings)
    {
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
        File.Move(temporary, _path, true);
    }

    private static UiSettings Default => new(false, "Dark", true);
}
