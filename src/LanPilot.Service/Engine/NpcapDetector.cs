using Microsoft.Win32;

namespace LanPilot.Service.Engine;

public static class NpcapDetector
{
    public static (bool Available, string? Version) Detect()
    {
        string systemPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "Npcap", "wpcap.dll");

        bool available = File.Exists(systemPath) ||
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Npcap") is not null;

        string? version = null;
        using RegistryKey? uninstall = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst");
        version = uninstall?.GetValue("DisplayVersion") as string;

        if (version is null)
        {
            foreach (string viewPath in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                     })
            {
                using RegistryKey? root = Registry.LocalMachine.OpenSubKey(viewPath);
                if (root is null) continue;
                foreach (string name in root.GetSubKeyNames())
                {
                    using RegistryKey? key = root.OpenSubKey(name);
                    if (key is not null && string.Equals(key.GetValue("DisplayName") as string, "Npcap", StringComparison.OrdinalIgnoreCase))
                    {
                        version = key.GetValue("DisplayVersion") as string;
                        break;
                    }
                }

                if (version is not null) break;
            }
        }

        return (available, version);
    }
}
