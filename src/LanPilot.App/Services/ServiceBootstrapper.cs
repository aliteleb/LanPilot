using System.Diagnostics;
using System.ComponentModel;
using System.IO;

namespace LanPilot.App.Services;

public static class ServiceBootstrapper
{
    private static readonly object LaunchGate = new();
    private static DateTimeOffset _nextLaunchAttempt = DateTimeOffset.MinValue;

    public static async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        Process[] services = Process.GetProcessesByName("LanPilot.Service");
        bool alreadyRunning = services.Length > 0;
        foreach (Process service in services) service.Dispose();
        if (alreadyRunning)
        {
            return;
        }

        using var serviceKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanPilotService");
        bool installedService = serviceKey is not null;
        string? candidate = installedService ? Path.Combine(Environment.SystemDirectory, "sc.exe") : new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Service", "LanPilot.Service.exe")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "Service", "LanPilot.Service.exe"))
        }.FirstOrDefault(File.Exists);
        if (candidate is null)
        {
            return;
        }

        lock (LaunchGate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now < _nextLaunchAttempt)
            {
                return;
            }

            // Avoid repeated elevation prompts if startup is cancelled or the
            // native packet driver fails immediately after launch.
            _nextLaunchAttempt = now.AddSeconds(30);
        }

        try
        {
            Process.Start(new ProcessStartInfo(candidate)
            {
                Arguments = installedService ? "start LanPilotService" : "",
                WorkingDirectory = Path.GetDirectoryName(candidate)!,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            })?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user cancelled the elevation prompt. The UI will remain usable
            // and show the service as offline until they reconnect.
            return;
        }

        // Give the host enough time to initialize its database and pipe before
        // the normal connection timeout starts.
        await Task.Delay(1200, cancellationToken);
    }
}
