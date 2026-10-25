using System.Text.Json.Nodes;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Graph;

/// <summary>Loads what the "why isn't this installing?" check needs from Intune and Entra.</summary>
public sealed class AppCheckService
{
    private readonly GraphClient _graph;
    private readonly DirectoryService _directory;

    public AppCheckService(GraphClient graph, DirectoryService directory)
    {
        _graph = graph;
        _directory = directory;
    }

    public async Task<(AppRequirements App, List<AppAssignment> Assignments)> GetAppAsync(string appId, CancellationToken ct = default)
    {
        var appTask = _graph.GetAsync($"deviceAppManagement/mobileApps/{appId}", ct);
        var assignTask = _graph.GetAllAsync($"deviceAppManagement/mobileApps/{appId}/assignments", ct: ct);
        Task<List<JsonNode>> relTask = _graph.GetAllAsync(
            $"deviceAppManagement/mobileApps/{appId}/relationships?$filter={Uri.EscapeDataString("targetType eq 'child'")}", ct: ct);

        var app = ParseApp(await appTask);
        var assignments = (await assignTask).Select(ParseAssignment).ToList();

        try
        {
            foreach (var r in await relTask)
                if ((r.Str("@odata.type") ?? "").Contains("Dependency", StringComparison.OrdinalIgnoreCase))
                    app.Dependencies.Add((r.Str("targetId") ?? "", r.Str("targetDisplayName") ?? r.Str("targetId") ?? ""));
        }
        catch (GraphException) { /* relationships aren't available for every app type */ }

        // Names for groups and filters, looked up once each.
        foreach (var gid in assignments.Where(a => a.GroupId is not null).Select(a => a.GroupId!).Distinct())
        {
            var name = await _directory.GetGroupNameAsync(gid, ct);
            foreach (var a in assignments.Where(a => a.GroupId == gid)) a.GroupName = name;
        }
        foreach (var fid in assignments.Where(a => a.HasFilter).Select(a => a.FilterId!).Distinct())
        {
            try
            {
                var f = await _graph.GetAsync($"deviceManagement/assignmentFilters/{fid}?$select=displayName,rule", ct);
                foreach (var a in assignments.Where(a => a.FilterId == fid))
                {
                    a.FilterName = f.Str("displayName");
                    a.FilterRule = f.Str("rule");
                }
            }
            catch (GraphException) { }
        }
        return (app, assignments);
    }

    public static AppRequirements ParseApp(JsonNode? n)
    {
        var type = (n.Str("@odata.type") ?? "").Replace("#microsoft.graph.", "");
        var rules = new List<string>();
        foreach (var r in (n?["rules"] as JsonArray ?? new JsonArray()).Concat(n?["requirementRules"] as JsonArray ?? new JsonArray()))
        {
            if (r is null) continue;
            var ruleType = r.Str("ruleType");
            // "rules" mixes detection and requirement rules; legacy "requirementRules" has no ruleType.
            if (ruleType is not null && !ruleType.Equals("requirement", StringComparison.OrdinalIgnoreCase)) continue;
            var t = r.Str("@odata.type") ?? "";
            rules.Add(t.Contains("Registry", StringComparison.OrdinalIgnoreCase) ? "a registry rule"
                : t.Contains("PowerShell", StringComparison.OrdinalIgnoreCase) || t.Contains("Script", StringComparison.OrdinalIgnoreCase) ? "a script rule"
                : t.Contains("FileSystem", StringComparison.OrdinalIgnoreCase) ? "a file rule"
                : "a custom rule");
        }

        var arch = n.Str("allowedArchitectures");
        if (string.IsNullOrWhiteSpace(arch) || arch == "null") arch = n.Str("applicableArchitectures");

        return new AppRequirements
        {
            AppName = n.Str("displayName") ?? "",
            AppType = type,
            Architectures = arch,
            MinimumWindowsRelease = n.Str("minimumSupportedWindowsRelease"),
            MinimumFreeDiskMb = (int?)n.Num("minimumFreeDiskSpaceInMB"),
            MinimumMemoryMb = (int?)n.Num("minimumMemoryInMB"),
            CustomRequirementRules = rules,
        };
    }

    public static AppAssignment ParseAssignment(JsonNode n)
    {
        var t = n["target"];
        var odata = (t.Str("@odata.type") ?? "").Replace("#microsoft.graph.", "");
        return new AppAssignment
        {
            Intent = n.Str("intent") ?? "",
            TargetType = odata switch
            {
                "exclusionGroupAssignmentTarget" => "exclusion",
                "allDevicesAssignmentTarget" => "allDevices",
                "allLicensedUsersAssignmentTarget" => "allUsers",
                _ => "group",
            },
            GroupId = t.Str("groupId"),
            FilterId = t.Str("deviceAndAppManagementAssignmentFilterId"),
            FilterMode = t.Str("deviceAndAppManagementAssignmentFilterType"),
        };
    }

    /// <summary>Device properties and group memberships for the check.</summary>
    public async Task<DeviceFacts> GetDeviceFactsAsync(ManagedDevice d, CancellationToken ct = default)
    {
        JsonNode? n = null;
        try
        {
            n = await _graph.GetAsync($"deviceManagement/managedDevices/{d.Id}?$select=processorArchitecture,freeStorageSpaceInBytes," +
                                      "physicalMemoryInBytes,managedDeviceOwnerType,deviceCategoryDisplayName,enrollmentProfileName", ct);
        }
        catch (GraphException) { }

        var groupsKnown = true;
        var deviceGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var userGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!string.IsNullOrEmpty(d.AzureAdDeviceId) && d.AzureAdDeviceId != Guid.Empty.ToString())
                deviceGroups = await _directory.GetDeviceGroupIdsAsync(d.AzureAdDeviceId, ct);
            if (!string.IsNullOrEmpty(d.UserId) && d.UserId != Guid.Empty.ToString())
                userGroups = await _directory.GetUserGroupIdsAsync(d.UserId, ct);
        }
        catch (GraphException) { groupsKnown = false; }

        long? Bytes(string key) => n.Num(key) is { } v and > 0 ? (long)v : null;
        return new DeviceFacts
        {
            DeviceName = d.DeviceName,
            Manufacturer = d.Manufacturer,
            Model = d.Model,
            OsVersion = d.OsVersion,
            Architecture = n.Str("processorArchitecture"),
            FreeStorageBytes = Bytes("freeStorageSpaceInBytes"),
            PhysicalMemoryBytes = Bytes("physicalMemoryInBytes"),
            Ownership = n.Str("managedDeviceOwnerType"),
            DeviceCategory = n.Str("deviceCategoryDisplayName"),
            EnrollmentProfileName = n.Str("enrollmentProfileName"),
            DeviceGroupIds = deviceGroups,
            UserGroupIds = userGroups,
            HasPrimaryUser = !string.IsNullOrEmpty(d.UserId) && d.UserId != Guid.Empty.ToString(),
            GroupsKnown = groupsKnown,
        };
    }
}
