using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace LanPilot.Service.Engine;

public static class NpcapDetector
{
    public static (bool Available, string? Version) Detect()
    {
        bool libraryAvailable = new[]
        {
            Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"),
            Path.Combine(Environment.SystemDirectory, "wpcap.dll")
        }.Any(CanLoadLibrary);

        bool driverRegistered = RegistryKeyExists(
            RegistryView.Registry64,
            @"SYSTEM\CurrentControlSet\Services\npcap") ||
            RegistryKeyExists(
                RegistryView.Registry32,
                @"SYSTEM\CurrentControlSet\Services\npcap");

        string? version = FindInstalledVersion();
        if (version is null)
        {
            string installerUtility = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Npcap",
                "NPFInstall.exe");
            if (File.Exists(installerUtility))
            {
                version = System.Diagnostics.FileVersionInfo
                    .GetVersionInfo(installerUtility)
                    .ProductVersion;
            }
        }

        return (libraryAvailable && driverRegistered, version);
    }

    private static bool CanLoadLibrary(string path)
    {
        if (!File.Exists(path)) return false;
        // Check process architecture and dependencies using trusted system directories only.
        IntPtr handle = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00000800);
        if (handle == IntPtr.Zero) return false;
        try { return NativeLibrary.TryGetExport(handle, "pcap_findalldevs", out _); }
        finally { NativeLibrary.Free(handle); }
    }

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);

    private static string? FindInstalledVersion()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? direct = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst");
                if (direct?.GetValue("DisplayVersion") is string directVersion &&
                    !string.IsNullOrWhiteSpace(directVersion))
                    return directVersion;

                using RegistryKey? root = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (string name in root.GetSubKeyNames())
                {
                    using RegistryKey? key = root.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string displayName ||
                        !displayName.StartsWith("Npcap", StringComparison.OrdinalIgnoreCase)) continue;
                    if (key.GetValue("DisplayVersion") is string discoveredVersion &&
                        !string.IsNullOrWhiteSpace(discoveredVersion))
                        return discoveredVersion;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // File and service-key detection can still identify the driver.
            }
        }

        return null;
    }

    private static bool RegistryKeyExists(RegistryView view, string path)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? key = baseKey.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
