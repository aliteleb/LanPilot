using Microsoft.Win32;

namespace LanPilot.App.Services;

public static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LanPilot";

    public static bool IsEnabled()
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, false)
            ?? throw new InvalidOperationException("The Windows startup registry key is unavailable.");
        return key.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --tray", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
