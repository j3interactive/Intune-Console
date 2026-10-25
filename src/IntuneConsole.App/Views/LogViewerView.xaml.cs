using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Diagnostics;
using IntuneConsole.Core.Logs;
using IntuneConsole.Core.Settings;
using Microsoft.Win32;

namespace IntuneConsole.App.Views;

public partial class LogViewerView : UserControl
{
    private LogBundle? _bundle;
    private string? _deviceName;
    private Dictionary<BundleFile, List<LogLine>> _parsed = new();
    private readonly Dictionary<BundleFile, List<LogLine>> _eventCache = new();
    private List<LogLine> _current = new();
    private BundleFile? _currentFile;
    private bool _loaded;

    private static readonly Regex HexCode = new(@"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

    public LogViewerView(LogBundle? bundle, string? deviceName)
    {
        InitializeComponent();
        _bundle = bundle;
        _deviceName = deviceName;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await ShowBundleAsync();
    }

    private async Task ShowBundleAsync()
    {
        OpenFolderButton.IsEnabled = _bundle is not null;
        if (_bundle is null)
        {
            FileList.ItemsSource = null;
            LinesGrid.ItemsSource = null;
            return;
        }

        var bundle = _bundle;
        TitleText.Text = _deviceName is null ? "Diagnostics bundle" : $"Diagnostics · {_deviceName}";
        SubtitleText.Text = $"{bundle.Files.Count} files · {bundle.Folder}";

        await Shell.Run("Reading logs…", async () =>
        {
            _parsed = await Task.Run(() => bundle.ParseAll());
        });

        // Group the file list.
        var view = new ListCollectionView(bundle.Files);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BundleFile.Group)));
        FileList.ItemsSource = view;

        // Likely causes from the Intune Management Extension logs.
        var imeLines = _parsed.Where(kv => kv.Key.Kind == BundleFileKind.Log).SelectMany(kv => kv.Value);
        var findings = LikelyCauses.Analyze(imeLines, max: 3);
        FindingsList.ItemsSource = findings;
        FindingsList.Visibility = findings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Start on the IME log with the most errors, or the main IME log.
        var first = bundle.Files.Where(f => f.IsImeLog).OrderByDescending(f => f.ErrorCount ?? 0).FirstOrDefault()
                    ?? bundle.Files.FirstOrDefault(f => f.Kind == BundleFileKind.Log)
                    ?? bundle.Files.FirstOrDefault();
        FileList.SelectedItem = first;

        Shell.Status(findings.Count > 0
            ? $"Found {findings.Count} likely cause(s). Each one shows the log line it matched."
            : "Logs loaded. No known error patterns matched; check the error lines.");
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not BundleFile f) return;
        _currentFile = f;

        if (f.Kind is BundleFileKind.Html or BundleFileKind.Other)
        {
            ShowExternal(f, f.Kind == BundleFileKind.Html
                ? $"{f.Name} is a report. It opens best in your browser."
                : $"{f.Name} isn't a text log.");
            return;
        }

        if (f.Kind == BundleFileKind.EventLog)
        {
            if (!_eventCache.TryGetValue(f, out var ev))
            {
                try
                {
                    ev = EventLogFile.Read(f.FullPath);
                    _eventCache[f] = ev;
                    f.ErrorCount = ev.Count(l => l.Level == LogLevel.Error);
                    f.WarningCount = ev.Count(l => l.Level == LogLevel.Warning);
                    (FileList.ItemsSource as ICollectionView)?.Refresh();
                }
                catch (Exception ex)
                {
                    ShowExternal(f, $"Couldn't read this event log here ({ex.Message}). Open it in Event Viewer instead.");
                    return;
                }
            }
            ShowLines(ev);
            return;
        }

        if (!_parsed.TryGetValue(f, out var lines))
        {
            try { lines = LogParser.ParseFile(f.FullPath); _parsed[f] = lines; }
            catch (Exception ex)
            {
                ShowExternal(f, $"Couldn't read {f.Name} ({ex.Message}).");
                return;
            }
        }
        ShowLines(lines);
    }

    private void ShowExternal(BundleFile f, string text)
    {
        _current = new();
        LinesGrid.ItemsSource = null;
        LinesGrid.Visibility = Visibility.Collapsed;
        ExternalPanel.Visibility = Visibility.Visible;
        ExternalText.Text = text;
        DetailBox.Text = f.FullPath;
        CodeHint.Visibility = Visibility.Collapsed;
    }

    private void ShowLines(List<LogLine> lines)
    {
        _current = lines;
        ExternalPanel.Visibility = Visibility.Collapsed;
        LinesGrid.Visibility = Visibility.Visible;

        // Default to errors when a log has any; otherwise show everything.
        var hasErrors = lines.Any(l => l.Level == LogLevel.Error);
        if (hasErrors && lines.Count > 200) LevelErrors.IsChecked = true;
        else LevelAll.IsChecked = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!_loaded) return;
        IEnumerable<LogLine> q = _current;
        if (LevelErrors.IsChecked == true) q = q.Where(l => l.Level == LogLevel.Error);
        else if (LevelWarnings.IsChecked == true) q = q.Where(l => l.Level != LogLevel.Info);

        var text = FilterBox.Text.Trim();
        if (text.Length > 0)
            q = q.Where(l => l.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                             || l.Component.Contains(text, StringComparison.OrdinalIgnoreCase));

        var list = q.ToList();
        LinesGrid.ItemsSource = list;
        if (_currentFile is not null)
            SubtitleText.Text = $"{_currentFile.Name} · showing {list.Count:N0} of {_current.Count:N0} lines · {_bundle?.Folder}";
    }

    private void Level_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void LinesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LinesGrid.SelectedItem is not LogLine line) return;
        DetailBox.Text = $"{line.SourceFile} · line {line.LineNumber} · {line.TimeText} · {line.Component}\n\n{line.Message}";

        var hints = HexCode.Matches(line.Message).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code => (code, text: ErrorCodes.Explain(code)))
            .Where(x => x.text is not null)
            .Select(x => $"{x.code}: {x.text}")
            .ToList();
        CodeHint.Text = string.Join("\n", hints);
        CodeHint.Visibility = hints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEvidence_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Finding { Evidence: { } line } || _bundle is null) return;
        var file = _parsed.Keys.FirstOrDefault(f => _parsed[f].Contains(line));
        if (file is null) return;

        FileList.SelectedItem = file;
        LevelAll.IsChecked = true;
        FilterBox.Text = "";
        ApplyFilter();
        LinesGrid.SelectedItem = line;
        LinesGrid.ScrollIntoView(line);
        LinesGrid.Focus();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_bundle is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_bundle.Folder}\"") { UseShellExecute = true });
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFile is null) return;
        // Opens with the user's default handler, as if double-clicked in Explorer.
        Process.Start(new ProcessStartInfo(_currentFile.FullPath) { UseShellExecute = true });
    }

    private async void OpenZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Open a diagnostics bundle", Filter = "Zip files|*.zip" };
        if (dlg.ShowDialog() != true) return;

        LogBundle? bundle = null;
        var ok = await Shell.Run("Unpacking…", async () =>
        {
            var folder = Path.Combine(SettingsStore.GetDiagnosticsFolder(Session.Current.Settings), "_opened",
                $"{Path.GetFileNameWithoutExtension(dlg.FileName)}-{DateTime.Now:yyyyMMdd-HHmmss}");
            bundle = await Task.Run(() => LogBundle.Extract(dlg.FileName, folder));
        });
        if (!ok || bundle is null) return;

        _bundle = bundle;
        _deviceName = null;
        _parsed = new();
        _eventCache.Clear();
        FindingsList.ItemsSource = null;
        await ShowBundleAsync();
    }
}
