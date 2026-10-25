using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using IntuneConsole.App.Auth;
using IntuneConsole.Core.Datto;
using IntuneConsole.Core.Graph;
using IntuneConsole.Core.Models;
using IntuneConsole.Core.Settings;
using IntuneConsole.Core.Storage;

namespace IntuneConsole.App.Services;

/// <summary>Everything the views share: settings, sign-in, API clients, the device cache and the action log.</summary>
public sealed class Session
{
    public static Session Current { get; } = new();

    // Secret names in Windows Credential Manager
    public const string DattoKeySecret = "DattoApiKey";
    public const string DattoSecretSecret = "DattoApiSecret";
    public const string UploadSasSecret = "FileDropUploadSas";
    public const string ReadSasSecret = "FileDropReadSas";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        // Some Graph APIs (LAPS, BitLocker) require a User-Agent.
        var version = typeof(Session).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"IntuneTroubleshootingConsole/{version}");
        return http;
    }

    public AppSettings Settings { get; private set; } = SettingsStore.Load();
    public MsalAuth? Auth { get; private set; }
    public IntuneService? Intune { get; private set; }
    public DirectoryService? Directory { get; private set; }
    public SecretsService? Secrets { get; private set; }
    public RecordsService? Records { get; private set; }
    public DattoClient? Datto { get; private set; }
    public FileDrop? FileDrop { get; private set; }

    public List<ManagedDevice> Devices { get; private set; } = new();
    public DateTimeOffset? DevicesLoadedAt { get; private set; }
    public string? DattoLoadError { get; private set; }
    private List<DattoComponent>? _components;

    public ObservableCollection<ActionLogEntry> ActionLog { get; } = new();

    private Session()
    {
        LoadActionHistory();
    }

    /// <summary>Loads this admin's recent actions from earlier sessions, so device timelines and
    /// ticket summaries include them.</summary>
    private void LoadActionHistory()
    {
        try
        {
            var path = Path.Combine(SettingsStore.AppDataFolder, "actions.jsonl");
            if (!File.Exists(path)) return;
            var cutoff = DateTimeOffset.Now.AddDays(-30);
            var entries = new List<ActionLogEntry>();
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    var n = System.Text.Json.Nodes.JsonNode.Parse(line);
                    if (n is null || !DateTimeOffset.TryParse(n["when"]?.GetValue<string>(), out var when) || when < cutoff) continue;
                    entries.Add(new ActionLogEntry
                    {
                        When = when,
                        DeviceName = n["device"]?.GetValue<string>() ?? "",
                        Action = n["action"]?.GetValue<string>() ?? "",
                        Via = n["via"]?.GetValue<string>() ?? "",
                        Outcome = n["outcome"]?.GetValue<string>() ?? "",
                        User = n["user"]?.GetValue<string>() ?? "",
                    });
                }
                catch (JsonException) { }
                catch (InvalidOperationException) { }
            }
            // The log appends a line each time an outcome changes; keep the last line per action.
            foreach (var e in entries.GroupBy(e => (e.When, e.DeviceName, e.Action)).Select(g => g.Last())
                         .OrderByDescending(e => e.When).Take(500))
                ActionLog.Add(e);
        }
        catch (IOException) { }
    }

    /// <summary>(Re)creates the API clients from current settings and stored secrets.</summary>
    public void Configure(AppSettings settings)
    {
        Settings = settings;
        _components = null;

        Auth = null;
        Intune = null;
        Directory = null;
        Secrets = null;
        Records = null;
        if (settings.IsSignInConfigured)
        {
            // Called on the UI thread; grab the main window handle now so sign-in prompts have a parent.
            var hwnd = Application.Current?.MainWindow is { } w ? new WindowInteropHelper(w).EnsureHandle() : IntPtr.Zero;
            var auth = new MsalAuth(settings.TenantId.Trim(), settings.ClientId.Trim(), hwnd);
            Auth = auth;
            var graph = new GraphClient(Http, ct => auth.GetTokenAsync(ct));
            Intune = new IntuneService(graph, Http);
            Directory = new DirectoryService(graph);
            Secrets = new SecretsService(graph);
            Records = new RecordsService(graph);
        }

        var key = CredentialStore.Read(DattoKeySecret);
        var secret = CredentialStore.Read(DattoSecretSecret);
        Datto = settings.IsDattoConfigured && !string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(secret)
            ? new DattoClient(Http, settings.DattoApiUrl.Trim(), key, secret)
            : null;

        var upload = CredentialStore.Read(UploadSasSecret);
        var read = CredentialStore.Read(ReadSasSecret);
        FileDrop = settings.IsFileDropConfigured && !string.IsNullOrEmpty(upload) && !string.IsNullOrEmpty(read)
            ? new FileDrop(Http, settings.FileDropContainerUrl.Trim(), upload, read)
            : null;
    }

    public async Task LoadDevicesAsync(CancellationToken ct = default)
    {
        if (Intune is null) throw new InvalidOperationException("Sign-in isn't set up yet. Open Settings first.");

        var intuneTask = Intune.GetWindowsDevicesAsync(ct);
        Task<List<DattoDevice>>? dattoTask = Datto?.GetDevicesAsync(ct);

        var devices = await intuneTask;
        DattoLoadError = null;
        if (dattoTask is not null)
        {
            try { DattoClient.MatchDevices(devices, await dattoTask); }
            catch (Exception ex) when (ex is DattoException or HttpRequestException)
            {
                DattoLoadError = ex.Message;
            }
        }

        Devices = devices;
        DevicesLoadedAt = DateTimeOffset.Now;
    }

    /// <summary>Finds a Datto component's UID by the name set in Settings.</summary>
    public async Task<string> ResolveComponentAsync(string componentName, CancellationToken ct = default)
    {
        if (Datto is null) throw new InvalidOperationException("Datto isn't set up yet. Add your API URL, key and secret in Settings.");
        _components ??= await Datto.GetComponentsAsync(ct);
        var match = _components.FirstOrDefault(c => c.Name.Equals(componentName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _components = null; // re-fetch next time in case it was just imported
            throw new InvalidOperationException(
                $"Datto component \"{componentName}\" wasn't found. Import it from the scripts\\datto folder (see the setup guide), or fix the name in Settings.");
        }
        return match.Uid;
    }

    public async Task<List<DattoComponent>> GetComponentsAsync(CancellationToken ct = default)
    {
        if (Datto is null) return new();
        _components = await Datto.GetComponentsAsync(ct);
        return _components;
    }

    public ActionLogEntry Log(string device, string action, string via, string outcome)
    {
        var entry = new ActionLogEntry { DeviceName = device, Action = action, Via = via, Outcome = outcome };
        ActionLog.Insert(0, entry);
        Persist(entry);
        return entry;
    }

    public void UpdateLog(ActionLogEntry entry, string outcome)
    {
        entry.Outcome = outcome;
        // ObservableCollection doesn't see property changes on the item; re-insert to refresh lists.
        var i = ActionLog.IndexOf(entry);
        if (i >= 0) { ActionLog.RemoveAt(i); ActionLog.Insert(i, entry); }
        Persist(entry);
    }

    /// <summary>Appends to a local log of this admin's actions. Intune's and Datto's own audit logs remain the
    /// authoritative record.</summary>
    private static void Persist(ActionLogEntry e)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                when = e.When, device = e.DeviceName, action = e.Action, via = e.Via, outcome = e.Outcome,
                user = e.User
            });
            File.AppendAllText(Path.Combine(SettingsStore.AppDataFolder, "actions.jsonl"), line + Environment.NewLine);
        }
        catch (IOException) { }
    }
}
