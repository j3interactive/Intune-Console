namespace IntuneConsole.Core.Models;

/// <summary>An Entra ID user, as shown in search results and on the user page.</summary>
public sealed class DirectoryUser
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? UserPrincipalName { get; init; }
    public string? Mail { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? OfficeLocation { get; init; }
    public string? EmployeeId { get; init; }
    public string? MobilePhone { get; init; }
    public bool? AccountEnabled { get; init; }
    public string? ManagerName { get; set; }
    public string? ManagerId { get; set; }

    /// <summary>One line under the name in search results: title · department · UPN.</summary>
    public string Subtitle => string.Join(" · ",
        new[] { JobTitle, Department, UserPrincipalName }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string AccountText => AccountEnabled switch { true => "Enabled", false => "Disabled", _ => "Unknown" };
}

/// <summary>One Entra sign-in event for a user.</summary>
public sealed class SignInEvent
{
    public DateTimeOffset? When { get; init; }
    public string App { get; init; } = "";
    public string? DeviceName { get; init; }
    public string? EntraDeviceId { get; init; }
    public string? OperatingSystem { get; init; }
    public string? Browser { get; init; }
    public string? IpAddress { get; init; }
    public string? City { get; init; }
    public string? Country { get; init; }
    public int ErrorCode { get; init; }
    public string? FailureReason { get; init; }
    public bool IsInteractive { get; init; }
    public bool? IsCompliant { get; init; }
    public string? UserId { get; init; }
    public string? UserPrincipalName { get; init; }
    public string? UserDisplayName { get; init; }

    public bool Succeeded => ErrorCode == 0;
    public string WhenText => When?.ToLocalTime().ToString("MMM d HH:mm") ?? "";
    public string ResultText => Succeeded ? "Success" : $"Failed ({ErrorCode})";
    public string LocationText => string.Join(", ", new[] { City, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string DeviceText => string.IsNullOrWhiteSpace(DeviceName) ? (OperatingSystem ?? "Unknown device") : DeviceName!;
    public string Detail => Succeeded ? (IsInteractive ? "Interactive" : "Non-interactive") : (FailureReason ?? "");
}

/// <summary>A device on the user page, with why it's linked to the user.</summary>
public sealed class UserDeviceLink
{
    public ManagedDevice Device { get; init; } = new();
    public bool IsPrimaryUser { get; init; }
    public DateTimeOffset? LastSignIn { get; init; }

    public string Relationship => IsPrimaryUser && LastSignIn is not null ? "Primary user · signed in recently"
        : IsPrimaryUser ? "Primary user"
        : "Signed in recently";
    public string LastSignInText => LastSignIn is null ? "–" : Humanize.Ago(LastSignIn);
}

/// <summary>A row in the global search results: either a person or a device.</summary>
public sealed class SearchResult
{
    public DirectoryUser? User { get; init; }
    public ManagedDevice? Device { get; init; }
    public bool IsUser => User is not null;
    public string Kind => IsUser ? "Person" : "Device";
    public string Title => User?.DisplayName ?? Device?.DeviceName ?? "";
    public string Subtitle => User?.Subtitle
        ?? string.Join(" · ", new[] { Device?.UserDisplayName, Device?.Model, Device?.SerialNumber }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}
