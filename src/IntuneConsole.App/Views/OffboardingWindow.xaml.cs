using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Diagnostics;
using IntuneConsole.Core.Models;

namespace IntuneConsole.App.Views;

/// <summary>Leaver checklist: every device the person has (Intune and asset register), whether it's
/// back, and the next step for each. Progress comes from live data, so it can be reopened any time.</summary>
public partial class OffboardingWindow : Window
{
    public sealed class Row
    {
        public Offboarding.Item Item { get; init; } = new();
        public string DoneMark => Item.Done ? "✓" : "";
    }

    private static Session S => Session.Current;
    private readonly DirectoryUser _user;
    private List<Offboarding.Item> _items = new();
    private bool _revoked;

    public OffboardingWindow(DirectoryUser user)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _user = user;
        Title = $"Offboarding – {user.DisplayName}";
        HeadingText.Text = $"Offboarding {user.DisplayName}";
        SubText.Text = $"{user.UserPrincipalName} · account {user.AccountText.ToLowerInvariant()}" +
                       (user.AccountEnabled == true ? " · disable the account in Entra or your HR process when they leave" : "");
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var problems = new List<string>();
        List<ManagedDevice> devices = new();
        await Shell.Run($"Gathering {_user.DisplayName}'s devices…", async () =>
        {
            var devTask = S.Intune!.GetUserDevicesAsync(_user.Id);
            var assetTask = S.Assets is not null ? S.LoadAssetsAsync() : Task.CompletedTask;
            try { devices = await devTask; } catch (Exception ex) { problems.Add($"Intune ({ex.Message})"); }
            try { await assetTask; } catch (Exception ex) { problems.Add($"asset register ({ex.Message})"); }
        });

        // Use cached copies (with Datto status) where available.
        var cache = S.Devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        devices = devices.Select(d => cache.TryGetValue(d.Id, out var c) ? c : d).ToList();

        _items = Offboarding.Build(_user, devices, S.AssetRecords);
        ItemsGrid.ItemsSource = _items.Select(i => new Row { Item = i }).ToList();

        var done = _items.Count(i => i.Done);
        ProgressText.Text = _items.Count == 0
            ? "No devices found in Intune or assigned in the asset register."
            : done == _items.Count ? $"All {done} device(s) done."
            : $"{done} of {_items.Count} done · {_items.Count(i => i.Returned)} returned · {_items.Count(i => !i.RemovedFromIntune)} still in Intune";
        var (bg, border) = _items.Count > 0 && done == _items.Count ? ("OkBg", "OkFg") : ("Surface", "Border");
        ProgressBox.Background = (Brush)FindResource(bg);
        ProgressBox.BorderBrush = (Brush)FindResource(border);
        if (S.Assets is null) problems.Add("asset register isn't set up, so only Intune devices are listed");
        if (problems.Count > 0) Shell.Status("Offboarding: " + string.Join("; ", problems), isError: true);
        UpdateButtons();
    }

    private Offboarding.Item? Selected => (ItemsGrid.SelectedItem as Row)?.Item;

    private void ItemsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var i = Selected;
        AssetButton.IsEnabled = i is not null && S.Assets is not null;
        WipeButton.IsEnabled = i?.Device is { IsWindows: true };
        RetireButton.IsEnabled = i?.Device is not null;
        OpenDeviceButton.IsEnabled = i?.Device is not null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (S.Directory is null) return;
        if (!Shell.Confirm("Sign out everywhere", $"Revoke all sign-in sessions for {_user.DisplayName}?\n\nThey'll be signed out of Microsoft 365 apps on every device within minutes.")) return;
        var ok = await Shell.Run("Revoking sessions…", async () => await S.Directory.RevokeSessionsAsync(_user.Id));
        if (!ok) { Shell.Status("Revoking sessions needs User.RevokeSessions.All (and an admin role such as User Administrator).", isError: true); return; }
        _revoked = true;
        S.Log(_user.UserPrincipalName ?? _user.DisplayName, "Revoke sign-in sessions (offboarding)", "Entra", "Done");
        Shell.Status($"Sessions revoked for {_user.DisplayName}.");
    }

    private async void Asset_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } i) return;
        new AssetWindow(new AssetRow { Record = i.Asset, Device = i.Device }).ShowDialog();
        await LoadAsync();
    }

    private async void Wipe_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Device is not { } d || S.Intune is null) return;
        if (!Shell.Confirm("Wipe device", $"Factory reset {d.DeviceName}?\n\nEverything on it is erased. If it's registered in Autopilot it comes back ready for the next person. This can't be undone.")) return;
        var typed = Dialogs.AskReason("Confirm wipe", $"Type the device name ({d.DeviceName}) to confirm.", "Device name", "Wipe");
        if (!string.Equals(typed?.Trim(), d.DeviceName, StringComparison.OrdinalIgnoreCase)) { Shell.Status("Wipe cancelled: the name didn't match."); return; }

        var ok = await Shell.Run($"Sending wipe to {d.DeviceName}…", async () => await S.Intune.WipeAsync(d.Id));
        if (!ok) return;
        S.Log(d.DeviceName, $"Wipe (offboarding {_user.UserPrincipalName})", "Intune", "Sent");
        Shell.Status($"Wipe sent to {d.DeviceName}. It runs at the device's next check-in.");
        await LoadAsync();
    }

    private async void Retire_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Device is not { } d || S.Intune is null) return;
        if (!Shell.Confirm("Retire device", $"Retire {d.DeviceName}?\n\nCompany apps, data and settings are removed and it leaves Intune. Personal data stays.")) return;
        var ok = await Shell.Run($"Retiring {d.DeviceName}…", async () => await S.Intune.RetireAsync(d.Id));
        if (!ok) return;
        S.Log(d.DeviceName, $"Retire (offboarding {_user.UserPrincipalName})", "Intune", "Sent");
        Shell.Status($"Retire sent to {d.DeviceName}.");
        await LoadAsync();
    }

    private void OpenDevice_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Device is not { } d) return;
        Close();
        Shell.Navigate(new DeviceDetailView(d));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(Offboarding.Summary(_user, _items, _revoked, S.Auth?.UserName ?? Environment.UserName, DateTimeOffset.Now));
        Shell.Status("Offboarding summary copied.");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
