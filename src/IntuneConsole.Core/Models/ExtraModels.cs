namespace IntuneConsole.Core.Models;

/// <summary>A local admin account password backed up by Windows LAPS.</summary>
public sealed class LapsCredential
{
    public string AccountName { get; init; } = "";
    public string Password { get; init; } = "";
    public DateTimeOffset? BackupTime { get; init; }
}

public sealed class LapsInfo
{
    public string DeviceName { get; init; } = "";
    public DateTimeOffset? LastBackup { get; init; }
    public DateTimeOffset? NextRotation { get; init; }
    /// <summary>Newest first; the first entry is the current password.</summary>
    public List<LapsCredential> Credentials { get; init; } = new();
}

public sealed class BitLockerKey
{
    public string Id { get; init; } = "";
    public DateTimeOffset? Created { get; init; }
    public string VolumeType { get; init; } = "";
    public string? Key { get; set; }

    public string VolumeText => VolumeType switch
    {
        "1" or "operatingSystemVolume" => "OS drive",
        "2" or "fixedDataVolume" => "Fixed data drive",
        "3" or "removableDataVolume" => "Removable drive",
        _ => "Drive",
    };
    /// <summary>First 8 characters of the key ID, which BitLocker shows on the recovery screen.</summary>
    public string ShortId => Id.Length >= 8 ? Id[..8].ToUpperInvariant() : Id.ToUpperInvariant();
}

/// <summary>An Entra ID device record.</summary>
public sealed class EntraDevice
{
    public string ObjectId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? OperatingSystem { get; init; }
    public string? TrustType { get; init; }     // AzureAd (joined), ServerAd (hybrid), Workplace (registered)
    public bool? IsManaged { get; init; }
    public bool? AccountEnabled { get; init; }
    public DateTimeOffset? LastSignIn { get; init; }
    public bool IsWindows => OperatingSystem?.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class AutopilotDevice
{
    public string Id { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string? GroupTag { get; init; }
    public string? EnrollmentState { get; init; }
    public string? Model { get; init; }
    public string? ManagedDeviceId { get; init; }
    public DateTimeOffset? LastContacted { get; init; }
}

public enum FindingSeverity { Info = 0, Warning = 1, Problem = 2 }

/// <summary>One mismatch found when comparing Intune, Entra ID, Autopilot and Datto.</summary>
public sealed class ReconciliationFinding
{
    public string Category { get; init; } = "";
    public FindingSeverity Severity { get; init; }
    public string DeviceName { get; init; } = "";
    public string? SerialNumber { get; init; }
    public string Detail { get; init; } = "";
    public string Suggestion { get; init; } = "";
    public ManagedDevice? IntuneDevice { get; init; }

    public string SeverityText => Severity switch
    {
        FindingSeverity.Problem => "Problem",
        FindingSeverity.Warning => "Warning",
        _ => "Info",
    };
}

public enum TimelineSource { Intune, SignIn, Console, Device }

public sealed class TimelineEvent
{
    public DateTimeOffset When { get; init; }
    public TimelineSource Source { get; init; }
    public string Title { get; init; } = "";
    public string? Detail { get; init; }
    public string? Actor { get; init; }
    public bool IsFailure { get; init; }

    public string WhenText => When.ToLocalTime().ToString("MMM d HH:mm");
    public string SourceText => Source switch
    {
        TimelineSource.Intune => "Intune",
        TimelineSource.SignIn => "Sign-in",
        TimelineSource.Console => "Console",
        _ => "Device",
    };
    public string ResultText => IsFailure ? "Failed" : "";
}
