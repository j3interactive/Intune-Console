using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Works out why an app isn't installing on one device by walking the same gates Intune does:
/// assignment, exclusion, intent, assignment filter, requirements, dependencies, then the install result.</summary>
public static class AppInstallCheck
{
    public static CheckResult Evaluate(AppRequirements app, IReadOnlyList<AppAssignment> assignments, DeviceFacts device,
        AppInstallStatus? status, IReadOnlyList<AppInstallStatus> allDeviceApps)
    {
        var steps = new List<CheckStep>();

        // 1. Assignment and exclusion
        var includes = assignments.Where(a => !a.IsExclusion).ToList();
        var applying = includes.Where(a => Applies(a, device)).ToList();
        var exclusions = assignments.Where(a => a.IsExclusion && a.GroupId is not null
                                                && (device.DeviceGroupIds.Contains(a.GroupId) || device.UserGroupIds.Contains(a.GroupId))).ToList();

        if (assignments.Count == 0)
            steps.Add(new CheckStep { Name = "Assigned", Status = CheckStatus.Fail, Detail = "The app has no assignments at all.",
                Fix = "Assign it to a group that contains this device or its user." });
        else if (!device.GroupsKnown && !includes.Any(a => a.TargetType is "allDevices" || (a.TargetType == "allUsers" && device.HasPrimaryUser)))
            steps.Add(new CheckStep { Name = "Assigned", Status = CheckStatus.Unknown,
                Detail = $"Assigned to: {string.Join(", ", includes.Select(a => a.TargetText))}. The console couldn't read group membership to check.",
                Fix = "Add GroupMember.Read.All to the app registration (see README)." });
        else if (applying.Count == 0)
            steps.Add(new CheckStep { Name = "Assigned", Status = CheckStatus.Fail,
                Detail = $"Neither the device nor its primary user is in an assigned group. Assigned to: {string.Join(", ", includes.Select(a => a.TargetText))}.",
                Fix = device.HasPrimaryUser
                    ? "Add the device (for a device assignment) or the user (for a user assignment) to one of those groups."
                    : "Add the device to an assigned group. This device has no primary user, so user-targeted assignments only apply to whoever signs in." });
        else
            steps.Add(new CheckStep { Name = "Assigned", Status = CheckStatus.Pass,
                Detail = $"Applies through: {string.Join(", ", applying.Select(a => $"{a.TargetText} ({a.Intent})"))}." });

        if (exclusions.Count > 0)
            steps.Add(new CheckStep { Name = "Not excluded", Status = CheckStatus.Fail,
                Detail = $"Excluded by: {string.Join(", ", exclusions.Select(e => e.GroupName ?? e.GroupId))}. Exclusions win over inclusions.",
                Fix = "Remove the device or user from the excluded group, or remove the exclusion." });
        else if (assignments.Any(a => a.IsExclusion))
            steps.Add(new CheckStep { Name = "Not excluded", Status = device.GroupsKnown ? CheckStatus.Pass : CheckStatus.Unknown,
                Detail = device.GroupsKnown ? "The app has exclusions, but this device and user aren't in them." : "Couldn't check exclusion groups." });

        // 2. Intent
        if (applying.Count > 0)
        {
            if (applying.Any(a => a.Intent == "required"))
                steps.Add(new CheckStep { Name = "Intent", Status = CheckStatus.Pass, Detail = "Required: Intune installs it automatically." });
            else if (applying.Any(a => a.Intent == "uninstall"))
                steps.Add(new CheckStep { Name = "Intent", Status = CheckStatus.Fail, Detail = "Assigned as Uninstall for this device or user.",
                    Fix = "Check the uninstall assignment's group if the app should stay." });
            else
                steps.Add(new CheckStep { Name = "Intent", Status = CheckStatus.Info,
                    Detail = "Available only: it won't install until the user installs it from Company Portal.",
                    Fix = "Ask the user to install it from Company Portal, or assign it as Required." });
        }

        // 3. Assignment filters on the assignments that apply
        foreach (var a in applying.Where(a => a.HasFilter))
        {
            var r = FilterRule.Evaluate(a.FilterRule, device);
            var name = $"Filter \"{a.FilterName ?? a.FilterId}\" ({a.FilterMode})";
            if (r.Matches is null)
                steps.Add(new CheckStep { Name = name, Status = CheckStatus.Warn,
                    Detail = r.ParseError is not null
                        ? $"Couldn't read the rule ({r.ParseError}): {a.FilterRule}"
                        : $"Uses properties the console can't see ({string.Join(", ", r.UnknownProperties)}): {a.FilterRule}",
                    Fix = "Check it in Intune: Tenant administration > Filters > the filter > Preview devices." });
            else
            {
                var blocked = (a.FilterMode == "include" && r.Matches == false) || (a.FilterMode == "exclude" && r.Matches == true);
                steps.Add(new CheckStep { Name = name, Status = blocked ? CheckStatus.Fail : CheckStatus.Pass,
                    Detail = $"{(r.Matches == true ? "Device matches" : "Device doesn't match")}: {a.FilterRule}",
                    Fix = blocked ? "The filter is keeping this device out. Change the filter or the assignment." : null });
            }
        }

        // 4. Requirements
        AddRequirementSteps(app, device, steps);

        // 5. Dependencies
        foreach (var (depId, depName) in app.Dependencies)
        {
            var dep = allDeviceApps.FirstOrDefault(x => string.Equals(x.ApplicationId, depId, StringComparison.OrdinalIgnoreCase));
            steps.Add(dep switch
            {
                null => new CheckStep { Name = $"Dependency: {depName}", Status = CheckStatus.Warn,
                    Detail = "No install status reported on this device.",
                    Fix = "If the dependency is set to install automatically this is fine; otherwise assign it too." },
                { IsFailed: true } => new CheckStep { Name = $"Dependency: {depName}", Status = CheckStatus.Fail,
                    Detail = $"Failed{(dep.HexErrorCode is null ? "" : $" ({dep.HexErrorCode})")}. {dep.Explanation}",
                    Fix = "Fix the dependency first; this app waits for it." },
                { IsPending: true } => new CheckStep { Name = $"Dependency: {depName}", Status = CheckStatus.Warn, Detail = "Still pending." },
                _ when dep.InstallState.Contains("install", StringComparison.OrdinalIgnoreCase)
                       && !dep.InstallState.Contains("not", StringComparison.OrdinalIgnoreCase)
                    => new CheckStep { Name = $"Dependency: {depName}", Status = CheckStatus.Pass, Detail = "Installed." },
                _ => new CheckStep { Name = $"Dependency: {depName}", Status = CheckStatus.Warn, Detail = dep.InstallState },
            });
        }

        // 6. What the device reported
        steps.Add(StatusStep(status));

        var firstFail = steps.FirstOrDefault(s => s.Status == CheckStatus.Fail);
        var firstWarn = steps.FirstOrDefault(s => s.Status is CheckStatus.Warn or CheckStatus.Unknown);
        var installed = status is not null && IsInstalled(status);
        var verdict = firstFail is not null ? $"{firstFail.Name}: {firstFail.Detail}"
            : installed ? "Installed. Intune reports the app as installed on this device."
            : firstWarn is not null ? $"Nothing blocking found. Check: {firstWarn.Name}. {firstWarn.Detail}"
            : "Nothing blocking found. It should install at the device's next app check (about every hour, or restart the IME).";

        return new CheckResult
        {
            Title = $"Why isn't {app.AppName} installing?",
            Verdict = verdict,
            Overall = firstFail is not null ? CheckStatus.Fail : installed ? CheckStatus.Pass : firstWarn is not null ? CheckStatus.Warn : CheckStatus.Info,
            Steps = steps,
        };
    }

    private static bool Applies(AppAssignment a, DeviceFacts d) => a.TargetType switch
    {
        "allDevices" => true,
        "allUsers" => d.HasPrimaryUser,
        "group" => a.GroupId is not null && (d.DeviceGroupIds.Contains(a.GroupId) || d.UserGroupIds.Contains(a.GroupId)),
        _ => false,
    };

    private static bool IsInstalled(AppInstallStatus s) =>
        s.InstallState.Contains("install", StringComparison.OrdinalIgnoreCase)
        && !s.InstallState.Contains("not", StringComparison.OrdinalIgnoreCase)
        && !s.IsFailed && !s.IsPending;

    private static CheckStep StatusStep(AppInstallStatus? s)
    {
        if (s is null)
            return new CheckStep { Name = "Device result", Status = CheckStatus.Unknown,
                Detail = "The device hasn't reported a status for this app.",
                Fix = "Sync the device. If it's assigned and still nothing after an hour, restart the IME and collect diagnostics." };
        if (s.IsFailed)
            return new CheckStep { Name = "Device result", Status = CheckStatus.Fail,
                Detail = $"Install failed{(s.HexErrorCode is null ? "" : $" ({s.HexErrorCode})")}. {s.Explanation}".Trim(),
                Fix = "Collect diagnostics and open the log viewer for the exact error line." };
        if (s.InstallState.Contains("applicable", StringComparison.OrdinalIgnoreCase))
            return new CheckStep { Name = "Device result", Status = CheckStatus.Fail,
                Detail = $"Intune marked it Not applicable{(string.IsNullOrEmpty(s.InstallStateDetail) ? "" : $": {s.InstallStateDetail}")}. A requirement rule or filter excluded the device.",
                Fix = "Check the requirement steps above, and the custom requirement rules in the app." };
        if (s.IsPending)
            return new CheckStep { Name = "Device result", Status = CheckStatus.Warn,
                Detail = "Pending: the IME has it queued or is retrying (it retries a failed install up to 3 times, about 5 minutes apart, then waits 24 hours).",
                Fix = "Restart the IME (Datto) to make it try now." };
        if (IsInstalled(s))
            return new CheckStep { Name = "Device result", Status = CheckStatus.Pass, Detail = $"Installed{(s.AppVersion is null ? "" : $" (version {s.AppVersion})")}." };
        return new CheckStep { Name = "Device result", Status = CheckStatus.Info, Detail = s.InstallState };
    }

    private static void AddRequirementSteps(AppRequirements app, DeviceFacts d, List<CheckStep> steps)
    {
        if (!string.IsNullOrWhiteSpace(app.Architectures) && !app.Architectures.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var allowed = app.Architectures.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(d.Architecture) || d.Architecture.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                steps.Add(new CheckStep { Name = "Architecture", Status = CheckStatus.Unknown, Detail = $"App allows {app.Architectures}; device architecture unknown." });
            else
            {
                // x64 devices can run x86 apps.
                var ok = allowed.Any(a => a.Equals(d.Architecture, StringComparison.OrdinalIgnoreCase)
                                          || (a.Equals("x86", StringComparison.OrdinalIgnoreCase) && d.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)));
                steps.Add(new CheckStep { Name = "Architecture", Status = ok ? CheckStatus.Pass : CheckStatus.Fail,
                    Detail = $"App allows {app.Architectures}; device is {d.Architecture}.",
                    Fix = ok ? null : "Add the device's architecture to the app's requirements, or deploy a build for it." });
            }
        }

        if (!string.IsNullOrWhiteSpace(app.MinimumWindowsRelease))
        {
            var minBuild = WindowsBuild(app.MinimumWindowsRelease);
            var devBuild = BuildOf(d.OsVersion);
            if (minBuild is null || devBuild is null)
                steps.Add(new CheckStep { Name = "Windows version", Status = CheckStatus.Unknown,
                    Detail = $"App needs Windows {app.MinimumWindowsRelease}; device runs {d.OsVersion ?? "unknown"}." });
            else
                steps.Add(new CheckStep { Name = "Windows version", Status = devBuild >= minBuild ? CheckStatus.Pass : CheckStatus.Fail,
                    Detail = $"App needs Windows {app.MinimumWindowsRelease} (build {minBuild}+); device is build {devBuild}.",
                    Fix = devBuild >= minBuild ? null : "Update Windows on the device, or lower the app's minimum OS." });
        }

        if (app.MinimumFreeDiskMb is > 0)
        {
            if (d.FreeStorageBytes is null or <= 0)
                steps.Add(new CheckStep { Name = "Free disk space", Status = CheckStatus.Unknown, Detail = $"App needs {app.MinimumFreeDiskMb} MB free; device didn't report free space." });
            else
            {
                var freeMb = d.FreeStorageBytes.Value / 1024 / 1024;
                steps.Add(new CheckStep { Name = "Free disk space", Status = freeMb >= app.MinimumFreeDiskMb ? CheckStatus.Pass : CheckStatus.Fail,
                    Detail = $"App needs {app.MinimumFreeDiskMb:N0} MB free; device has {freeMb:N0} MB.",
                    Fix = freeMb >= app.MinimumFreeDiskMb ? null : "Free up disk space on the device." });
            }
        }

        if (app.MinimumMemoryMb is > 0)
        {
            if (d.PhysicalMemoryBytes is null or <= 0)
                steps.Add(new CheckStep { Name = "Memory", Status = CheckStatus.Unknown, Detail = $"App needs {app.MinimumMemoryMb} MB RAM; device didn't report memory." });
            else
            {
                var mb = d.PhysicalMemoryBytes.Value / 1024 / 1024;
                steps.Add(new CheckStep { Name = "Memory", Status = mb >= app.MinimumMemoryMb ? CheckStatus.Pass : CheckStatus.Fail,
                    Detail = $"App needs {app.MinimumMemoryMb:N0} MB RAM; device has {mb:N0} MB." });
            }
        }

        if (app.CustomRequirementRules.Count > 0)
            steps.Add(new CheckStep { Name = "Custom requirement rules", Status = CheckStatus.Info,
                Detail = $"The app also has {string.Join(", ", app.CustomRequirementRules)}, which only the device can evaluate.",
                Fix = "In the device's AppWorkload.log (log viewer), search for the app name and \"requirement\"." });
    }

    /// <summary>Build number from an Intune OS version like "10.0.22631.4317".</summary>
    public static int? BuildOf(string? osVersion)
    {
        var parts = (osVersion ?? "").Split('.');
        return parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : null;
    }

    /// <summary>Minimum build for a Windows release name as Intune stores it ("1909", "21H2", "Windows11_23H2"…).</summary>
    public static int? WindowsBuild(string release)
    {
        var r = release.Trim().ToUpperInvariant().Replace("WINDOWS", "").Replace("_", "").Replace(" ", "");
        var win11 = r.StartsWith("11");
        if (r.StartsWith("10") && r.Length > 4) r = r[2..];
        if (win11) r = r[2..];
        if (r.StartsWith("V10")) r = r[3..];
        var map10 = new Dictionary<string, int>
        {
            ["1607"] = 14393, ["1703"] = 15063, ["1709"] = 16299, ["1803"] = 17134, ["1809"] = 17763, ["1903"] = 18362,
            ["1909"] = 18363, ["2004"] = 19041, ["20H2"] = 19042, ["21H1"] = 19043, ["21H2"] = 19044, ["22H2"] = 19045,
        };
        var map11 = new Dictionary<string, int>
        {
            ["21H2"] = 22000, ["22H2"] = 22621, ["23H2"] = 22631, ["24H2"] = 26100, ["25H2"] = 26200,
        };
        return win11 ? (map11.TryGetValue(r, out var b11) ? b11 : null)
                     : (map10.TryGetValue(r, out var b10) ? b10 : map11.TryGetValue(r, out var b) && r is "23H2" or "24H2" or "25H2" ? b : null);
    }
}
