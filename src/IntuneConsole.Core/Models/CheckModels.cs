namespace IntuneConsole.Core.Models;

public enum CheckStatus { Pass, Fail, Warn, Info, Unknown }

/// <summary>One line in a step-by-step check (app install check, readiness check).</summary>
public sealed class CheckStep
{
    public string Name { get; init; } = "";
    public CheckStatus Status { get; init; }
    public string Detail { get; init; } = "";
    public string? Fix { get; init; }

    public string StatusText => Status switch
    {
        CheckStatus.Pass => "Pass",
        CheckStatus.Fail => "Fail",
        CheckStatus.Warn => "Check",
        CheckStatus.Info => "Info",
        _ => "Unknown",
    };
    /// <summary>Word the shared pill converters understand.</summary>
    public string Tone => Status switch
    {
        CheckStatus.Pass => "success",
        CheckStatus.Fail => "failed",
        CheckStatus.Warn => "pending",
        _ => "",
    };
}

public sealed class CheckResult
{
    public string Title { get; init; } = "";
    public string Verdict { get; init; } = "";
    public CheckStatus Overall { get; init; }
    public List<CheckStep> Steps { get; init; } = new();
}

// ----- Inputs for the app install check -----

public sealed class AppAssignment
{
    public string Intent { get; init; } = "";            // required, available, uninstall, availableWithoutEnrollment
    public string TargetType { get; init; } = "";        // group, exclusion, allDevices, allUsers
    public string? GroupId { get; init; }
    public string? GroupName { get; set; }
    public string? FilterId { get; init; }
    public string? FilterMode { get; init; }             // include, exclude, none
    public string? FilterName { get; set; }
    public string? FilterRule { get; set; }

    public bool IsExclusion => TargetType == "exclusion";
    public bool HasFilter => !string.IsNullOrEmpty(FilterId) && FilterMode is "include" or "exclude";
    public string TargetText => TargetType switch
    {
        "allDevices" => "All devices",
        "allUsers" => "All users",
        "exclusion" => $"Excluded: {GroupName ?? GroupId}",
        _ => GroupName ?? GroupId ?? "Unknown group",
    };
}

public sealed class AppRequirements
{
    public string AppName { get; init; } = "";
    public string AppType { get; init; } = "";            // e.g. win32LobApp
    public string? Architectures { get; init; }           // "x64,arm64"
    public string? MinimumWindowsRelease { get; init; }
    public int? MinimumFreeDiskMb { get; init; }
    public int? MinimumMemoryMb { get; init; }
    public List<string> CustomRequirementRules { get; init; } = new();   // e.g. "Registry rule", "Script rule"
    public List<(string Id, string Name)> Dependencies { get; init; } = new();
}

/// <summary>Device facts used by the app check and assignment filter evaluation.</summary>
public sealed class DeviceFacts
{
    public string? DeviceName { get; init; }
    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? OsVersion { get; init; }
    public string? Architecture { get; init; }            // x64, arm64, x86
    public long? FreeStorageBytes { get; init; }
    public long? PhysicalMemoryBytes { get; init; }
    public string? Ownership { get; init; }               // company, personal
    public string? DeviceCategory { get; init; }
    public string? EnrollmentProfileName { get; init; }
    public HashSet<string> DeviceGroupIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UserGroupIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasPrimaryUser { get; init; }
    /// <summary>False when group membership couldn't be read (missing permission).</summary>
    public bool GroupsKnown { get; init; } = true;
}
