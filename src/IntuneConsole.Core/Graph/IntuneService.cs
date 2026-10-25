using System.Text.Json.Nodes;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Graph;

/// <summary>Intune operations used by the console. All calls go to Graph beta, where the
/// device actions, log collection and reports live.</summary>
public sealed class IntuneService
{
    private readonly GraphClient _graph;
    private readonly HttpClient _download;
    private readonly Dictionary<string, string> _appNames = new(StringComparer.OrdinalIgnoreCase);

    public IntuneService(GraphClient graph, HttpClient downloadClient)
    {
        _graph = graph;
        _download = downloadClient;
    }

    private const string DeviceSelect =
        "id,deviceName,userPrincipalName,userDisplayName,osVersion,model,manufacturer,serialNumber," +
        "complianceState,joinType,deviceEnrollmentType,isEncrypted,lastSyncDateTime,azureADDeviceId,operatingSystem," +
        "wiFiMacAddress,userId,enrolledDateTime";

    // ---------- Devices ----------

    public async Task<List<ManagedDevice>> GetWindowsDevicesAsync(CancellationToken ct = default) =>
        (await GetAllDevicesAsync(ct)).Where(d => d.IsWindows).ToList();

    /// <summary>Every Intune-managed device, all platforms, sorted by name.</summary>
    public async Task<List<ManagedDevice>> GetAllDevicesAsync(CancellationToken ct = default)
    {
        var nodes = await _graph.GetAllAsync($"deviceManagement/managedDevices?$select={DeviceSelect}&$top=500", ct: ct);
        return nodes
            .Select(MapDevice)
            .OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ManagedDevice> GetDeviceAsync(string id, CancellationToken ct = default)
    {
        var n = await _graph.GetAsync($"deviceManagement/managedDevices/{id}?$select={DeviceSelect}", ct);
        return MapDevice(n!);
    }

    internal static ManagedDevice MapDevice(JsonNode n) => new()
    {
        Id = n.Str("id") ?? "",
        DeviceName = n.Str("deviceName") ?? "",
        UserPrincipalName = n.Str("userPrincipalName"),
        UserDisplayName = n.Str("userDisplayName"),
        OsVersion = n.Str("osVersion"),
        Model = n.Str("model"),
        Manufacturer = n.Str("manufacturer"),
        SerialNumber = n.Str("serialNumber"),
        ComplianceState = n.Str("complianceState"),
        JoinType = n.Str("joinType"),
        EnrollmentType = n.Str("deviceEnrollmentType"),
        IsEncrypted = n.Bool("isEncrypted"),
        LastSyncDateTime = n.Date("lastSyncDateTime"),
        AzureAdDeviceId = n.Str("azureADDeviceId"),
        OperatingSystem = n.Str("operatingSystem"),
        WiFiMacAddress = n.Str("wiFiMacAddress"),
        UserId = n.Str("userId"),
        EnrolledDateTime = n.Date("enrolledDateTime"),
    };

    /// <summary>All Intune devices (any platform) where this user is the primary user.</summary>
    public async Task<List<ManagedDevice>> GetUserDevicesAsync(string userId, CancellationToken ct = default)
    {
        var filter = Uri.EscapeDataString($"userId eq '{userId}'");
        var nodes = await _graph.GetAllAsync($"deviceManagement/managedDevices?$filter={filter}&$select={DeviceSelect}", ct: ct);
        return nodes.Select(MapDevice).ToList();
    }

    // ---------- Actions ----------

    public Task SyncAsync(string deviceId, CancellationToken ct = default) =>
        _graph.PostAsync($"deviceManagement/managedDevices/{deviceId}/syncDevice", null, ct);

    public Task RebootAsync(string deviceId, CancellationToken ct = default) =>
        _graph.PostAsync($"deviceManagement/managedDevices/{deviceId}/rebootNow", null, ct);

    /// <summary>Runs a Remediation (device health script) on one device, on demand.
    /// Requires the Remediations licensing prerequisites (Windows Enterprise E3/E5 or equivalent).</summary>
    public Task RunRemediationAsync(string deviceId, string scriptPolicyId, CancellationToken ct = default) =>
        _graph.PostAsync($"deviceManagement/managedDevices/{deviceId}/initiateOnDemandProactiveRemediation",
            new { scriptPolicyId }, ct);

    public async Task<List<RemediationScript>> GetRemediationsAsync(CancellationToken ct = default)
    {
        var nodes = await _graph.GetAllAsync("deviceManagement/deviceHealthScripts?$select=id,displayName", ct: ct);
        return nodes.Select(n => new RemediationScript { Id = n.Str("id") ?? "", DisplayName = n.Str("displayName") ?? "" })
                    .Where(s => s.Id != "")
                    .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    // ---------- Diagnostics (log collection) ----------

    public async Task<LogCollectionRequest> RequestDiagnosticsAsync(string deviceId, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["templateType"] = new JsonObject
            {
                ["@odata.type"] = "microsoft.graph.deviceLogCollectionRequest",
                ["templateType"] = "predefined"
            }
        };
        var resp = await _graph.PostAsync($"deviceManagement/managedDevices/{deviceId}/createDeviceLogCollectionRequest", body, ct);
        var v = resp?["value"] ?? resp;
        return MapLogRequest(v!);
    }

    public async Task<List<LogCollectionRequest>> GetDiagnosticsAsync(string deviceId, CancellationToken ct = default)
    {
        var nodes = await _graph.GetAllAsync($"deviceManagement/managedDevices/{deviceId}/logCollectionRequests", ct: ct);
        return nodes.Select(MapLogRequest)
                    .OrderByDescending(r => r.RequestedUtc ?? DateTimeOffset.MinValue)
                    .ToList();
    }

    /// <summary>Downloads a completed diagnostics bundle (zip) to the given path.</summary>
    public async Task DownloadDiagnosticsAsync(string deviceId, string requestId, string zipPath, CancellationToken ct = default)
    {
        var resp = await _graph.PostAsync(
            $"deviceManagement/managedDevices/{deviceId}/logCollectionRequests/{requestId}/createDownloadUrl", null, ct);
        var url = resp.Str("value") ?? throw new InvalidOperationException("Intune did not return a download link.");

        // The link is a pre-signed storage URL: no Graph token is sent with it.
        using var stream = await _download.GetStreamAsync(url, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        await using var file = File.Create(zipPath);
        await stream.CopyToAsync(file, ct);
    }

    private static LogCollectionRequest MapLogRequest(JsonNode n) => new()
    {
        Id = n.Str("id") ?? "",
        Status = n.Str("status") ?? "",
        RequestedUtc = n.Date("requestedDateTimeUTC"),
        ReceivedUtc = n.Date("receivedDateTimeUTC"),
        ExpiresUtc = n.Date("expirationDateTimeUTC"),
        SizeInKb = n.Num("sizeInKB"),
        InitiatedBy = n.Str("initiatedByUserPrincipalName"),
    };

    // ---------- Apps & policies ----------

    public async Task<List<AppInstallStatus>> GetAppStatusAsync(string deviceId, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["filter"] = $"(DeviceId eq '{deviceId}')",
            ["top"] = 500,
            ["skip"] = 0,
            ["orderBy"] = new JsonArray(),
        };
        var resp = await _graph.PostAsync("deviceManagement/reports/retrieveDeviceAppInstallationStatusReport", body, ct);
        var rows = ReportTable.Parse(resp);

        var list = rows.Select(r => new AppInstallStatus
        {
            ApplicationId = r.Get("ApplicationId", "AppId") ?? "",
            AppName = r.Get("AppName", "ApplicationName", "DisplayName", "Name") ?? "",
            AppVersion = r.Get("AppVersion", "Version"),
            InstallState = r.Get("AppInstallState_loc", "InstallState_loc", "AppInstallState", "InstallState") ?? "",
            InstallStateDetail = r.Get("AppInstallStateDetails_loc", "InstallStateDetail_loc", "AppInstallStateDetails", "InstallStateDetail"),
            HexErrorCode = r.Get("HexErrorCode"),
            ErrorCode = long.TryParse(r.Get("ErrorCode"), out var ec) ? ec : null,
            LastModified = DateTimeOffset.TryParse(r.Get("LastModifiedDateTime"), out var lm) ? lm : null,
        }).ToList();

        // Fill in app names the report didn't include.
        foreach (var app in list.Where(a => a.AppName == "" && a.ApplicationId != ""))
            app.AppName = await GetAppNameAsync(app.ApplicationId, ct);

        return list.OrderByDescending(a => a.IsFailed).ThenByDescending(a => a.IsPending).ThenBy(a => a.AppName).ToList();
    }

    private async Task<string> GetAppNameAsync(string appId, CancellationToken ct)
    {
        if (_appNames.TryGetValue(appId, out var name)) return name;
        try
        {
            var n = await _graph.GetAsync($"deviceAppManagement/mobileApps/{appId}?$select=displayName", ct);
            name = n.Str("displayName") ?? appId;
        }
        catch (GraphException) { name = appId; }
        _appNames[appId] = name;
        return name;
    }

    public async Task<List<PolicyState>> GetPolicyStatesAsync(string deviceId, CancellationToken ct = default)
    {
        var result = new List<PolicyState>();
        try
        {
            var cfg = await _graph.GetAllAsync($"deviceManagement/managedDevices/{deviceId}/deviceConfigurationStates", ct: ct);
            result.AddRange(cfg.Select(n => new PolicyState
            {
                Kind = "Configuration",
                DisplayName = n.Str("displayName") ?? "",
                State = n.Str("state") ?? ""
            }));
        }
        catch (GraphException) { /* not available for every device type; ignore */ }

        try
        {
            var comp = await _graph.GetAllAsync($"deviceManagement/managedDevices/{deviceId}/deviceCompliancePolicyStates", ct: ct);
            result.AddRange(comp.Select(n => new PolicyState
            {
                Kind = "Compliance",
                DisplayName = n.Str("displayName") ?? "",
                State = n.Str("state") ?? ""
            }));
        }
        catch (GraphException) { }

        return result.OrderByDescending(p => p.IsProblem).ThenBy(p => p.DisplayName).ToList();
    }
}

/// <summary>Parses the Schema/Values table format that Intune report endpoints return.</summary>
public static class ReportTable
{
    public sealed class Row
    {
        private readonly Dictionary<string, string?> _cells;
        public Row(Dictionary<string, string?> cells) => _cells = cells;

        public string? Get(params string[] names)
        {
            foreach (var n in names)
                if (_cells.TryGetValue(n, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return null;
        }
    }

    public static List<Row> Parse(JsonNode? report)
    {
        var rows = new List<Row>();
        if (report?["Schema"] is not JsonArray schema || report["Values"] is not JsonArray values) return rows;

        var cols = schema.Select(c => c?["Column"]?.GetValue<string>() ?? "").ToList();
        foreach (var v in values)
        {
            if (v is not JsonArray cells) continue;
            var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < cols.Count && i < cells.Count; i++)
            {
                var cell = cells[i];
                dict[cols[i]] = cell is null ? null
                    : cell is JsonValue jv && jv.TryGetValue<string>(out var s) ? s
                    : cell.ToJsonString();
            }
            rows.Add(new Row(dict));
        }
        return rows;
    }
}
