using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LanPilot.App.ViewModels;

namespace LanPilot.App.Views;

public partial class DevicesView
{
    private bool _itemsAttached;

    public DevicesView()
    {
        InitializeComponent();
        Loaded += DevicesView_Loaded;
        Unloaded += DevicesView_Unloaded;
    }

    private async void DevicesView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_itemsAttached) return;

        DevicesLoadingOverlay.Visibility = Visibility.Visible;
        DeviceGrid.IsHitTestVisible = false;

        // Let WPF render the page shell and column headers first. The virtualized
        // rows are attached at idle priority so navigation never waits for them.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (!IsLoaded || DataContext is not MainViewModel viewModel) return;

        DeviceGrid.ItemsSource = viewModel.DevicesView;
        _itemsAttached = true;
        DevicesLoadingOverlay.Visibility = Visibility.Collapsed;
        DeviceGrid.IsHitTestVisible = true;
    }

    private void DevicesView_Unloaded(object sender, RoutedEventArgs e)
    {
        DeviceGrid.ItemsSource = null;
        _itemsAttached = false;
    }

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel &&
            DeviceGrid.SelectedItem is DeviceRowViewModel device &&
            device.CanEditPolicy)
        {
            viewModel.EditDeviceCommand.Execute(device);
        }
    }
}
