namespace IntuneConsole.Core.Models;

/// <summary>A Windows device managed by Intune, optionally matched to a Datto RMM device.</summary>
public sealed class ManagedDevice
{
    public string Id { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string? UserPrincipalName { get; init; }
    public string? UserDisplayName { get; init; }
    public string? OsVersion { get; init; }
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public string? ComplianceState { get; init; }
    public string? JoinType { get; init; }
    public string? EnrollmentType { get; init; }
    public bool? IsEncrypted { get; init; }
    public DateTimeOffset? LastSyncDateTime { get; init; }
    public string? AzureAdDeviceId { get; init; }

    /// <summary>Matched Datto device, if any (by hostname).</summary>
    public DattoDevice? Datto { get; set; }

    public bool IsNoncompliant => string.Equals(ComplianceState, "noncompliant", StringComparison.OrdinalIgnoreCase);
    public bool IsInGracePeriod => string.Equals(ComplianceState, "inGracePeriod", StringComparison.OrdinalIgnoreCase);

    public bool IsStale(DateTimeOffset now, int days = 7) =>
        LastSyncDateTime is null || now - LastSyncDateTime.Value > TimeSpan.FromDays(days);

    public string DattoStatusText => Datto is null ? "Not in Datto" : Datto.Online ? "Online" : "Offline";

    public string ComplianceText => ComplianceState switch
    {
        null or "" => "Unknown",
        "compliant" => "Compliant",
        "noncompliant" => "Noncompliant",
        "inGracePeriod" => "In grace period",
        "configManager" => "ConfigMgr",
        "conflict" => "Conflict",
        "error" => "Error",
        _ => ComplianceState
    };

    public string LastSyncText => Humanize.Ago(LastSyncDateTime);
}

public sealed class DattoDevice
{
    public string Uid { get; init; } = "";
    public string Hostname { get; init; } = "";
    public bool Online { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public string? SiteName { get; init; }
    public string? PortalUrl { get; init; }
    public string? WebRemoteUrl { get; init; }
}

public sealed class DattoComponent
{
    public string Uid { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class DattoJobResult
{
    public string JobUid { get; init; } = "";
    public string Status { get; init; } = "";
    public bool IsFinished => Status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                           || Status.Equals("expired", StringComparison.OrdinalIgnoreCase)
                           || Status.Equals("failed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One row of the per-device app install status report.</summary>
public sealed class AppInstallStatus
{
    public string ApplicationId { get; init; } = "";
    public string AppName { get; set; } = "";
    public string? AppVersion { get; init; }
    public string InstallState { get; init; } = "";
    public string? InstallStateDetail { get; init; }
    public string? HexErrorCode { get; init; }
    public long? ErrorCode { get; init; }
    public DateTimeOffset? LastModified { get; init; }

    public bool IsFailed => InstallState.Contains("fail", StringComparison.OrdinalIgnoreCase)
                         || InstallState.Contains("error", StringComparison.OrdinalIgnoreCase);
    public bool IsPending => InstallState.Contains("pending", StringComparison.OrdinalIgnoreCase);

    public string Explanation => Diagnostics.ErrorCodes.Explain(HexErrorCode, ErrorCode) ?? (InstallStateDetail ?? "");
}

/// <summary>A configuration or compliance policy state on a device.</summary>
public sealed class PolicyState
{
    public string Kind { get; init; } = "";   // "Configuration" or "Compliance"
    public string DisplayName { get; init; } = "";
    public string State { get; init; } = "";
    public bool IsProblem => State is "conflict" or "error" or "nonCompliant" or "noncompliant";
    public string Explanation => State switch
    {
        "conflict" => "Two or more policies set this differently. The device can't satisfy both.",
        "error" => "The device reported an error applying this policy.",
        "nonCompliant" or "noncompliant" => "The device doesn't meet this policy's requirements.",
        _ => ""
    };
}

public sealed class RemediationScript
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public override string ToString() => DisplayName;
}

public sealed class LogCollectionRequest
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public DateTimeOffset? RequestedUtc { get; init; }
    public DateTimeOffset? ReceivedUtc { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public double? SizeInKb { get; init; }
    public string? InitiatedBy { get; init; }
    public bool IsCompleted => Status.Equals("completed", StringComparison.OrdinalIgnoreCase);
    public bool IsPending => Status.Equals("pending", StringComparison.OrdinalIgnoreCase);

    public override string ToString()
    {
        var when = (ReceivedUtc ?? RequestedUtc)?.ToLocalTime().ToString("g") ?? "?";
        var size = SizeInKb is > 0 ? $" · {SizeInKb.Value / 1024:0.0} MB" : "";
        return $"{when} · {Status}{size}";
    }
}

public sealed class ActionLogEntry
{
    public DateTimeOffset When { get; init; } = DateTimeOffset.Now;
    public string DeviceName { get; init; } = "";
    public string Action { get; init; } = "";
    public string Via { get; init; } = "";
    public string Outcome { get; set; } = "";
    public string WhenText => When.ToLocalTime().ToString("MMM d HH:mm");
}

public static class Humanize
{
    public static string Ago(DateTimeOffset? t)
    {
        if (t is null) return "Never";
        var d = DateTimeOffset.UtcNow - t.Value;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "Just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} d ago";
    }
}
