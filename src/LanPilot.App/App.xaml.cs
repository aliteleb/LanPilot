using System.Threading;
using System.Windows;
using LanPilot.App.Services;

namespace LanPilot.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\LanPilot.App.SingleInstance";
    private const string ShowEventName = @"Local\LanPilot.App.Show";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _showListenerCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showListenerCancellation = new CancellationTokenSource();

        UiSettingsStore uiSettings = new();
        LanPilotClient client = new();
        MainWindow window = new(client, uiSettings);
        MainWindow = window;

        bool requestedTray = e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!requestedTray || !uiSettings.Load().FirstRunComplete)
        {
            window.Show();
        }

        _ = ListenForShowRequestAsync(window, _showListenerCancellation.Token);
    }

    private async Task ListenForShowRequestAsync(MainWindow window, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _showEvent is not null)
        {
            await Task.Run(() => _showEvent.WaitOne(), cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(window.ShowAndActivate);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showListenerCancellation?.Cancel();
        _showEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
