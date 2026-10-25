using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Diagnostics;
using IntuneConsole.Core.Models;

namespace IntuneConsole.App.Views;

/// <summary>Runs one action on many devices with a live result per device.</summary>
public partial class BulkActionWindow : Window
{
    public sealed class Item : INotifyPropertyChanged
    {
        private string _state = "Waiting";
        private string _message = "";
        public ManagedDevice Device { get; init; } = new();
        public string State { get => _state; set { _state = value; Raise(); } }
        public string Message { get => _message; set { _message = value; Raise(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private sealed record ActionDef(string Name, string Hint, bool NeedsDatto, bool NeedsRemediation, bool Destructive,
        Func<Item, CancellationToken, Task<string>> Run)
    {
        public override string ToString() => Name;
    }

    private static Session S => Session.Current;
    private List<Item> _items = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public BulkActionWindow(IEnumerable<ManagedDevice> selected)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        ActionCombo.ItemsSource = BuildActions();
        ActionCombo.SelectedIndex = 0;
        TargetsBox.Text = string.Join(Environment.NewLine, selected.Select(d => d.DeviceName));
        Loaded += async (_, _) => await LoadRemediationsAsync();
        Resolve();
    }

    private List<ActionDef> BuildActions() =>
    [
        new("Sync with Intune", "Each device checks in at its next opportunity.", false, false, false, async (i, ct) =>
        {
            await S.Intune!.SyncAsync(i.Device.Id, ct);
            return "Sync sent";
        }),
        new("Collect diagnostics", "Each device uploads a diagnostics bundle at its next check-in. Open them from each device page.", false, false, false, async (i, ct) =>
        {
            await S.Intune!.RequestDiagnosticsAsync(i.Device.Id, ct);
            try { await S.Intune.SyncAsync(i.Device.Id, ct); } catch { }
            return "Requested";
        }),
        new("Run Intune Remediation", "Runs the chosen Remediation on each device on demand.", false, true, false, async (i, ct) =>
        {
            var script = (RemediationScript?)Dispatcher.Invoke(() => RemediationCombo.SelectedItem)
                         ?? throw new InvalidOperationException("Pick a Remediation.");
            await S.Intune!.RunRemediationAsync(i.Device.Id, script.Id, ct);
            return $"\"{script.DisplayName}\" requested";
        }),
        new("Restart IME service (Datto)", "Runs the Restart IME component on each device that's in Datto.", true, false, false,
            (i, ct) => RunDattoAsync(i, S.Settings.DattoRestartImeComponent, "Restart IME", ct)),
        new("Clear IME download cache (Datto)", "Runs the Clear IME Download Cache component on each device that's in Datto.", true, false, false,
            (i, ct) => RunDattoAsync(i, S.Settings.DattoClearCacheComponent, "Clear IME cache", ct)),
        new("Restart devices", "Restarts each device at its next check-in. Users may lose unsaved work.", false, false, true, async (i, ct) =>
        {
            await S.Intune!.RebootAsync(i.Device.Id, ct);
            return "Restart sent";
        }),
    ];

    private static async Task<string> RunDattoAsync(Item i, string component, string label, CancellationToken ct)
    {
        if (i.Device.Datto is null) throw new InvalidOperationException("Not in Datto");
        var uid = await S.ResolveComponentAsync(component, ct);
        var job = await S.Datto!.RunQuickJobAsync(i.Device.Datto.Uid, $"{label} – {i.Device.DeviceName} (Intune Console bulk)", uid, null, ct);
        return i.Device.Datto.Online ? $"Datto job started ({job})" : $"Queued, agent offline ({job})";
    }

    private async Task LoadRemediationsAsync()
    {
        if (S.Intune is null) return;
        try { RemediationCombo.ItemsSource = await S.Intune.GetRemediationsAsync(); RemediationCombo.SelectedIndex = 0; }
        catch (Exception ex) { ActionHint.Text = $"Couldn't list Remediations: {ex.Message}"; }
    }

    private void TargetsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_running && IsLoaded) Resolve();
    }

    private void Resolve()
    {
        var res = Bulk.ResolveTargets(TargetsBox.Text, S.Devices);
        _items = res.Matched.Select(d => new Item { Device = d }).ToList();
        ResultsGrid.ItemsSource = _items;
        MatchText.Text = _items.Count == 0 ? "No devices yet." : $"{_items.Count} device(s) matched";
        UnmatchedText.Text = res.Unmatched.Count == 0 ? "" : $"Not found ({res.Unmatched.Count}): {string.Join(", ", res.Unmatched.Take(20))}{(res.Unmatched.Count > 20 ? "…" : "")}";
        UnmatchedText.Visibility = res.Unmatched.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ProgressText.Text = "";
        UpdateHint();
    }

    private void ActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHint();

    private void UpdateHint()
    {
        if (ActionCombo.SelectedItem is not ActionDef a) return;
        RemediationPanel.Visibility = a.NeedsRemediation ? Visibility.Visible : Visibility.Collapsed;
        var hint = a.Hint;
        if (a.NeedsDatto)
        {
            var missing = _items.Count(i => i.Device.Datto is null);
            hint += S.Datto is null ? " Datto isn't set up." : missing > 0 ? $" {missing} selected device(s) aren't in Datto and will be skipped." : "";
        }
        ActionHint.Text = hint;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (ActionCombo.SelectedItem is not ActionDef action || _items.Count == 0) return;
        if (action.NeedsDatto && S.Datto is null) { ActionHint.Text = "Datto isn't set up. Add it in Settings."; return; }
        if (action.NeedsRemediation && RemediationCombo.SelectedItem is null) { ActionHint.Text = "Pick a Remediation."; return; }

        var confirm = action.Destructive
            ? $"{action.Name} on {_items.Count} device(s)?\n\nThis can't be undone and users may lose unsaved work."
            : $"{action.Name} on {_items.Count} device(s)?";
        if (MessageBox.Show(this, confirm, "Bulk action", MessageBoxButton.YesNo,
                action.Destructive ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        if (action.Destructive && _items.Count > 5 &&
            MessageBox.Show(this, $"Really restart all {_items.Count} devices?", "Confirm again", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        foreach (var i in _items) { i.State = "Waiting"; i.Message = ""; }
        _running = true;
        _cts = new CancellationTokenSource();
        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        TargetsBox.IsReadOnly = true;
        ActionCombo.IsEnabled = false;

        var done = 0;
        var ok = 0;
        // Datto allows about 100 writes a minute for the whole account; stay well under it.
        var parallel = action.NeedsDatto ? 2 : 4;
        var targets = action.NeedsDatto ? _items.Where(i => i.Device.Datto is not null).ToList() : _items;
        foreach (var skipped in _items.Except(targets)) { skipped.State = "Skipped"; skipped.Message = "Not in Datto"; }

        await Bulk.RunAsync(targets, async (item, ct) =>
        {
            Dispatcher.Invoke(() => item.State = "Running");
            return await action.Run(item, ct);
        }, (item, success, message) => Dispatcher.Invoke(() =>
        {
            item.State = success ? "Succeeded" : message == "Cancelled" ? "Cancelled" : "Failed";
            item.Message = message;
            done++;
            if (success) ok++;
            ProgressText.Text = $"{done} of {targets.Count} done · {ok} succeeded";
            S.Log(item.Device.DeviceName, $"{action.Name} (bulk)", action.NeedsDatto ? "Datto" : "Intune", success ? message : $"Failed: {message}");
        }), parallel, _cts.Token);

        _running = false;
        RunButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        TargetsBox.IsReadOnly = false;
        ActionCombo.IsEnabled = true;
        Shell.Status($"Bulk {action.Name.ToLowerInvariant()}: {ok} of {targets.Count} succeeded.", isError: ok < targets.Count);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        if (MessageBox.Show(this, "Stop the remaining devices and close?", "Bulk action", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _cts?.Cancel();
        else
            e.Cancel = true;
    }
}
