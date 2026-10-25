using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IntuneConsole.App.Converters;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Graph;
using IntuneConsole.Core.Models;

namespace IntuneConsole.App.Views;

/// <summary>One person: who they are, their devices, and their recent sign-ins.</summary>
public partial class UserView : UserControl
{
    private DirectoryUser _user;
    private List<SignInEvent> _signIns = new();
    private bool _loaded;

    private static Session S => Session.Current;

    public UserView(DirectoryUser user)
    {
        InitializeComponent();
        _user = user;
        RenderHeader();
    }

    private void RenderHeader()
    {
        var u = _user;
        CrumbText.Text = $"/ {u.DisplayName}";
        NameText.Text = u.DisplayName;
        InitialsText.Text = Initials(u.DisplayName);
        TitleText.Text = string.Join(" · ", new[] { u.JobTitle, u.Department }.Where(s => !string.IsNullOrWhiteSpace(s)));

        AccountPill.Tag = u.AccountText;
        AccountText.Text = $"Account {u.AccountText.ToLowerInvariant()}";
        AccountText.Foreground = (System.Windows.Media.Brush)new PillForegroundConverter()
            .Convert(AccountPill.Tag, typeof(object), null, System.Globalization.CultureInfo.CurrentCulture);

        InfoPanel.Children.Clear();
        AddInfo("UPN", u.UserPrincipalName);
        AddInfo("Email", u.Mail);
        AddInfo("Employee ID", u.EmployeeId);
        AddInfo("Office", u.OfficeLocation);
        AddInfo("Mobile", u.MobilePhone);
        AddInfo("Manager", u.ManagerName);
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    private void AddInfo(string label, string? value)
    {
        var panel = new StackPanel { Width = 240, Margin = new Thickness(0, 0, 16, 14) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        panel.Children.Add(new TextBox
        {
            // Read-only text box so values (UPN, employee ID) can be selected and copied.
            Text = string.IsNullOrWhiteSpace(value) ? "–" : value,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
        InfoPanel.Children.Add(panel);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        await Shell.Run($"Loading {_user.DisplayName}…", LoadAsync);
    }

    private async Task LoadAsync()
    {
        var dir = S.Directory ?? throw new InvalidOperationException("Sign-in isn't set up yet.");
        var intune = S.Intune!;

        var userTask = dir.GetUserAsync(_user.Id);
        var devicesTask = intune.GetUserDevicesAsync(_user.Id);
        var signInTask = dir.GetSignInsAsync(_user.Id);
        var cacheTask = S.Devices.Count == 0 ? S.LoadDevicesAsync() : Task.CompletedTask;

        var problems = new List<string>();

        try { _user = await userTask; RenderHeader(); }
        catch (Exception ex) { problems.Add($"profile ({ex.Message})"); }

        List<ManagedDevice> primary = new();
        try { primary = await devicesTask; }
        catch (Exception ex) { problems.Add($"devices ({ex.Message})"); }

        try
        {
            _signIns = await signInTask;
            SignInNote.Visibility = Visibility.Collapsed;
        }
        catch (GraphException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            _signIns = new();
            SignInNote.Text = "Sign-ins need the AuditLog.Read.All permission on the app registration, and the Reports Reader " +
                              "(or Global Reader / Security Reader) role in Entra for your account.";
            SignInNote.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _signIns = new();
            SignInNote.Text = $"Couldn't load sign-ins: {ex.Message}";
            SignInNote.Visibility = Visibility.Visible;
        }

        try { await cacheTask; } catch { /* device cache is only used to enrich the list */ }

        // Reuse cached devices where possible so Datto status shows; fall back to what the user query returned.
        var cache = S.Devices.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        var primaryEnriched = primary.Select(d => cache.TryGetValue(d.Id, out var c) ? c : d).ToList();
        var links = DirectoryService.LinkDevices(primaryEnriched, _signIns, S.Devices);

        DevicesGrid.ItemsSource = links;
        DevicesGrid.Visibility = links.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DevicesEmpty.Visibility = links.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var nonCompliant = links.Count(l => l.Device.IsNoncompliant);
        DevicesSummary.Text = links.Count == 0 ? "" : $"{links.Count} device(s){(nonCompliant > 0 ? $" · {nonCompliant} noncompliant" : "")}";

        ShowSignIns();

        if (problems.Count > 0) Shell.Status("Some sections didn't load: " + string.Join("; ", problems), isError: true);
        else Shell.Status($"{_user.DisplayName} loaded.");
    }

    private void ShowSignIns()
    {
        var list = FailuresOnly.IsChecked == true ? _signIns.Where(s => !s.Succeeded).ToList() : _signIns;
        SignInGrid.ItemsSource = list;
        var failed = _signIns.Count(s => !s.Succeeded);
        SignInSummary.Text = _signIns.Count == 0 ? "Last 14 days · none found"
            : $"Last 14 days · {_signIns.Count} sign-ins · {failed} failed";
    }

    private void FailuresOnly_Changed(object sender, RoutedEventArgs e) { if (_loaded) ShowSignIns(); }

    private void DevicesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void DevicesGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (DevicesGrid.SelectedItem is UserDeviceLink link) Shell.Navigate(new DeviceDetailView(link.Device));
    }

    private void Offboard_Click(object sender, RoutedEventArgs e) => new OffboardingWindow(_user).ShowDialog();

    private void OpenDevice_Menu(object sender, RoutedEventArgs e) => OpenSelected();

    private void Remote_Menu(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is UserDeviceLink link) RemoteHelper.Open(link.Device);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => (Application.Current.MainWindow as MainWindow)?.ShowDevices();

    private void CopyUpn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_user.UserPrincipalName)) return;
        Clipboard.SetText(_user.UserPrincipalName);
        Shell.Status($"Copied {_user.UserPrincipalName}");
    }

    private void OpenEntra_Click(object sender, RoutedEventArgs e)
    {
        if (!Guid.TryParse(_user.Id, out var id)) return;
        var url = $"https://entra.microsoft.com/#view/Microsoft_AAD_UsersAndTenants/UserProfileMenuBlade/~/overview/userId/{id}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
