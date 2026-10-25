using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IntuneConsole.App.Services;
using IntuneConsole.App.Views;

namespace IntuneConsole.App;

public partial class MainWindow : Window
{
    private DeviceListView? _deviceList;
    private LogViewerView? _lastLogViewer;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => { StatusBar.Visibility = Visibility.Collapsed; _statusTimer.Stop(); };
    }

    public void Start()
    {
        if (!Session.Current.Settings.IsSignInConfigured)
        {
            ShowView(new SettingsView(firstRun: true));
            return;
        }
        ShowDevices();
    }

    public void ShowDevices()
    {
        _deviceList ??= new DeviceListView();
        ShowView(_deviceList);
    }

    /// <summary>Called after settings change so the device list reloads with the new configuration.</summary>
    public void ResetViews()
    {
        _deviceList = null;
        _lastLogViewer = null;
    }

    public void ShowView(UserControl view)
    {
        ContentHost.Content = view;
        if (view is LogViewerView lv) _lastLogViewer = lv;

        NavDevices.IsChecked = view is DeviceListView or DeviceDetailView;
        NavDiagnostics.IsChecked = view is LogViewerView;
        NavSettings.IsChecked = view is SettingsView;
        RefreshSignedIn();
    }

    public void RefreshSignedIn()
    {
        var user = Session.Current.Auth?.UserName;
        SignedInText.Text = user is null ? "Not signed in" : $"Signed in as {user}";
        SignOutButton.Visibility = user is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "ErrorFg" : "Ink");
        StatusBar.Background = (System.Windows.Media.Brush)FindResource(isError ? "ErrorBg" : "Surface");
        StatusBar.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        if (!isError) _statusTimer.Start(); // errors stay until dismissed
    }

    public void SetBusy(string? text)
    {
        BusyBar.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null) ShowStatus(text, false);
        RefreshSignedIn();
    }

    private void NavDevices_Click(object sender, RoutedEventArgs e) => ShowDevices();

    private void NavDiagnostics_Click(object sender, RoutedEventArgs e) =>
        ShowView(_lastLogViewer ?? new LogViewerView(null, null));

    private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowView(new SettingsView(firstRun: false));

    private void DismissStatus_Click(object sender, RoutedEventArgs e) => StatusBar.Visibility = Visibility.Collapsed;

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Current.Auth is null) return;
        await Session.Current.Auth.SignOutAsync();
        ResetViews();
        RefreshSignedIn();
        ShowStatus("Signed out. You'll be asked to sign in again on the next action.", false);
        ShowDevices();
    }
}
