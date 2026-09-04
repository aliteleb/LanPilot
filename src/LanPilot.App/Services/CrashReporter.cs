using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace LanPilot.App.Services;

public static class CrashReporter
{
    private const int RetainedSessionLogs = 20;
    private static readonly object FileGate = new();
    private static string? _sessionLogPath;
    private static bool _initialized;

    public static string DiagnosticsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LanPilot",
        "Diagnostics");

    public static string DumpsDirectory => Path.Combine(DiagnosticsDirectory, "Dumps");
    public static string? SessionLogPath => _sessionLogPath;

    public static void Initialize(System.Windows.Application application)
    {
        lock (FileGate)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Directory.CreateDirectory(DiagnosticsDirectory);
                _sessionLogPath = Path.Combine(
                    DiagnosticsDirectory,
                    $"app-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
                PruneOldSessionLogs();
            }
            catch
            {
                _sessionLogPath = null;
            }
        }

        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        if (string.Equals(
                Environment.GetEnvironmentVariable("LANPILOT_TRACE_FIRST_CHANCE"),
                "1",
                StringComparison.Ordinal))
        {
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        }

        Record(
            "Application started",
            $"Version: {typeof(CrashReporter).Assembly.GetName().Version}\n" +
            $"Process: {Environment.ProcessId}\n" +
            $"Runtime: {RuntimeInformation.FrameworkDescription}\n" +
            $"OS: {RuntimeInformation.OSDescription}\n" +
            $"Architecture: {RuntimeInformation.ProcessArchitecture}\n" +
            $"Command line: {Environment.CommandLine}");
    }

    public static void RecordException(string source, Exception exception, bool fatal = false)
    {
        string severity = fatal ? "FATAL" : "ERROR";
        Record($"{severity}: {source}", exception.ToString());
    }

    public static void Record(string source, string details)
    {
        string? logPath = _sessionLogPath;
        if (logPath is null) return;

        string entry =
            $"[{DateTimeOffset.Now:O}] [{source}] [Thread {Environment.CurrentManagedThreadId}]\n" +
            details +
            $"\n{new string('-', 100)}\n";

        lock (FileGate)
        {
            try
            {
                File.AppendAllText(logPath, entry, Encoding.UTF8);
                if (source.StartsWith("FATAL:", StringComparison.Ordinal))
                {
                    File.WriteAllText(
                        Path.Combine(DiagnosticsDirectory, "latest-crash.txt"),
                        entry,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never become another reason for the app to fail.
            }
        }
    }

    public static void OpenDiagnosticsDirectory()
    {
        Directory.CreateDirectory(DiagnosticsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = DiagnosticsDirectory,
            UseShellExecute = true
        });
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        RecordException("WPF dispatcher unhandled exception", e.Exception, fatal: true);
        e.Handled = false;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            RecordException("AppDomain unhandled exception", exception, fatal: e.IsTerminating);
        }
        else
        {
            Record(
                e.IsTerminating ? "FATAL: AppDomain unhandled exception" : "ERROR: AppDomain unhandled exception",
                e.ExceptionObject?.ToString() ?? "Unknown exception object.");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        RecordException("Unobserved background task exception", e.Exception);

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e) =>
        RecordException("First-chance exception", e.Exception);

    private static void PruneOldSessionLogs()
    {
        try
        {
            foreach (FileInfo file in new DirectoryInfo(DiagnosticsDirectory)
                         .EnumerateFiles("app-*.log")
                         .OrderByDescending(file => file.CreationTimeUtc)
                         .Skip(RetainedSessionLogs))
            {
                file.Delete();
            }
        }
        catch
        {
            // Retention cleanup is best effort.
        }
    }
}
