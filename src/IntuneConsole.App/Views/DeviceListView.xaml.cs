using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Models;
using Microsoft.Win32;

namespace IntuneConsole.App.Views;

public partial class DeviceListView : UserControl
{
    private ICollectionView? _view;
    private bool _loadedOnce;

    public DeviceListView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        SubtitleText.Text = "Loading devices from Intune…";
        var ok = await Shell.Run("Loading devices from Intune and Datto…", () => Session.Current.LoadDevicesAsync());
        if (!ok)
        {
            SubtitleText.Text = "Couldn't load devices. Check the message below, then Refresh.";
            return;
        }
        Bind();
    }

    private void Bind()
    {
        var s = Session.Current;
        var devices = s.Devices;
        var now = DateTimeOffset.UtcNow;
        var staleDays = Math.Max(1, s.Settings.StaleDays);

        _view = CollectionViewSource.GetDefaultView(devices);
        _view.Filter = Matches;
        DeviceGrid.ItemsSource = _view;

        KpiTotal.Text = devices.Count.ToString("N0");
        KpiNoncompliant.Text = devices.Count(d => d.IsNoncompliant).ToString("N0");
        KpiStaleLabel.Text = $"No check-in {staleDays}+ days";
        KpiStale.Text = devices.Count(d => d.IsStale(now, staleDays)).ToString("N0");
        KpiUnencrypted.Text = devices.Count(d => d.IsEncrypted == false).ToString("N0");

        if (s.Datto is null)
        {
            KpiDatto.Text = "–";
            KpiDatto.ToolTip = "Datto isn't set up yet (Settings).";
        }
        else
        {
            KpiDatto.Text = $"{devices.Count(d => d.Datto is { Online: false })} / {devices.Count(d => d.Datto is null)}";
            KpiDatto.ToolTip = "Offline in Datto / not found in Datto (matched by hostname)";
        }

        var dattoNote = s.Datto is null ? "Datto not set up"
            : s.DattoLoadError is not null ? $"Datto error: {s.DattoLoadError}"
            : "Datto RMM linked";
        SubtitleText.Text = $"Intune data loaded {s.DevicesLoadedAt?.ToString("t")} · {dattoNote} · double-click a device to troubleshoot";
        UpdateCount();
    }

    private bool Matches(object o)
    {
        if (o is not ManagedDevice d) return false;
        var now = DateTimeOffset.UtcNow;
        var staleDays = Math.Max(1, Session.Current.Settings.StaleDays);

        if (FilterNoncompliant.IsChecked == true && !d.IsNoncompliant) return false;
        if (FilterStale.IsChecked == true && !d.IsStale(now, staleDays)) return false;
        if (FilterUnencrypted.IsChecked == true && d.IsEncrypted != false) return false;
        if (FilterDattoOffline.IsChecked == true && d.Datto is not { Online: false }) return false;
        if (FilterNotInDatto.IsChecked == true && d.Datto is not null) return false;

        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        return Has(d.DeviceName, q) || Has(d.UserDisplayName, q) || Has(d.UserPrincipalName, q)
               || Has(d.SerialNumber, q) || Has(d.Model, q);
    }

    private static bool Has(string? s, string q) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    private void UpdateCount()
    {
        if (_view is null) return;
        var shown = _view.Cast<object>().Count();
        CountText.Text = $"Showing {shown:N0} of {Session.Current.Devices.Count:N0}";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
        UpdateCount();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        _view?.Refresh();
        UpdateCount();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (DeviceGrid.SelectedItem is ManagedDevice d) Shell.Navigate(new DeviceDetailView(d));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_view is null) return;
        var dlg = new SaveFileDialog { FileName = $"devices-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder("Device,User,UPN,OS,Compliance,LastCheckIn,Model,Serial,Encrypted,Datto\r\n");
        foreach (ManagedDevice d in _view)
            sb.AppendLine(string.Join(",", new[]
            {
                d.DeviceName, d.UserDisplayName, d.UserPrincipalName, d.OsVersion, d.ComplianceText,
                d.LastSyncDateTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), d.Model, d.SerialNumber,
                d.IsEncrypted?.ToString(), d.DattoStatusText
            }.Select(Csv)));
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        Shell.Status($"Exported to {dlg.FileName}");
    }

    private static string Csv(string? s)
    {
        s ??= "";
        // Guard against spreadsheet formula injection from device or user names.
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
