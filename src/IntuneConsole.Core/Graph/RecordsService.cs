using System.Text.Json.Nodes;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Graph;

/// <summary>Entra ID device records, Autopilot registrations and Intune audit events.</summary>
public sealed class RecordsService
{
    private const string V1 = "https://graph.microsoft.com/v1.0/";
    private readonly GraphClient _graph;

    public RecordsService(GraphClient graph) => _graph = graph;

    /// <summary>All Entra device records. Needs Device.Read.All.</summary>
    public async Task<List<EntraDevice>> GetEntraDevicesAsync(CancellationToken ct = default)
    {
        var nodes = await _graph.GetAllAsync(
            $"{V1}devices?$select=id,deviceId,displayName,operatingSystem,trustType,isManaged,accountEnabled,approximateLastSignInDateTime&$top=999",
            ct: ct);
        return nodes.Select(MapEntra).ToList();
    }

    internal static EntraDevice MapEntra(JsonNode n) => new()
    {
        ObjectId = n.Str("id") ?? "",
        DeviceId = n.Str("deviceId") ?? "",
        DisplayName = n.Str("displayName") ?? "",
        OperatingSystem = n.Str("operatingSystem"),
        TrustType = n.Str("trustType"),
        IsManaged = n.Bool("isManaged"),
        AccountEnabled = n.Bool("accountEnabled"),
        LastSignIn = n.Date("approximateLastSignInDateTime"),
    };

    /// <summary>All Windows Autopilot registrations. Needs DeviceManagementServiceConfig.Read.All.</summary>
    public async Task<List<AutopilotDevice>> GetAutopilotDevicesAsync(CancellationToken ct = default)
    {
        var nodes = await _graph.GetAllAsync($"{V1}deviceManagement/windowsAutopilotDeviceIdentities", ct: ct);
        return nodes.Select(n => new AutopilotDevice
        {
            Id = n.Str("id") ?? "",
            SerialNumber = n.Str("serialNumber") ?? "",
            GroupTag = n.Str("groupTag"),
            EnrollmentState = n.Str("enrollmentState"),
            Model = n.Str("model"),
            ManagedDeviceId = n.Str("managedDeviceId"),
            LastContacted = n.Date("lastContactedDateTime"),
        }).ToList();
    }

    /// <summary>Intune audit events (admin actions and changes) that touched this device in the last N days.
    /// Intune can't filter by device server-side, so this reads the tenant's events for the period and
    /// keeps the ones whose resources include the device.</summary>
    public async Task<List<TimelineEvent>> GetDeviceAuditEventsAsync(string managedDeviceId, string? deviceName,
        int days = 30, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = Uri.EscapeDataString($"activityDateTime ge {since}");
        var nodes = await _graph.GetAllAsync($"deviceManagement/auditEvents?$filter={filter}", maxItems: 5000, ct: ct);
        return FilterAuditEvents(nodes, managedDeviceId, deviceName);
    }

    public static List<TimelineEvent> FilterAuditEvents(IEnumerable<JsonNode> events, string managedDeviceId, string? deviceName)
    {
        var list = new List<TimelineEvent>();
        foreach (var e in events)
        {
            var resources = e["resources"] as JsonArray;
            var hit = resources?.Any(r =>
                string.Equals(r.Str("resourceId"), managedDeviceId, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(deviceName) && string.Equals(r.Str("displayName"), deviceName, StringComparison.OrdinalIgnoreCase)
                    && (r.Str("type") ?? "").Contains("Device", StringComparison.OrdinalIgnoreCase))) == true;
            if (!hit) continue;

            var when = e.Date("activityDateTime");
            if (when is null) continue;
            var result = e.Str("activityResult") ?? "";
            list.Add(new TimelineEvent
            {
                When = when.Value,
                Source = TimelineSource.Intune,
                Title = e.Str("activity") ?? e.Str("displayName") ?? "Intune change",
                Detail = string.Join(" · ", new[] { e.Str("componentName"), e.Str("activityType") }
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct()),
                Actor = e["actor"].Str("userPrincipalName") ?? e["actor"].Str("applicationDisplayName"),
                IsFailure = result.Length > 0 && !result.Equals("Success", StringComparison.OrdinalIgnoreCase),
            });
        }
        return list;
    }
}
