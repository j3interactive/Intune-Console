using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>"Is this device ready to hand over?" as one checklist.</summary>
public static class Readiness
{
    public sealed class Inputs
    {
        public ManagedDevice Device { get; init; } = new();
        public AutopilotDevice? Autopilot { get; init; }
        public bool AutopilotKnown { get; init; }            // false = couldn't read Autopilot
        public IReadOnlyList<AppInstallStatus>? Apps { get; init; }
        public DateTimeOffset? LapsLastBackup { get; init; }
        public bool LapsKnown { get; init; }
        public bool DattoConfigured { get; init; }
        public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    }

    public static CheckResult Evaluate(Inputs i)
    {
        var d = i.Device;
        var steps = new List<CheckStep>();

        steps.Add(!i.AutopilotKnown
            ? new CheckStep { Name = "Registered in Autopilot", Status = CheckStatus.Unknown, Detail = "Couldn't read Autopilot records.",
                Fix = "Add DeviceManagementServiceConfig.Read.All (see README)." }
            : i.Autopilot is null
                ? new CheckStep { Name = "Registered in Autopilot", Status = CheckStatus.Fail, Detail = $"Serial {d.SerialNumber ?? "unknown"} isn't registered.",
                    Fix = "Register the hardware hash so a reset brings it back through Autopilot." }
                : string.IsNullOrWhiteSpace(i.Autopilot.GroupTag)
                    ? new CheckStep { Name = "Registered in Autopilot", Status = CheckStatus.Warn, Detail = "Registered, but has no group tag.",
                        Fix = "Set the group tag so it gets the right profile and apps." }
                    : new CheckStep { Name = "Registered in Autopilot", Status = CheckStatus.Pass, Detail = $"Registered, group tag \"{i.Autopilot.GroupTag}\"." });

        var sinceSync = d.LastSyncDateTime is null ? (TimeSpan?)null : i.Now - d.LastSyncDateTime.Value;
        steps.Add(sinceSync is null
            ? new CheckStep { Name = "Checking in to Intune", Status = CheckStatus.Fail, Detail = "Has never checked in." }
            : sinceSync < TimeSpan.FromHours(24)
                ? new CheckStep { Name = "Checking in to Intune", Status = CheckStatus.Pass, Detail = $"Last check-in {d.LastSyncText}." }
                : new CheckStep { Name = "Checking in to Intune", Status = CheckStatus.Warn, Detail = $"Last check-in {d.LastSyncText}.",
                    Fix = "Turn it on, connect it to the network and sync." });

        steps.Add(d.ComplianceState switch
        {
            "compliant" => new CheckStep { Name = "Compliant", Status = CheckStatus.Pass, Detail = "Compliant." },
            "inGracePeriod" => new CheckStep { Name = "Compliant", Status = CheckStatus.Warn, Detail = "In grace period: something is out of compliance.",
                Fix = "Open the device page's policy problems." },
            null or "" or "unknown" => new CheckStep { Name = "Compliant", Status = CheckStatus.Warn, Detail = "Not evaluated yet." },
            _ => new CheckStep { Name = "Compliant", Status = CheckStatus.Fail, Detail = d.ComplianceText, Fix = "Open the device page's policy problems." },
        });

        steps.Add(d.IsEncrypted switch
        {
            true => new CheckStep { Name = "BitLocker on", Status = CheckStatus.Pass, Detail = "Encrypted." },
            false => new CheckStep { Name = "BitLocker on", Status = CheckStatus.Fail, Detail = "Not encrypted.",
                Fix = "Check the BitLocker policy and TPM; encryption can take a while after enrollment." },
            _ => new CheckStep { Name = "BitLocker on", Status = CheckStatus.Unknown, Detail = "Encryption state not reported." },
        });

        if (i.Apps is null)
            steps.Add(new CheckStep { Name = "Apps installed", Status = CheckStatus.Unknown, Detail = "App status not loaded." });
        else
        {
            var failed = i.Apps.Where(a => a.IsFailed).Select(a => a.AppName).ToList();
            var pending = i.Apps.Where(a => a.IsPending).Select(a => a.AppName).ToList();
            steps.Add(failed.Count > 0
                ? new CheckStep { Name = "Apps installed", Status = CheckStatus.Fail, Detail = $"Failed: {string.Join(", ", failed)}.",
                    Fix = "Select the app on the device page and run \"Why isn't this installing?\"." }
                : pending.Count > 0
                    ? new CheckStep { Name = "Apps installed", Status = CheckStatus.Warn, Detail = $"Still installing: {string.Join(", ", pending)}." }
                    : new CheckStep { Name = "Apps installed", Status = CheckStatus.Pass, Detail = $"{i.Apps.Count} app(s) reported, none failed or pending." });
        }

        steps.Add(string.IsNullOrWhiteSpace(d.UserPrincipalName) && string.IsNullOrWhiteSpace(d.UserDisplayName)
            ? new CheckStep { Name = "Primary user set", Status = CheckStatus.Warn, Detail = "No primary user.",
                Fix = "Set the primary user in Intune if this isn't a shared device; user-targeted apps and Company Portal depend on it." }
            : new CheckStep { Name = "Primary user set", Status = CheckStatus.Pass, Detail = d.UserDisplayName ?? d.UserPrincipalName ?? "" });

        if (i.DattoConfigured)
            steps.Add(d.Datto is null
                ? new CheckStep { Name = "Datto agent", Status = CheckStatus.Fail, Detail = "No Datto agent with this hostname.",
                    Fix = "Make sure the Datto agent app is assigned and installed." }
                : d.Datto.Online
                    ? new CheckStep { Name = "Datto agent", Status = CheckStatus.Pass, Detail = "Installed and online." }
                    : new CheckStep { Name = "Datto agent", Status = CheckStatus.Warn, Detail = $"Installed, offline (last seen {Humanize.Ago(d.Datto.LastSeen)})." });

        steps.Add(!i.LapsKnown
            ? new CheckStep { Name = "LAPS password backed up", Status = CheckStatus.Unknown, Detail = "Couldn't check LAPS (permission or role)." }
            : i.LapsLastBackup is null
                ? new CheckStep { Name = "LAPS password backed up", Status = CheckStatus.Fail, Detail = "No LAPS password in Entra.",
                    Fix = "Make sure a Windows LAPS policy targets the device." }
                : new CheckStep { Name = "LAPS password backed up", Status = CheckStatus.Pass, Detail = $"Last backup {Humanize.Ago(i.LapsLastBackup)}." });

        var fails = steps.Count(s => s.Status == CheckStatus.Fail);
        var warns = steps.Count(s => s.Status is CheckStatus.Warn or CheckStatus.Unknown);
        return new CheckResult
        {
            Title = $"Is {d.DeviceName} ready to hand over?",
            Overall = fails > 0 ? CheckStatus.Fail : warns > 0 ? CheckStatus.Warn : CheckStatus.Pass,
            Verdict = fails > 0 ? $"Not ready: {fails} problem(s) to fix."
                : warns > 0 ? $"Nearly ready: {warns} item(s) to check."
                : "Ready to hand over.",
            Steps = steps,
        };
    }
}
