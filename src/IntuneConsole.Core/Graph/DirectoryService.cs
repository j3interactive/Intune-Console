using System.Text.Json.Nodes;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Graph;

/// <summary>Entra ID lookups: user search, user details and sign-in history.</summary>
public sealed class DirectoryService
{
    private readonly GraphClient _graph;

    // v1.0 for directory and sign-in data; Intune calls stay on beta.
    private const string V1 = "https://graph.microsoft.com/v1.0/";

    private const string UserSelect =
        "id,displayName,userPrincipalName,mail,jobTitle,department,officeLocation,employeeId,mobilePhone,accountEnabled";

    public DirectoryService(GraphClient graph) => _graph = graph;

    /// <summary>Searches users by name, UPN, email, employee ID, job title or department.
    /// Name matching is word-based ("jsmi" won't match "John Smith", but "john" or "smi" will);
    /// the other fields match from the start ("12" matches employee ID "1234").</summary>
    public async Task<List<DirectoryUser>> SearchUsersAsync(string query, int top = 15, CancellationToken ct = default)
    {
        var q = BuildSearch(query);
        if (q is null) return new();

        var url = $"{V1}users?$search={Uri.EscapeDataString(q)}&$select={UserSelect}&$top={top}&$count=true";
        var page = await _graph.GetAsync(url, ct, GraphClient.EventualConsistency);
        var users = (page?["value"] as JsonArray ?? new JsonArray())
            .Where(n => n is not null)
            .Select(n => MapUser(n!))
            .ToList();

        // Exact UPN/email/name matches first, then enabled accounts, then alphabetical.
        var needle = query.Trim();
        return users
            .OrderByDescending(u => string.Equals(u.UserPrincipalName, needle, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(u.Mail, needle, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(u.DisplayName, needle, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(u => u.AccountEnabled != false)
            .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Builds the $search expression, or null if the query is too short to search.</summary>
    public static string? BuildSearch(string? query)
    {
        // Quotes would break the expression and an encoded '&' breaks directory search (known Graph issue).
        var cleaned = new string((query ?? "").Where(c => c != '"' && c != '&' && c != '\\').ToArray()).Trim();
        if (cleaned.Length < 2) return null;

        string[] props = ["displayName", "userPrincipalName", "mail", "employeeId", "jobTitle", "department"];
        return string.Join(" OR ", props.Select(p => $"\"{p}:{cleaned}\""));
    }

    public async Task<DirectoryUser> GetUserAsync(string userId, CancellationToken ct = default)
    {
        var n = await _graph.GetAsync($"{V1}users/{userId}?$select={UserSelect}", ct);
        var user = MapUser(n!);
        try
        {
            var m = await _graph.GetAsync($"{V1}users/{userId}/manager?$select=id,displayName", ct);
            user.ManagerName = m.Str("displayName");
            user.ManagerId = m.Str("id");
        }
        catch (GraphException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // No manager set.
        }
        return user;
    }

    /// <summary>Recent sign-ins for a user, newest first. Needs AuditLog.Read.All and a reports role
    /// (Reports Reader, Global Reader or a Security role) for the admin.</summary>
    public async Task<List<SignInEvent>> GetSignInsAsync(string userId, int days = 14, int top = 100, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"userId eq '{userId}' and createdDateTime ge {since}";
        var url = $"{V1}auditLogs/signIns?$filter={Uri.EscapeDataString(filter)}&$top={top}";
        var page = await _graph.GetAsync(url, ct);
        return (page?["value"] as JsonArray ?? new JsonArray())
            .Where(n => n is not null)
            .Select(n => MapSignIn(n!))
            .OrderByDescending(s => s.When)
            .ToList();
    }

    internal static DirectoryUser MapUser(JsonNode n) => new()
    {
        Id = n.Str("id") ?? "",
        DisplayName = n.Str("displayName") ?? "",
        UserPrincipalName = n.Str("userPrincipalName"),
        Mail = n.Str("mail"),
        JobTitle = n.Str("jobTitle"),
        Department = n.Str("department"),
        OfficeLocation = n.Str("officeLocation"),
        EmployeeId = n.Str("employeeId"),
        MobilePhone = n.Str("mobilePhone"),
        AccountEnabled = n.Bool("accountEnabled"),
    };

    internal static SignInEvent MapSignIn(JsonNode n)
    {
        var dev = n["deviceDetail"];
        var loc = n["location"];
        var status = n["status"];
        return new SignInEvent
        {
            When = n.Date("createdDateTime"),
            App = n.Str("appDisplayName") ?? "",
            DeviceName = NullIfEmpty(dev.Str("displayName")),
            EntraDeviceId = NullIfEmpty(dev.Str("deviceId")),
            OperatingSystem = NullIfEmpty(dev.Str("operatingSystem")),
            Browser = NullIfEmpty(dev.Str("browser")),
            IsCompliant = dev.Bool("isCompliant"),
            IpAddress = n.Str("ipAddress"),
            City = NullIfEmpty(loc.Str("city")),
            Country = NullIfEmpty(loc.Str("countryOrRegion")),
            ErrorCode = (int)(status.Num("errorCode") ?? 0),
            FailureReason = NullIfEmpty(status.Str("failureReason")),
            IsInteractive = n.Bool("isInteractive") ?? false,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Combines primary-user devices with devices seen in sign-ins (matched on the Entra
    /// device ID that Intune also stores), newest activity first.</summary>
    public static List<UserDeviceLink> LinkDevices(IEnumerable<ManagedDevice> primaryDevices,
        IEnumerable<SignInEvent> signIns, IEnumerable<ManagedDevice> allDevices)
    {
        var lastSeen = signIns
            .Where(s => s.EntraDeviceId is not null && s.When is not null)
            .GroupBy(s => s.EntraDeviceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(s => s.When!.Value), StringComparer.OrdinalIgnoreCase);

        var byEntraId = allDevices
            .Where(d => !string.IsNullOrEmpty(d.AzureAdDeviceId))
            .GroupBy(d => d.AzureAdDeviceId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var links = new Dictionary<string, UserDeviceLink>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in primaryDevices)
        {
            DateTimeOffset? seen = d.AzureAdDeviceId is not null && lastSeen.TryGetValue(d.AzureAdDeviceId, out var t) ? t : null;
            links[d.Id] = new UserDeviceLink { Device = d, IsPrimaryUser = true, LastSignIn = seen };
        }
        foreach (var (entraId, seen) in lastSeen)
        {
            if (!byEntraId.TryGetValue(entraId, out var d) || links.ContainsKey(d.Id)) continue;
            links[d.Id] = new UserDeviceLink { Device = d, IsPrimaryUser = false, LastSignIn = seen };
        }

        return links.Values
            .OrderByDescending(l => l.IsPrimaryUser)
            .ThenByDescending(l => l.LastSignIn ?? l.Device.LastSyncDateTime ?? DateTimeOffset.MinValue)
            .ToList();
    }
}
