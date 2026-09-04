using System.Windows;
using System.Windows.Threading;
using LanPilot.App.ViewModels;

namespace LanPilot.App.Views;

public partial class OverviewView
{
    private CancellationTokenSource? _applicationRefreshCancellation;

    public OverviewView()
    {
        InitializeComponent();
        Loaded += OverviewView_Loaded;
        Unloaded += OverviewView_Unloaded;
    }

    private async void OverviewView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_applicationRefreshCancellation is not null) return;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!IsLoaded || DataContext is not MainViewModel viewModel) return;

        CancellationTokenSource refreshCancellation = new();
        CancellationToken cancellationToken = refreshCancellation.Token;
        _applicationRefreshCancellation = refreshCancellation;
        await viewModel.RefreshApplicationsSilentlyAsync();
        if (cancellationToken.IsCancellationRequested ||
            !ReferenceEquals(_applicationRefreshCancellation, refreshCancellation))
            return;

        _ = RefreshApplicationsAsync(viewModel, cancellationToken);
    }

    private void OverviewView_Unloaded(object sender, RoutedEventArgs e)
    {
        _applicationRefreshCancellation?.Cancel();
        _applicationRefreshCancellation?.Dispose();
        _applicationRefreshCancellation = null;
    }

    private static async Task RefreshApplicationsAsync(MainViewModel viewModel, CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await viewModel.RefreshApplicationsSilentlyAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
