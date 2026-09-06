using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LanPilot.App.Services;
using LanPilot.App.ViewModels;
using LanPilot.App.Views;
using LanPilot.Contracts;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace LanPilot.App;

public partial class MainWindow : FluentWindow
{
    private readonly LanPilotClient _client;
    private readonly MainViewModel _viewModel;
    private readonly SnackbarService _snackbarService = new();
    private readonly Dictionary<PageKey, object> _views;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private bool _isSelecting;
    private bool _exitRequested;

    public MainWindow(LanPilotClient client, UiSettingsStore uiSettingsStore)
    {
        _client = client;
        InitializeComponent();

        _viewModel = new MainViewModel(client, uiSettingsStore);
        DataContext = _viewModel;
        _viewModel.NotificationRequested += OnNotificationRequested;
        _viewModel.ThemeRequested += (_, theme) => ApplyTheme(theme);
        _viewModel.DeviceEditRequested += _ => Navigate(PageKey.Devices, DevicesNav);
        _viewModel.ApplicationControlRequested += () => Navigate(PageKey.Applications, ApplicationsNav);
        _snackbarService.SetSnackbarPresenter(RootSnackbar);
        ApplyTheme(_viewModel.Theme);

        _views = new Dictionary<PageKey, object>
        {
            [PageKey.Overview] = new OverviewView(),
            [PageKey.Devices] = new DevicesView(),
            [PageKey.Rules] = new RulesView(),
            [PageKey.Applications] = new ApplicationsView(),
            [PageKey.About] = new AboutView(),
            [PageKey.Settings] = new SettingsView()
        };
        MainContent.Content = _views[PageKey.Overview];

        System.Windows.Forms.ContextMenuStrip menu = new();
        menu.Items.Add("Open LanPilot", null, (_, _) => Dispatcher.Invoke(ShowAndActivate));
        menu.Items.Add("Scan network", null, (_, _) => Dispatcher.Invoke(() => _viewModel.ScanCommand.Execute(null)));
        menu.Items.Add("Emergency pause", null, (_, _) => Dispatcher.Invoke(() => _viewModel.EmergencyPauseCommand.Execute(null)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit LanPilot", null, async (_, _) => await Dispatcher.InvokeAsync(ExitAsync));
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "LanPilot",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);

        Loaded += async (_, _) => await _viewModel.InitializeAsync();
        Closing += OnClosing;
    }

    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OverviewNav_Click(object sender, RoutedEventArgs e) => Navigate(PageKey.Overview, OverviewNav);
    private void DevicesNav_Click(object sender, RoutedEventArgs e) => Navigate(PageKey.Devices, DevicesNav);
    private void RulesNav_Click(object sender, RoutedEventArgs e) => Navigate(PageKey.Rules, RulesNav);
    private void ApplicationsNav_Click(object sender, RoutedEventArgs e) => Navigate(PageKey.Applications, ApplicationsNav);
    private void AboutNav_Click(object sender, RoutedEventArgs e) => Navigate(PageKey.About, AboutNav);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => Navigate(PageKey.Settings, SettingsNav);

    private void Navigate(PageKey key, ToggleButton selected)
    {
        if (_isSelecting) return;
        _isSelecting = true;
        OverviewNav.IsChecked = selected == OverviewNav;
        DevicesNav.IsChecked = selected == DevicesNav;
        RulesNav.IsChecked = selected == RulesNav;
        ApplicationsNav.IsChecked = selected == ApplicationsNav;
        AboutNav.IsChecked = selected == AboutNav;
        SettingsNav.IsChecked = selected == SettingsNav;
        _isSelecting = false;
        MainContent.Content = _views[key];
    }

    private void OnNotificationRequested(object? sender, NotificationEvent notification)
    {
        ControlAppearance appearance = notification.Severity switch
        {
            NotificationSeverity.Success => ControlAppearance.Success,
            NotificationSeverity.Warning => ControlAppearance.Caution,
            NotificationSeverity.Error => ControlAppearance.Danger,
            _ => ControlAppearance.Info
        };
        _snackbarService.Show(notification.Title, notification.Message, appearance, null, TimeSpan.FromSeconds(4));
        if (!IsVisible)
        {
            _trayIcon.BalloonTipTitle = notification.Title;
            _trayIcon.BalloonTipText = notification.Message;
            _trayIcon.ShowBalloonTip(3000);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        Hide();
        _trayIcon.ShowBalloonTip(2000, "LanPilot is still running", "Use the tray menu to pause control or exit.", System.Windows.Forms.ToolTipIcon.Info);
    }

    private async Task ExitAsync()
    {
        try
        {
            var result = await _client.ExitControlAsync(CancellationToken.None);
            if (!result.Success) { _viewModel.ReportShutdownFailure(result.Message); return; }
        }
        catch (Exception ex) { _viewModel.ReportShutdownFailure(ex.Message); return; }
        _viewModel.StopConnectionRecovery();
        _exitRequested = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        await _client.DisposeAsync();
        System.Windows.Application.Current.Shutdown();
    }

    private void ApplyTheme(string theme)
    {
        bool dark = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase);
        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Acrylic, true);
        WindowBackdropType = WindowBackdropType.Acrylic;
        if (dark)
        {
            SetBrush("PilotWindowBaseBrush", "#E913171B");
            SetBrush("PilotBackgroundBrush", "#8F151A22");
            SetBrush("PilotRailBrush", "#B512171B");
            SetBrush("PilotSurfaceBrush", "#70272C35");
            SetBrush("PilotSurfaceSoftBrush", "#5A303645");
            SetBrush("PilotRouterRowBrush", "#286D9EFF");
            SetBrush("PilotDialogSurfaceBrush", "#FC171B22");
            SetBrush("PilotBorderBrush", "#24FFFFFF");
            SetBrush("PilotPrimaryBrush", "#6D9EFF");
            SetBrush("PilotTextPrimaryBrush", "#F7F8FC");
            SetBrush("PilotTextSecondaryBrush", "#C2C7D4");
        }
        else
        {
            SetBrush("PilotWindowBaseBrush", "#F2F7F4F9");
            SetBrush("PilotBackgroundBrush", "#F0F7F4F9");
            SetBrush("PilotRailBrush", "#F4F2F0F6");
            SetBrush("PilotSurfaceBrush", "#FFFFFFFF");
            SetBrush("PilotSurfaceSoftBrush", "#FAF9FC");
            SetBrush("PilotRouterRowBrush", "#184F6EF7");
            SetBrush("PilotDialogSurfaceBrush", "#FFFDFCFF");
            SetBrush("PilotBorderBrush", "#E7E2EA");
            SetBrush("PilotPrimaryBrush", "#4F6EF7");
            SetBrush("PilotTextPrimaryBrush", "#303038");
            SetBrush("PilotTextSecondaryBrush", "#66677A");
        }
    }

    private static void SetBrush(string key, string color) =>
        System.Windows.Application.Current.Resources[key] =
            new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);

    private enum PageKey { Overview, Devices, Rules, Applications, About, Settings }
}

public sealed class BooleanVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBooleanVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is Visibility.Collapsed;
}

public sealed class BooleanOpacityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? 1d : 0.25d;
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RateLimitDisplayConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is long bitsPerSecond
            ? bitsPerSecond >= 1_000_000
                ? $"{bitsPerSecond / 1_000_000d:0.##} Mbps"
                : $"{bitsPerSecond / 1_000d:0.#} Kbps"
            : "Unlimited";

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
