using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LanPilot.App.ViewModels;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace LanPilot.App.Views;

public partial class ApplicationsView
{
    private bool _itemsAttached;
    private CancellationTokenSource? _refreshCancellation;

    public ApplicationsView()
    {
        InitializeComponent();
        Loaded += ApplicationsView_Loaded;
        Unloaded += ApplicationsView_Unloaded;
    }

    private async void ApplicationsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_itemsAttached) return;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!IsLoaded || DataContext is not MainViewModel viewModel) return;

        ApplicationGrid.ItemsSource = viewModel.ApplicationsView;
        _itemsAttached = true;
        CancellationTokenSource refreshCancellation = new();
        CancellationToken cancellationToken = refreshCancellation.Token;
        _refreshCancellation = refreshCancellation;
        await viewModel.RefreshApplicationsCommand.ExecuteAsync(null);
        if (cancellationToken.IsCancellationRequested ||
            !ReferenceEquals(_refreshCancellation, refreshCancellation))
            return;

        _ = RefreshLoopAsync(viewModel, cancellationToken);
    }

    private void ApplicationsView_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        ApplicationGrid.ItemsSource = null;
        _itemsAttached = false;
    }

    private void ApplicationGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindAncestor<WpfButtonBase>(source) is not null ||
            FindAncestor<DataGridRow>(source)?.Item is not ApplicationRowViewModel application ||
            DataContext is not MainViewModel viewModel)
            return;

        viewModel.EditApplicationCommand.Execute(application);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static async Task RefreshLoopAsync(MainViewModel viewModel, CancellationToken cancellationToken)
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
