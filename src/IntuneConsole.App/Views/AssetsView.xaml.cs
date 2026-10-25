using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Assets;
using IntuneConsole.Core.Models;
using Microsoft.Win32;

namespace IntuneConsole.App.Views;

/// <summary>The asset register: records from SharePoint joined with live Intune devices.</summary>
public partial class AssetsView : UserControl
{
    /// <summary>Grid row wrapper (flat properties for sorting and pill colours).</summary>
    public sealed class Item
    {
        public AssetRow Row { get; init; } = new();
        public string SerialNumber => Row.SerialNumber;
        public string AssetTag => Row.AssetTag;
        public string Status => Row.Status;
        public string StatusTone => Row.Status switch
        {
            AssetStatus.InUse => "success",
            AssetStatus.InStock => "",
            AssetStatus.Loaner or AssetStatus.Repair => "pending",
            AssetStatus.Lost => "error",
            AssetStatus.Retired => "",
            _ => "pending",   // not in register
        };
    }

    private static Session S => Session.Current;
    private List<Item> _items = new();
    private ICollectionView? _view;
    private bool _loaded;

    public AssetsView()
    {
        InitializeComponent();
        ReportCombo.ItemsSource = AssetRegister.Reports;
        ReportCombo.SelectedIndex = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) { Bind(); return; }
        _loaded = true;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (S.Assets is null)
        {
            SetupPanel.Visibility = Visibility.Visible;
            AssetGrid.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "Not set up yet.";
            return;
        }
        SetupPanel.Visibility = Visibility.Collapsed;
        AssetGrid.Visibility = Visibility.Visible;

        var ok = await Shell.Run("Loading the asset register and Intune devices…", async () =>
        {
            var assetsTask = S.LoadAssetsAsync();
            var devicesTask = S.Devices.Count == 0 ? S.LoadDevicesAsync() : Task.CompletedTask;
            await assetsTask;
            await devicesTask;
        });
        if (ok) Bind();
        else SubtitleText.Text = "Couldn't load the register. See the message below, then Refresh.";
    }

    private void Bind()
    {
        var records = S.AssetRecords ?? new();
        var rows = AssetRegister.Merge(records, S.Devices);
        _items = rows.Select(r => new Item { Row = r }).ToList();
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = o => o is Item i && AssetRegister.InReport(i.Row, ReportCombo.SelectedItem as string ?? AssetRegister.ReportAll)
                                        && AssetRegister.Matches(i.Row, SearchBox.Text);
        AssetGrid.ItemsSource = _view;

        KpiTotal.Text = rows.Count(r => r.InRegister).ToString("N0");
        KpiMissing.Text = rows.Count(r => !r.InRegister).ToString("N0");
        KpiStock.Text = rows.Count(r => r.Status == AssetStatus.InStock).ToString("N0");
        KpiWarranty.Text = rows.Count(r => AssetRegister.InReport(r, AssetRegister.ReportWarrantySoon)).ToString("N0");
        KpiDiffers.Text = rows.Count(r => r.AssignmentDiffers).ToString("N0");
        SubtitleText.Text = $"{records.Count} record(s) in SharePoint · {S.Devices.Count(d => d.IsWindows)} Windows devices in Intune · " +
                            (S.Dell is null ? "Dell warranty not set up" : "Dell warranty linked") + " · double-click to open";
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (_view is null) return;
        CountText.Text = $"{_view.Cast<object>().Count():N0} shown";
    }

    private void Report_Changed(object sender, SelectionChangedEventArgs e) { _view?.Refresh(); UpdateCount(); }
    private void Search_TextChanged(object sender, TextChangedEventArgs e) { _view?.Refresh(); UpdateCount(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Shell.Navigate(new SettingsView(firstRun: false));

    private void AssetGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void AssetGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (AssetGrid.SelectedItem is not Item item) return;
        new AssetWindow(item.Row).ShowDialog();
        Bind();   // the session cache is updated on every save
    }

    private void AddAsset_Click(object sender, RoutedEventArgs e)
    {
        if (S.Assets is null) return;
        new AssetWindow(new AssetRow()).ShowDialog();
        Bind();
    }

    private async void AddMissing_Click(object sender, RoutedEventArgs e)
    {
        if (S.Assets is null) return;
        var missing = _items.Where(i => !i.Row.InRegister && i.Row.Device is not null).Select(i => i.Row.Device!).ToList();
        if (missing.Count == 0) { Shell.Status("Every Intune Windows device is already in the register."); return; }
        if (!Shell.Confirm("Add missing devices",
                $"Create {missing.Count} asset record(s) from Intune?\n\nEach gets status \"In use\", assigned to its Intune primary user." +
                (S.Dell is null ? "" : "\nDell warranty is looked up for them afterwards.")))
            return;

        var added = 0;
        var failed = new List<string>();
        await Shell.Run($"Adding {missing.Count} records…", async () =>
        {
            var records = missing.Select(AssetRegister.NewRecordFor).ToList();
            if (S.Dell is not null)
            {
                try
                {
                    var warranties = (await S.Dell.LookupAsync(records.Select(r => r.SerialNumber)))
                        .ToDictionary(w => w.ServiceTag, StringComparer.OrdinalIgnoreCase);
                    foreach (var r in records)
                        if (warranties.TryGetValue(r.SerialNumber, out var w) && !w.Invalid) AssetRegister.ApplyWarranty(r, w, DateTimeOffset.UtcNow);
                }
                catch (Exception ex) { failed.Add($"warranty lookup ({ex.Message})"); }
            }
            foreach (var r in records)
            {
                try { await S.SaveAssetAsync(null, r, "Added from Intune"); added++; }
                catch (Exception ex) { failed.Add($"{r.SerialNumber} ({ex.Message})"); }
            }
        });
        Bind();
        Shell.Status($"Added {added} of {missing.Count} record(s)." + (failed.Count > 0 ? $" Problems: {string.Join("; ", failed.Take(5))}" : ""),
            isError: failed.Count > 0);
    }

    private async void Warranty_Click(object sender, RoutedEventArgs e)
    {
        if (S.Assets is null || S.AssetRecords is null) return;
        if (S.Dell is null) { Shell.Status("Add the Dell TechDirect client ID and secret in Settings first.", isError: true); return; }

        var due = S.AssetRecords.Where(r => r.Status is not (AssetStatus.Retired or AssetStatus.Lost)
                                            && (r.WarrantyEnd is null || r.WarrantyChecked is null || r.WarrantyChecked < DateTimeOffset.UtcNow.AddDays(-30)))
            .ToList();
        if (due.Count == 0) { Shell.Status("All warranties were checked in the last 30 days."); return; }

        int updated = 0, unknown = 0;
        var problems = new List<string>();
        await Shell.Run($"Looking up {due.Count} Dell warranties…", async () =>
        {
            var found = (await S.Dell.LookupAsync(due.Select(r => r.SerialNumber))).ToDictionary(w => w.ServiceTag, StringComparer.OrdinalIgnoreCase);
            foreach (var r in due)
            {
                if (!found.TryGetValue(AssetRegister.Key(r.SerialNumber), out var w) || w.Invalid) { unknown++; continue; }
                var after = r.Clone();
                AssetRegister.ApplyWarranty(after, w, DateTimeOffset.UtcNow);
                try { await S.SaveAssetAsync(r, after); updated++; }
                catch (Exception ex) { problems.Add($"{r.SerialNumber} ({ex.Message})"); }
            }
        });
        Bind();
        Shell.Status($"Warranty checked for {updated} record(s)." + (unknown > 0 ? $" {unknown} not recognised by Dell (non-Dell or mistyped serial)." : "") +
                     (problems.Count > 0 ? $" Problems: {string.Join("; ", problems.Take(5))}" : ""), isError: problems.Count > 0);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (S.AssetRecords is null) return;
        var dlg = new SaveFileDialog { FileName = $"assets-{DateTime.Now:yyyyMMdd}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, AssetCsv.Export(S.AssetRecords.OrderBy(r => r.SerialNumber)), new UTF8Encoding(true));
        Shell.Status($"Exported {S.AssetRecords.Count} record(s) to {dlg.FileName}. Edit in Excel, then Import CSV to apply changes.");
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (S.Assets is null || S.AssetRecords is null) return;
        var dlg = new OpenFileDialog { Filter = "CSV|*.csv", Title = "Import assets" };
        if (dlg.ShowDialog() != true) return;

        var plan = AssetCsv.PlanImport(File.ReadAllText(dlg.FileName), S.AssetRecords);
        var summary = $"{plan.Creates.Count} new record(s), {plan.Updates.Count} change(s), {plan.Unchanged} unchanged.";
        if (plan.Errors.Count > 0)
            summary += $"\n\n{plan.Errors.Count} problem(s), these rows will be skipped:\n" + string.Join("\n", plan.Errors.Take(12)) +
                       (plan.Errors.Count > 12 ? "\n…" : "");
        if (plan.Creates.Count + plan.Updates.Count == 0)
        {
            MessageBox.Show(Application.Current.MainWindow!, summary, "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Shell.Confirm("Import assets", summary + "\n\nApply now? Every change is recorded in the asset history.")) return;

        var done = 0;
        var problems = new List<string>();
        await Shell.Run("Importing…", async () =>
        {
            foreach (var r in plan.Creates)
            {
                try { await S.SaveAssetAsync(null, r, $"Imported from {Path.GetFileName(dlg.FileName)}", "Imported"); done++; }
                catch (Exception ex) { problems.Add($"{r.SerialNumber} ({ex.Message})"); }
            }
            foreach (var (before, after) in plan.Updates)
            {
                try { await S.SaveAssetAsync(before, after, $"Imported from {Path.GetFileName(dlg.FileName)}"); done++; }
                catch (Exception ex) { problems.Add($"{after.SerialNumber} ({ex.Message})"); }
            }
        });
        Bind();
        Shell.Status($"Imported {done} record(s)." + (problems.Count > 0 ? $" Problems: {string.Join("; ", problems.Take(5))}" : ""), isError: problems.Count > 0);
    }
}
