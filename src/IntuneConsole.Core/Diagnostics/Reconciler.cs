using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Compares Intune, Entra ID, Autopilot and Datto and lists records that don't line up.
/// Read-only: it suggests cleanup but never changes anything.</summary>
public static class Reconciler
{
    public const string DuplicateIntune = "Duplicate Intune records";
    public const string NotInDatto = "In Intune, not in Datto";
    public const string NotInIntune = "In Datto, not in Intune";
    public const string StaleIntune = "Intune: no check-in 30+ days";
    public const string StaleEntra = "Entra: no sign-in 90+ days";
    public const string JoinedNotManaged = "Entra joined, not in Intune";
    public const string NoGroupTag = "Autopilot: no group tag";
    public const string NotInAutopilot = "Not registered in Autopilot";
    public const string NeverEnrolled = "Autopilot: never enrolled";

    public sealed class Inputs
    {
        public IReadOnlyList<ManagedDevice> Intune { get; init; } = [];
        public IReadOnlyList<EntraDevice>? Entra { get; init; }        // null = not loaded (no permission)
        public IReadOnlyList<AutopilotDevice>? Autopilot { get; init; }
        public IReadOnlyList<DattoDevice>? Datto { get; init; }
        public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    }

    public static List<ReconciliationFinding> Analyze(Inputs input)
    {
        var findings = new List<ReconciliationFinding>();
        var now = input.Now;
        var windows = input.Intune.Where(d => d.IsWindows).ToList();

        // 1. Duplicate Intune records for one serial: the older ones are usually leftovers from a reset or re-enrollment.
        foreach (var g in windows.Where(d => IsRealSerial(d.SerialNumber))
                     .GroupBy(d => d.SerialNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var ordered = g.OrderByDescending(d => d.LastSyncDateTime ?? DateTimeOffset.MinValue).ToList();
            foreach (var old in ordered.Skip(1))
                findings.Add(new ReconciliationFinding
                {
                    Category = DuplicateIntune,
                    Severity = FindingSeverity.Warning,
                    DeviceName = old.DeviceName,
                    SerialNumber = old.SerialNumber,
                    IntuneDevice = old,
                    Detail = $"{g.Count()} Intune records share this serial. This one last checked in {Humanize.Ago(old.LastSyncDateTime)}; " +
                             $"the newest is {ordered[0].DeviceName} ({Humanize.Ago(ordered[0].LastSyncDateTime)}).",
                    Suggestion = "Delete the older record in Intune once you've confirmed the newer one is the active device.",
                });
        }

        // 2. Stale Intune devices.
        foreach (var d in windows.Where(d => d.LastSyncDateTime is not null && now - d.LastSyncDateTime.Value > TimeSpan.FromDays(30)))
            findings.Add(new ReconciliationFinding
            {
                Category = StaleIntune,
                Severity = FindingSeverity.Warning,
                DeviceName = d.DeviceName,
                SerialNumber = d.SerialNumber,
                IntuneDevice = d,
                Detail = $"Last checked in {Humanize.Ago(d.LastSyncDateTime)}. Primary user: {d.UserDisplayName ?? "none"}.",
                Suggestion = "Find out if the device is in use. If it's retired or lost, retire it in Intune.",
            });

        // 3 & 4. Intune vs Datto, matched by hostname.
        if (input.Datto is not null)
        {
            var dattoNames = new HashSet<string>(input.Datto.Select(d => d.Hostname), StringComparer.OrdinalIgnoreCase);
            var intuneNames = new HashSet<string>(input.Intune.Select(d => d.DeviceName), StringComparer.OrdinalIgnoreCase);

            foreach (var d in windows.Where(d => !dattoNames.Contains(d.DeviceName)))
                findings.Add(new ReconciliationFinding
                {
                    Category = NotInDatto,
                    Severity = d.IsStale(now, 30) ? FindingSeverity.Info : FindingSeverity.Problem,
                    DeviceName = d.DeviceName,
                    SerialNumber = d.SerialNumber,
                    IntuneDevice = d,
                    Detail = $"Active in Intune ({Humanize.Ago(d.LastSyncDateTime)}) but no Datto agent with this hostname.",
                    Suggestion = "Deploy the Datto agent (for example as a required Win32 app), or check whether the device was renamed.",
                });

            foreach (var d in input.Datto.Where(d => d.Hostname.Length > 0 && !intuneNames.Contains(d.Hostname)))
                findings.Add(new ReconciliationFinding
                {
                    Category = NotInIntune,
                    Severity = d.LastSeen is not null && now - d.LastSeen.Value < TimeSpan.FromDays(30) ? FindingSeverity.Problem : FindingSeverity.Info,
                    DeviceName = d.Hostname,
                    Detail = $"Datto agent last seen {Humanize.Ago(d.LastSeen)}{(d.SiteName is null ? "" : $" in site {d.SiteName}")}, but no Intune device has this name.",
                    Suggestion = "If it's a company PC, enroll it in Intune. If it's a server or retired device, you can ignore or remove it in Datto.",
                });
        }

        // 5 & 6. Entra records.
        if (input.Entra is not null)
        {
            var intuneEntraIds = new HashSet<string>(input.Intune.Select(d => d.AzureAdDeviceId ?? ""), StringComparer.OrdinalIgnoreCase);
            foreach (var e in input.Entra.Where(e => e.IsWindows && e.AccountEnabled != false))
            {
                if (e.LastSignIn is not null && now - e.LastSignIn.Value > TimeSpan.FromDays(90))
                    findings.Add(new ReconciliationFinding
                    {
                        Category = StaleEntra,
                        Severity = FindingSeverity.Info,
                        DeviceName = e.DisplayName,
                        Detail = $"Enabled Entra device, last sign-in {Humanize.Ago(e.LastSignIn)} ({JoinText(e.TrustType)}).",
                        Suggestion = "Disable it in Entra, then delete it after a waiting period if nobody notices.",
                    });
                else if (e.TrustType == "AzureAd" && !intuneEntraIds.Contains(e.DeviceId))
                    findings.Add(new ReconciliationFinding
                    {
                        Category = JoinedNotManaged,
                        Severity = FindingSeverity.Problem,
                        DeviceName = e.DisplayName,
                        Detail = $"Entra joined and signed in {Humanize.Ago(e.LastSignIn)}, but there's no Intune record for it.",
                        Suggestion = "Check automatic MDM enrollment (MDM user scope) and that the user is licensed for Intune.",
                    });
            }
        }

        // 7, 8 & 9. Autopilot.
        if (input.Autopilot is not null)
        {
            var apSerials = new HashSet<string>(input.Autopilot.Select(a => a.SerialNumber.Trim()), StringComparer.OrdinalIgnoreCase);
            var intuneSerials = new HashSet<string>(windows.Where(d => IsRealSerial(d.SerialNumber)).Select(d => d.SerialNumber!.Trim()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var a in input.Autopilot)
            {
                if (string.IsNullOrWhiteSpace(a.GroupTag))
                    findings.Add(new ReconciliationFinding
                    {
                        Category = NoGroupTag,
                        Severity = FindingSeverity.Warning,
                        DeviceName = a.SerialNumber,
                        SerialNumber = a.SerialNumber,
                        IntuneDevice = windows.FirstOrDefault(d => string.Equals(d.SerialNumber, a.SerialNumber, StringComparison.OrdinalIgnoreCase)),
                        Detail = $"Registered in Autopilot ({a.Model}) with no group tag.",
                        Suggestion = "Set a group tag so the device lands in the right dynamic group and gets the right profile.",
                    });

                if (string.Equals(a.EnrollmentState, "notContacted", StringComparison.OrdinalIgnoreCase) && !intuneSerials.Contains(a.SerialNumber.Trim()))
                    findings.Add(new ReconciliationFinding
                    {
                        Category = NeverEnrolled,
                        Severity = FindingSeverity.Info,
                        DeviceName = a.SerialNumber,
                        SerialNumber = a.SerialNumber,
                        Detail = $"Registered in Autopilot ({a.Model}) but has never enrolled.",
                        Suggestion = "Normal for spares in stock. If it should be in use, find it.",
                    });
            }

            foreach (var d in windows.Where(d => IsRealSerial(d.SerialNumber) && !apSerials.Contains(d.SerialNumber!.Trim())))
                findings.Add(new ReconciliationFinding
                {
                    Category = NotInAutopilot,
                    Severity = FindingSeverity.Warning,
                    DeviceName = d.DeviceName,
                    SerialNumber = d.SerialNumber,
                    IntuneDevice = d,
                    Detail = "In Intune but not registered in Autopilot, so a reset won't bring it back through Autopilot.",
                    Suggestion = "Register it (Dell can upload hashes for new orders; for existing devices, convert all targeted devices or upload the hash).",
                });
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Serials like "0", "System Serial Number" or "To be filled by O.E.M." are placeholders, not real serials.</summary>
    public static bool IsRealSerial(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (t.Length < 4 || t.All(c => c == '0')) return false;
        return !(t.Contains("serial", StringComparison.OrdinalIgnoreCase) || t.Contains("O.E.M", StringComparison.OrdinalIgnoreCase)
                 || t.Equals("Default string", StringComparison.OrdinalIgnoreCase));
    }

    private static string JoinText(string? trustType) => trustType switch
    {
        "AzureAd" => "Entra joined",
        "ServerAd" => "hybrid joined",
        "Workplace" => "registered",
        _ => trustType ?? "unknown join type",
    };
}
