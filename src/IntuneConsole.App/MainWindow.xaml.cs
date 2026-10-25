using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using IntuneConsole.Core.Models;
using IntuneConsole.App.Services;
using IntuneConsole.App.Views;

namespace IntuneConsole.App;

public partial class MainWindow : Window
{
    private DeviceListView? _deviceList;
    private LogViewerView? _lastLogViewer;
    private HealthCheckView? _healthCheck;
    private AssetsView? _assets;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private CancellationTokenSource? _searchCts;

    public MainWindow()
    {
        InitializeComponent();
        _statusTimer.Tick += (_, _) => { StatusBar.Visibility = Visibility.Collapsed; _statusTimer.Stop(); };
        _searchDebounce.Tick += async (_, _) => { _searchDebounce.Stop(); await RunSearchAsync(); };

        // The result popup is its own window: close it on clicks elsewhere in the app, or when the app moves or loses focus.
        PreviewMouseDown += (_, _) => { if (!GlobalSearch.IsMouseOver) SearchPopup.IsOpen = false; };
        Deactivated += (_, _) => SearchPopup.IsOpen = false;
        LocationChanged += (_, _) => SearchPopup.IsOpen = false;
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
        _healthCheck = null;
        _assets = null;
    }

    public void ShowView(UserControl view)
    {
        ContentHost.Content = view;
        if (view is LogViewerView lv) _lastLogViewer = lv;

        NavDevices.IsChecked = view is DeviceListView or DeviceDetailView or UserView;
        NavDiagnostics.IsChecked = view is LogViewerView;
        NavSettings.IsChecked = view is SettingsView;
        NavHealth.IsChecked = view is HealthCheckView;
        NavAssets.IsChecked = view is AssetsView;
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

    // ---------------- Global search ----------------

    private void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        GlobalSearch.Focus();
        GlobalSearch.SelectAll();
    }

    private void GlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = GlobalSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDebounce.Stop();
        if (GlobalSearch.Text.Trim().Length < 2)
        {
            _searchCts?.Cancel();
            SearchPopup.IsOpen = false;
            return;
        }
        _searchDebounce.Start();
    }

    private void GlobalSearch_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchResults.Items.Count > 0 && GlobalSearch.Text.Trim().Length >= 2) SearchPopup.IsOpen = true;
    }

    private void GlobalSearch_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Keep the popup open while focus moves into the result list.
        if (e.NewFocus is DependencyObject d && IsInside(d, SearchResults)) return;
        SearchPopup.IsOpen = false;
    }

    private static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (var c = child; c is not null; c = System.Windows.Media.VisualTreeHelper.GetParent(c) ?? LogicalTreeHelper.GetParent(c))
            if (ReferenceEquals(c, parent)) return true;
        return false;
    }

    private void GlobalSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when SearchResults.Items.Count > 0:
                SearchPopup.IsOpen = true;
                SearchResults.SelectedIndex = 0;
                (SearchResults.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                _searchDebounce.Stop();
                if (SearchResults.Items.Count > 0 && SearchPopup.IsOpen) OpenResult(SearchResults.Items[0] as SearchResult);
                else _ = RunSearchAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void SearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenResult(SearchResults.SelectedItem as SearchResult); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchPopup.IsOpen = false; GlobalSearch.Focus(); e.Handled = true; }
        else if (e.Key == Key.Up && SearchResults.SelectedIndex == 0) { GlobalSearch.Focus(); e.Handled = true; }
    }

    private void SearchResults_MouseUp(object sender, MouseButtonEventArgs e) => OpenResult(SearchResults.SelectedItem as SearchResult);

    private async Task RunSearchAsync()
    {
        var query = GlobalSearch.Text.Trim();
        if (query.Length < 2) return;

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var s = Session.Current;
        if (s.Directory is null)
        {
            ShowSearch(new List<SearchResult>(), "Sign-in isn't set up yet. Open Settings first.");
            return;
        }

        ShowSearch(null, "Searching…");
        var results = new List<SearchResult>();
        string? problem = null;

        // Devices come from the cached list (loaded on the Devices screen); load it once if needed.
        try
        {
            if (s.Devices.Count == 0 && s.Intune is not null) await s.LoadDevicesAsync(cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { problem = $"Devices: {ex.Message}"; }

        try
        {
            var users = await s.Directory.SearchUsersAsync(query, ct: cts.Token);
            if (cts.IsCancellationRequested) return;
            results.AddRange(users.Select(u => new SearchResult { User = u }));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { problem = $"People: {ex.Message}"; }

        if (cts.IsCancellationRequested) return;
        results.AddRange(s.Devices.Where(d => d.Matches(query)).Take(10).Select(d => new SearchResult { Device = d }));

        var status = problem ?? (results.Count == 0 ? $"No people or devices match \"{query}\"."
            : "Enter opens the top result · ↓ to choose · names match whole words, other fields match from the start");
        ShowSearch(results, status);
    }

    private void ShowSearch(List<SearchResult>? results, string status)
    {
        if (results is not null) SearchResults.ItemsSource = results;
        SearchResults.Visibility = results is { Count: > 0 } || (results is null && SearchResults.Items.Count > 0)
            ? Visibility.Visible : Visibility.Collapsed;
        SearchStatus.Text = status;
        SearchPopup.IsOpen = GlobalSearch.IsKeyboardFocusWithin || SearchResults.IsKeyboardFocusWithin;
    }

    private void OpenResult(SearchResult? r)
    {
        if (r is null) return;
        SearchPopup.IsOpen = false;
        if (r.User is not null) ShowView(new UserView(r.User));
        else if (r.Device is not null) ShowView(new DeviceDetailView(r.Device));
    }

    private void NavDiagnostics_Click(object sender, RoutedEventArgs e) =>
        ShowView(_lastLogViewer ?? new LogViewerView(null, null));

    private void NavAssets_Click(object sender, RoutedEventArgs e) => ShowView(_assets ??= new AssetsView());

    private void NavHealth_Click(object sender, RoutedEventArgs e) => ShowView(_healthCheck ??= new HealthCheckView());

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
