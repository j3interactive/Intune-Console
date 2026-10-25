using System.Text.Json;

namespace IntuneConsole.Core.Settings;

/// <summary>Non-secret settings, stored per admin in %LOCALAPPDATA%\IntuneConsole\settings.json.
/// Secrets (Datto key/secret, storage upload token) live in Windows Credential Manager instead.</summary>
public sealed class AppSettings
{
    // Entra app registration
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";

    // Datto RMM
    public string DattoApiUrl { get; set; } = "";          // e.g. https://merlot-api.centrastage.net
    public string DattoRestartImeComponent { get; set; } = "Intune Console - Restart IME";
    public string DattoClearCacheComponent { get; set; } = "Intune Console - Clear IME Download Cache";
    public string DattoResetAppComponent { get; set; } = "Intune Console - Reset Win32 App";
    public string DattoPushFileComponent { get; set; } = "Intune Console - Push File";

    // File drop (Azure Blob container used to hand files to the Datto push component)
    public string FileDropContainerUrl { get; set; } = "";   // https://<account>.blob.core.windows.net/<container>

    // Asset register (SharePoint) and Dell warranty
    public string AssetSiteUrl { get; set; } = "";           // https://contoso.sharepoint.com/sites/IT
    public string AssetsListName { get; set; } = "Assets";
    public string AssetHistoryListName { get; set; } = "Asset History";
    public string DellClientId { get; set; } = "";           // secret lives in Credential Manager
    public string OrganizationName { get; set; } = "";       // printed on handover sheets

    // Diagnostics
    public int StaleDays { get; set; } = 7;
    public string DiagnosticsFolder { get; set; } = "";      // default: %LOCALAPPDATA%\IntuneConsole\Diagnostics

    /// <summary>Tenant can be the GUID or a domain like contoso.onmicrosoft.com.</summary>
    public bool IsSignInConfigured => !string.IsNullOrWhiteSpace(TenantId) && Guid.TryParse(ClientId, out _);
    public bool IsDattoConfigured => !string.IsNullOrWhiteSpace(DattoApiUrl);
    public bool IsAssetRegisterConfigured => AssetSiteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public bool IsFileDropConfigured => FileDropContainerUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string AppDataFolder
    {
        get
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IntuneConsole");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    private static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    /// <summary>Loads per-user settings. If none exist yet, falls back to a defaults.json shipped next to
    /// the exe (lets you pre-fill tenant ID, client ID and Datto URL for every admin at install time).</summary>
    public static AppSettings Load()
    {
        foreach (var path in new[] { SettingsPath, Path.Combine(AppContext.BaseDirectory, "defaults.json") })
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
            }
            catch (JsonException) { }
        }
        return new AppSettings();
    }

    public static void Save(AppSettings s) => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Json));

    public static string GetDiagnosticsFolder(AppSettings s)
    {
        var p = string.IsNullOrWhiteSpace(s.DiagnosticsFolder) ? Path.Combine(AppDataFolder, "Diagnostics") : s.DiagnosticsFolder;
        Directory.CreateDirectory(p);
        return p;
    }
}
