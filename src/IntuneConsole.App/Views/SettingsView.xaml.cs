using System.Windows;
using System.Windows.Controls;
using IntuneConsole.App.Services;
using IntuneConsole.Core.Settings;

namespace IntuneConsole.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(bool firstRun)
    {
        InitializeComponent();
        FirstRunBanner.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;

        var s = Session.Current.Settings;
        TenantBox.Text = s.TenantId;
        ClientBox.Text = s.ClientId;
        DattoUrlBox.Text = s.DattoApiUrl;
        CompRestartBox.Text = s.DattoRestartImeComponent;
        CompCacheBox.Text = s.DattoClearCacheComponent;
        CompResetBox.Text = s.DattoResetAppComponent;
        CompPushBox.Text = s.DattoPushFileComponent;
        DropUrlBox.Text = s.FileDropContainerUrl;
        AssetSiteBox.Text = s.AssetSiteUrl;
        AssetsListBox.Text = s.AssetsListName;
        HistoryListBox.Text = s.AssetHistoryListName;
        OrgNameBox.Text = s.OrganizationName;
        DellIdBox.Text = s.DellClientId;
        DellSavedHint.Text = string.IsNullOrEmpty(CredentialStore.Read(Session.DellSecretSecret))
            ? "Request API access at techdirect.dell.com; Dell issues one client ID and secret for your organization."
            : "A secret is saved. Leave it blank to keep it.";
        StaleDaysBox.Text = s.StaleDays.ToString();
        DiagFolderBox.Text = s.DiagnosticsFolder;

        var hasDatto = !string.IsNullOrEmpty(CredentialStore.Read(Session.DattoKeySecret));
        DattoSavedHint.Text = hasDatto ? "A key and secret are saved. Leave these blank to keep them." : "Generate these under Setup > Users > your user in Datto.";
        var hasDrop = !string.IsNullOrEmpty(CredentialStore.Read(Session.UploadSasSecret));
        DropSavedHint.Text = hasDrop ? "Tokens are saved. Leave these blank to keep them." : "The setup guide shows how to create both tokens.";
    }

    private AppSettings Collect()
    {
        var s = Session.Current.Settings;
        return new AppSettings
        {
            TenantId = TenantBox.Text.Trim(),
            ClientId = ClientBox.Text.Trim(),
            DattoApiUrl = DattoUrlBox.Text.Trim().TrimEnd('/'),
            DattoRestartImeComponent = Or(CompRestartBox.Text, s.DattoRestartImeComponent),
            DattoClearCacheComponent = Or(CompCacheBox.Text, s.DattoClearCacheComponent),
            DattoResetAppComponent = Or(CompResetBox.Text, s.DattoResetAppComponent),
            DattoPushFileComponent = Or(CompPushBox.Text, s.DattoPushFileComponent),
            FileDropContainerUrl = DropUrlBox.Text.Trim().TrimEnd('/'),
            AssetSiteUrl = AssetSiteBox.Text.Trim().TrimEnd('/'),
            AssetsListName = Or(AssetsListBox.Text, "Assets"),
            AssetHistoryListName = Or(HistoryListBox.Text, "Asset History"),
            OrganizationName = OrgNameBox.Text.Trim(),
            DellClientId = DellIdBox.Text.Trim(),
            StaleDays = int.TryParse(StaleDaysBox.Text, out var d) && d is > 0 and < 366 ? d : 7,
            DiagnosticsFolder = DiagFolderBox.Text.Trim(),
        };
    }

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private bool Validate(AppSettings s)
    {
        if (!s.IsSignInConfigured)
        {
            Shell.Status("Enter the tenant ID (or domain) and a valid Application (client) ID.", isError: true);
            return false;
        }
        if (s.DattoApiUrl.Length > 0 && !s.DattoApiUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Shell.Status("The Datto API URL should start with https://", isError: true);
            return false;
        }
        if (s.AssetSiteUrl.Length > 0 && !s.AssetSiteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Shell.Status("The SharePoint site URL should start with https://", isError: true);
            return false;
        }
        if (s.FileDropContainerUrl.Length > 0 &&
            (!s.FileDropContainerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.FileDropContainerUrl.Contains('?')))
        {
            Shell.Status("The container URL should be https://<account>.blob.core.windows.net/<container> with no token on the end.", isError: true);
            return false;
        }
        return true;
    }

    private bool SaveCore()
    {
        var s = Collect();
        if (!Validate(s)) return false;

        try
        {
            if (DattoKeyBox.Password.Length > 0) CredentialStore.Write(Session.DattoKeySecret, DattoKeyBox.Password.Trim());
            if (DattoSecretBox.Password.Length > 0) CredentialStore.Write(Session.DattoSecretSecret, DattoSecretBox.Password.Trim());
            if (UploadSasBox.Password.Length > 0) CredentialStore.Write(Session.UploadSasSecret, UploadSasBox.Password.Trim().TrimStart('?'));
            if (DellSecretBox.Password.Length > 0) CredentialStore.Write(Session.DellSecretSecret, DellSecretBox.Password.Trim());
            if (ReadSasBox.Password.Length > 0) CredentialStore.Write(Session.ReadSasSecret, ReadSasBox.Password.Trim().TrimStart('?'));
            SettingsStore.Save(s);
        }
        catch (Exception ex)
        {
            Shell.Status($"Couldn't save: {ex.Message}", isError: true);
            return false;
        }

        Session.Current.Configure(s);
        (Application.Current.MainWindow as MainWindow)?.ResetViews();
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCore()) return;
        Shell.Status("Settings saved.");
        (Application.Current.MainWindow as MainWindow)?.ShowDevices();
    }

    private async void TestAssets_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCore()) return;
        var s = Session.Current;
        if (s.Assets is null)
        {
            AssetTestResult.Text = "Enter the SharePoint site URL first.";
            return;
        }
        await Shell.Run("Testing the asset register…", async () =>
        {
            var parts = new List<string>();
            try
            {
                await s.LoadAssetsAsync();
                parts.Add($"SharePoint OK: {s.AssetRecords!.Count} asset(s), {s.AssetHistory!.Count} history entries.");
            }
            catch (Exception ex)
            {
                parts.Add($"SharePoint failed: {ex.Message} Check the setup script ran, Sites.Selected has consent, and you're a member of the site.");
            }
            if (s.Dell is null) parts.Add("Dell warranty not set up.");
            else
            {
                try
                {
                    await s.Dell.LookupAsync(["0000000"]);
                    parts.Add("Dell API OK.");
                }
                catch (Exception ex) { parts.Add($"Dell API failed: {ex.Message}"); }
            }
            AssetTestResult.Text = string.Join(" ", parts);
        });
    }

    private async void TestDatto_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveCore()) return;
        if (Session.Current.Datto is null)
        {
            DattoTestResult.Text = "Enter the API URL, key and secret first.";
            return;
        }

        await Shell.Run("Testing Datto…", async () =>
        {
            var components = await Session.Current.GetComponentsAsync();
            var s = Session.Current.Settings;
            var wanted = new[] { s.DattoRestartImeComponent, s.DattoClearCacheComponent, s.DattoResetAppComponent, s.DattoPushFileComponent };
            var missing = wanted.Where(w => !components.Any(c => c.Name.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
            DattoTestResult.Text = missing.Count == 0
                ? $"Connected. Found {components.Count} components, including all four console components."
                : $"Connected ({components.Count} components), but these weren't found: {string.Join(", ", missing)}. Import them from scripts\\datto.";
        });
    }
}
