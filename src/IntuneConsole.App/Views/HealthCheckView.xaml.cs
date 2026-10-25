using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Datto;
using IntuneConsole.Core.Diagnostics;
using IntuneConsole.Core.Models;
using Microsoft.Win32;

namespace IntuneConsole.App.Views;

/// <summary>Reconciliation across Intune, Entra ID, Autopilot and Datto.</summary>
public partial class HealthCheckView : UserControl
{
    public sealed class Row
    {
        public ReconciliationFinding Finding { get; init; } = new();
        // Pill tone words understood by the shared converters.
        public string SeverityTone => Finding.Severity switch
        {
            FindingSeverity.Problem => "error",
            FindingSeverity.Warning => "pending",
            _ => "",
        };
        public Brush NameBrush => Finding.IntuneDevice is null
            ? (Brush)Application.Current.FindResource("Ink")
            : (Brush)Application.Current.FindResource("Accent");
    }

    public sealed class Category
    {
        public string Name { get; init; } = "";
        public int Count { get; init; }
        public bool IsSelected { get; init; }
    }

    private const string All = "All findings";
    private List<Row> _rows = new();
    private string _category = All;

    public HealthCheckView()
    {
        InitializeComponent();
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var s = Session.Current;
        if (s.Intune is null || s.Records is null)
        {
            Shell.Status("Sign-in isn't set up yet. Open Settings first.", isError: true);
            return;
        }

        var notes = new List<string>();
        List<ManagedDevice> intune = new();
        List<EntraDevice>? entra = null;
        List<AutopilotDevice>? autopilot = null;
        List<DattoDevice>? datto = null;
        List<SignInEvent>? windowsSignIns = null;

        RunButton.IsEnabled = false;
        var ok = await Shell.Run("Reading Intune, Entra ID, Autopilot and Datto…", async () =>
        {
            // All platforms from Intune (the check itself only looks at Windows), so phones don't show up as "missing".
            var intuneTask = s.Intune.GetAllDevicesAsync();
            var entraTask = s.Records.GetEntraDevicesAsync();
            var apTask = s.Records.GetAutopilotDevicesAsync();
            Task<List<DattoDevice>>? dattoTask = s.Datto?.GetDevicesAsync();
            var signInTask = s.Directory!.GetWindowsSignInsAsync();

            intune = await intuneTask;
            try { entra = await entraTask; } catch (Exception ex) { notes.Add($"Entra ID skipped ({Short(ex)})"); }
            try { autopilot = await apTask; } catch (Exception ex) { notes.Add($"Autopilot skipped ({Short(ex)})"); }
            if (dattoTask is null) notes.Add("Datto skipped (not set up)");
            else
            {
                try { datto = await dattoTask; } catch (Exception ex) { notes.Add($"Datto skipped ({Short(ex)})"); }
            }
            try { windowsSignIns = await signInTask; } catch (Exception ex) { notes.Add($"Primary user check skipped ({Short(ex)})"); }
            if (datto is not null) DattoClient.MatchDevices(intune, datto);
        });
        RunButton.IsEnabled = true;
        if (!ok) return;

        var findings = Reconciler.Analyze(new Reconciler.Inputs { Intune = intune, Entra = entra, Autopilot = autopilot, Datto = datto, WindowsSignIns = windowsSignIns });
        _rows = findings.Select(f => new Row { Finding = f }).ToList();
        _category = All;
        if (_rows.Count == 0) EmptyText.Text = "No mismatches found. Intune, Entra ID, Autopilot and Datto line up.";
        Bind();

        var counts = $"{intune.Count(d => d.IsWindows)} Windows devices in Intune" +
                     (entra is null ? "" : $" · {entra.Count} Entra devices") +
                     (autopilot is null ? "" : $" · {autopilot.Count} Autopilot") +
                     (datto is null ? "" : $" · {datto.Count} Datto") +
                     (windowsSignIns is null ? "" : $" · {windowsSignIns.Count} PC sign-ins (14 days)");
        SubtitleText.Text = $"Checked {DateTime.Now:t}: {counts}." + (notes.Count > 0 ? $" {string.Join(". ", notes)}." : "");
        ExportButton.IsEnabled = _rows.Count > 0;
        var problems = findings.Count(f => f.Severity == FindingSeverity.Problem);
        Shell.Status(findings.Count == 0 ? "Everything lines up." : $"{findings.Count} findings, {problems} marked as problems.",
            isError: notes.Count > 0 && notes.Any(n => !n.Contains("not set up")));
    }

    private static string Short(Exception ex) => ex.Message.Length > 120 ? ex.Message[..120] + "…" : ex.Message;

    private void Bind()
    {
        var categories = new List<Category> { new() { Name = All, Count = _rows.Count, IsSelected = _category == All } };
        categories.AddRange(_rows.GroupBy(r => r.Finding.Category)
            .OrderByDescending(g => g.Max(r => r.Finding.Severity)).ThenByDescending(g => g.Count())
            .Select(g => new Category { Name = g.Key, Count = g.Count(), IsSelected = _category == g.Key }));
        CategoryTiles.ItemsSource = categories;

        var shown = _category == All ? _rows : _rows.Where(r => r.Finding.Category == _category).ToList();
        FindingsGrid.ItemsSource = shown;
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name || name == _category) return;
        _category = name;
        var shown = _category == All ? _rows : _rows.Where(r => r.Finding.Category == _category).ToList();
        FindingsGrid.ItemsSource = shown;
    }

    private void FindingsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void FindingsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (FindingsGrid.SelectedItem is Row { Finding.IntuneDevice: { } d }) Shell.Navigate(new DeviceDetailView(d));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = $"record-health-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "CSV|*.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("Severity,Check,Device,Serial,Detail,Suggestion\r\n");
        foreach (var r in _rows)
            sb.AppendLine(string.Join(",", new[]
            {
                r.Finding.SeverityText, r.Finding.Category, r.Finding.DeviceName, r.Finding.SerialNumber, r.Finding.Detail, r.Finding.Suggestion
            }.Select(Csv)));
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        Shell.Status($"Exported to {dlg.FileName}");
    }

    private static string Csv(string? s)
    {
        s ??= "";
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
