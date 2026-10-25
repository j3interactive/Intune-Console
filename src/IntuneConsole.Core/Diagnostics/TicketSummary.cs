using System.Text;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Builds a short plain-text summary of a device's state to paste into a ticket or Teams.</summary>
public static class TicketSummary
{
    public static string Build(ManagedDevice d, IReadOnlyList<AppInstallStatus>? apps, IReadOnlyList<PolicyState>? policies,
        LogCollectionRequest? diag, IEnumerable<ActionLogEntry>? actions, IReadOnlyList<Finding>? causes = null,
        DateTimeOffset? now = null)
    {
        var at = (now ?? DateTimeOffset.Now).ToLocalTime();
        var sb = new StringBuilder();
        sb.AppendLine($"Device: {d.DeviceName}  ({string.Join(", ", new[] { d.Manufacturer, d.Model, d.SerialNumber }.Where(Has))})");
        sb.AppendLine($"User: {d.UserDisplayName ?? "none"}{(Has(d.UserPrincipalName) ? $" <{d.UserPrincipalName}>" : "")}");
        sb.AppendLine($"OS: {d.OsVersion ?? "unknown"}");
        sb.AppendLine($"Compliance: {d.ComplianceText} · Last check-in: {d.LastSyncText} · Encryption: " +
                      (d.IsEncrypted switch { true => "on", false => "OFF", _ => "unknown" }));
        sb.AppendLine($"Datto: {d.DattoStatusText}{(d.Datto?.LastSeen is { } seen ? $" (last seen {Humanize.Ago(seen)})" : "")}");

        var failedApps = apps?.Where(a => a.IsFailed).ToList() ?? new();
        var pendingApps = apps?.Where(a => a.IsPending).ToList() ?? new();
        if (apps is not null)
        {
            sb.AppendLine();
            if (failedApps.Count == 0 && pendingApps.Count == 0)
                sb.AppendLine("Apps: no failed or pending installs.");
            foreach (var a in failedApps)
                sb.AppendLine($"App FAILED: {a.AppName}{(Has(a.HexErrorCode) ? $" [{a.HexErrorCode}]" : "")}" +
                              (Has(a.Explanation) ? $" - {a.Explanation}" : ""));
            foreach (var a in pendingApps)
                sb.AppendLine($"App pending: {a.AppName}");
        }

        var badPolicies = policies?.Where(p => p.IsProblem).ToList();
        if (badPolicies is { Count: > 0 })
        {
            sb.AppendLine();
            foreach (var p in badPolicies) sb.AppendLine($"Policy {p.State}: {p.DisplayName} ({p.Kind})");
        }

        if (causes is { Count: > 0 })
        {
            sb.AppendLine();
            foreach (var c in causes) sb.AppendLine($"Likely cause: {c.Title}. Next step: {c.NextStep}");
        }

        if (diag is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Diagnostics: {diag}");
        }

        var recent = actions?.Where(a => a.DeviceName.Equals(d.DeviceName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.When).Take(8).ToList();
        if (recent is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Actions taken:");
            foreach (var a in recent.OrderBy(a => a.When))
                sb.AppendLine($"  {a.When.ToLocalTime():MMM d HH:mm}  {a.Action} via {a.Via}: {a.Outcome}");
        }

        sb.AppendLine();
        sb.Append($"(Intune Troubleshooting Console, {at:yyyy-MM-dd HH:mm})");
        return sb.ToString();
    }

    private static bool Has(string? s) => !string.IsNullOrWhiteSpace(s);
}
