using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Assets;
using IntuneConsole.Core.Models;
using IntuneConsole.Core.Settings;

namespace IntuneConsole.App.Views;

/// <summary>View and edit one asset: fields, live Intune data, history, check-out/in and handover sheet.
/// DialogResult is true when something was saved.</summary>
public partial class AssetWindow : Window
{
    private static Session S => Session.Current;
    private readonly ManagedDevice? _device;
    private AssetRecord? _saved;          // what's in SharePoint (null = not saved yet)
    private bool _changed;

    public AssetWindow(AssetRow row)
    {
        InitializeComponent();
        Owner = Application.Current.MainWindow;
        _device = row.Device;
        _saved = row.Record;
        StatusCombo.ItemsSource = AssetStatus.All;

        var start = row.Record?.Clone() ?? (row.Device is not null ? AssetRegister.NewRecordFor(row.Device) : new AssetRecord { Status = AssetStatus.InStock });
        Fill(start);
        RenderLive();
        RenderHistory();
        RenderHeader();
    }

    private void RenderHeader()
    {
        var serial = SerialBox.Text.Trim();
        Title = serial.Length > 0 ? $"Asset {serial}" : "New asset";
        HeadingText.Text = _saved is null ? (serial.Length > 0 ? $"{serial} · not in register yet" : "New asset") : $"{serial}{(string.IsNullOrEmpty(_saved.AssetTag) ? "" : $" · {_saved.AssetTag}")}";
        SubText.Text = _saved is null ? "Fill in what you know and Save to add it to the register." : $"{_saved.Status}{(string.IsNullOrEmpty(_saved.AssignedName) ? "" : $" · {_saved.AssignedName}")}";
        SerialBox.IsReadOnly = _saved is not null;     // the serial is the key once saved
        CheckOutButton.IsEnabled = CheckInButton.IsEnabled = PrintButton.IsEnabled = _saved is not null;
        CheckInButton.IsEnabled = _saved is not null && !string.IsNullOrWhiteSpace(_saved.AssignedTo);
        OpenDeviceButton.IsEnabled = _device is not null;
        WarrantyButton.IsEnabled = S.Dell is not null;
    }

    private void Fill(AssetRecord r)
    {
        SerialBox.Text = r.SerialNumber;
        TagBox.Text = r.AssetTag ?? "";
        StatusCombo.SelectedItem = AssetStatus.All.Contains(r.Status) ? r.Status : AssetStatus.InUse;
        AssignedToBox.Text = r.AssignedTo ?? "";
        AssignedNameBox.Text = r.AssignedName ?? "";
        LocationBox.Text = r.Location ?? "";
        CostCenterBox.Text = r.CostCenter ?? "";
        ModelBox.Text = r.Model ?? "";
        PurchaseDatePicker.SelectedDate = r.PurchaseDate?.LocalDateTime.Date;
        CostBox.Text = r.Cost?.ToString("0.00", CultureInfo.CurrentCulture) ?? "";
        SupplierBox.Text = r.Supplier ?? "";
        PoBox.Text = r.PONumber ?? "";
        NotesBox.Text = r.Notes ?? "";
        _pending = r;
        RenderWarranty(r);
    }

    // Holds fields the form doesn't edit directly (warranty, ship date, device name, item ID).
    private AssetRecord _pending = new();

    private void RenderWarranty(AssetRecord r)
    {
        WarrantyText.Text = r.WarrantyEnd is null
            ? (S.Dell is null ? "Unknown (Dell API not set up)" : "Unknown")
            : $"{(r.WarrantyEnd < DateTimeOffset.UtcNow ? "Expired " : "Until ")}{r.WarrantyEnd.Value.ToLocalTime():yyyy-MM-dd}" +
              (string.IsNullOrEmpty(r.WarrantyLevel) ? "" : $" · {r.WarrantyLevel}") +
              (r.ShipDate is null ? "" : $"\nShipped {r.ShipDate.Value.ToLocalTime():yyyy-MM-dd}");
    }

    private AssetRecord? Collect()
    {
        MessageText.Text = "";
        var serial = AssetRegister.Key(SerialBox.Text);
        if (serial.Length < 4) { MessageText.Text = "Enter the serial number (Dell service tag)."; return null; }
        if (_saved is null && S.FindAsset(serial) is not null) { MessageText.Text = $"{serial} is already in the register."; return null; }

        double? cost = null;
        if (CostBox.Text.Trim().Length > 0)
        {
            var cleaned = CostBox.Text.Replace("$", "").Replace("£", "").Replace("€", "").Trim();
            if (!double.TryParse(cleaned, NumberStyles.Any, CultureInfo.CurrentCulture, out var c)) { MessageText.Text = "Cost should be a number."; return null; }
            cost = c;
        }

        var r = _pending.Clone();
        r.SerialNumber = serial;
        r.AssetTag = Blank(TagBox.Text);
        r.Status = StatusCombo.SelectedItem as string ?? AssetStatus.InUse;
        r.AssignedTo = Blank(AssignedToBox.Text);
        r.AssignedName = Blank(AssignedNameBox.Text);
        r.Location = Blank(LocationBox.Text);
        r.CostCenter = Blank(CostCenterBox.Text);
        r.Model = Blank(ModelBox.Text);
        r.PurchaseDate = PurchaseDatePicker.SelectedDate is { } d ? new DateTimeOffset(d) : null;
        r.Cost = cost;
        r.Supplier = Blank(SupplierBox.Text);
        r.PONumber = Blank(PoBox.Text);
        r.Notes = Blank(NotesBox.Text);
        if (_device is not null) r.DeviceName = _device.DeviceName;
        return r;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private async Task<bool> SaveAsync(AssetRecord after, string? notes = null, string? eventOverride = null)
    {
        SaveButton.IsEnabled = false;
        try
        {
            await S.SaveAssetAsync(_saved, after, notes, eventOverride);
            _saved = after.Clone();
            _pending = after.Clone();
            _changed = true;
            RenderHeader();
            RenderHistory();
            return true;
        }
        catch (Exception ex)
        {
            MessageText.Text = $"Couldn't save: {ex.Message}";
            return false;
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var r = Collect();
        if (r is null) return;
        if (_saved is not null && AssetRegister.Changes(_saved, r, "").Count == 0 && r.Model == _saved.Model && r.DeviceName == _saved.DeviceName)
        {
            MessageText.Text = "Nothing changed.";
            return;
        }
        if (await SaveAsync(r)) Shell.Status($"Saved {r.SerialNumber}.");
    }

    private async void Warranty_Click(object sender, RoutedEventArgs e)
    {
        if (S.Dell is null) return;
        var serial = AssetRegister.Key(SerialBox.Text);
        WarrantyButton.IsEnabled = false;
        try
        {
            var w = (await S.Dell.LookupAsync([serial])).FirstOrDefault();
            if (w is null || w.Invalid) { WarrantyText.Text = "Dell doesn't recognise this service tag."; return; }
            AssetRegister.ApplyWarranty(_pending, w, DateTimeOffset.UtcNow);
            if (string.IsNullOrWhiteSpace(ModelBox.Text) && !string.IsNullOrWhiteSpace(_pending.Model)) ModelBox.Text = _pending.Model;
            RenderWarranty(_pending);
            MessageText.Text = _saved is null ? "" : "Warranty updated; click Save to keep it.";
        }
        catch (Exception ex) { WarrantyText.Text = $"Lookup failed: {ex.Message}"; }
        finally { WarrantyButton.IsEnabled = true; }
    }

    private void RenderLive()
    {
        if (_device is null) { LiveText.Text = "Not in Intune. Spares, devices in repair and retired devices appear here."; return; }
        var d = _device;
        LiveText.Text = $"{d.DeviceName} · {d.Model}\nPrimary user: {d.UserDisplayName ?? "none"}{(string.IsNullOrEmpty(d.UserPrincipalName) ? "" : $" ({d.UserPrincipalName})")}\n" +
                        $"Last check-in: {d.LastSyncText} · {d.ComplianceText}\nDatto: {d.DattoStatusText}";
    }

    private void RenderHistory()
    {
        var serial = AssetRegister.Key(SerialBox.Text);
        HistoryList.ItemsSource = (S.AssetHistory ?? new()).Where(h => AssetRegister.Key(h.SerialNumber) == serial).OrderByDescending(h => h.When).ToList();
    }

    // ---------- Check out / check in ----------

    private async void CheckOut_Click(object sender, RoutedEventArgs e)
    {
        if (_saved is null || S.Directory is null) return;
        var pick = AssetDialogs.PickPerson(this, S.Directory, _saved.Status == AssetStatus.Loaner ? AssetStatus.Loaner : AssetStatus.InUse);
        if (pick is null) return;

        var after = (Collect() ?? _saved).Clone();
        after.AssignedTo = pick.User.UserPrincipalName;
        after.AssignedName = pick.User.DisplayName;
        after.Status = pick.Status;
        if (!string.IsNullOrWhiteSpace(pick.Location)) after.Location = pick.Location;
        Fill(after);
        if (!await SaveAsync(after, pick.Notes, "Checked out")) return;
        Shell.Status($"{after.SerialNumber} checked out to {pick.User.DisplayName}.");
        if (_device is not null && !string.Equals(_device.UserPrincipalName, after.AssignedTo, StringComparison.OrdinalIgnoreCase))
            MessageText.Text = "Remember to change the Intune primary user too, if this is now their main PC.";
        if (pick.Print) Print("Check-out", after, pick.User.DisplayName, pick.User.UserPrincipalName, pick.Accessories);
    }

    private async void CheckIn_Click(object sender, RoutedEventArgs e)
    {
        if (_saved is null) return;
        var res = AssetDialogs.CheckIn(this, _saved.AssignedName ?? _saved.AssignedTo ?? "");
        if (res is null) return;

        var personName = _saved.AssignedName;
        var personUpn = _saved.AssignedTo;
        var after = (Collect() ?? _saved).Clone();
        after.AssignedTo = null;
        after.AssignedName = null;
        after.Status = res.Status;
        Fill(after);
        if (!await SaveAsync(after, res.Notes, "Checked in")) return;
        Shell.Status($"{after.SerialNumber} checked in as {res.Status}.");
        if (res.Print) Print("Check-in", after, personName, personUpn, res.Accessories);
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_saved is null) return;
        Print(string.IsNullOrWhiteSpace(_saved.AssignedTo) ? "Check-in" : "Check-out", _saved, _saved.AssignedName, _saved.AssignedTo, null);
    }

    private static void Print(string kind, AssetRecord r, string? name, string? upn, string? accessories)
    {
        var html = AssetRegister.HandoverSheet(r, kind, name, upn, S.Auth?.UserName ?? Environment.UserName, accessories,
            DateTimeOffset.Now, S.Settings.OrganizationName);
        var folder = Path.Combine(SettingsStore.AppDataFolder, "Handover");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{kind}-{AssetRegister.Key(r.SerialNumber)}-{DateTime.Now:yyyyMMdd-HHmm}.html");
        File.WriteAllText(path, html);
        // Opens in the default browser; the page has a Print button.
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_device is null) return;
        DialogResult = _changed;
        Shell.Navigate(new DeviceDetailView(_device));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = _changed;
}
