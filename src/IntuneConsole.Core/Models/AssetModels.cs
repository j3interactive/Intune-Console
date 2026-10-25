namespace IntuneConsole.Core.Models;

public static class AssetStatus
{
    public const string InUse = "In use";
    public const string InStock = "In stock";
    public const string Loaner = "Loaner";
    public const string Repair = "Repair";
    public const string Retired = "Retired";
    public const string Lost = "Lost";

    public static readonly string[] All = [InUse, InStock, Loaner, Repair, Retired, Lost];
}

/// <summary>One row of the SharePoint "Assets" list. The serial number (Dell service tag) is the key.</summary>
public sealed class AssetRecord
{
    public string? ItemId { get; set; }                 // SharePoint list item ID; null until saved
    public string SerialNumber { get; set; } = "";      // stored in the list's Title column
    public string? AssetTag { get; set; }
    public string Status { get; set; } = AssetStatus.InUse;
    public string? AssignedTo { get; set; }             // UPN
    public string? AssignedName { get; set; }
    public string? Model { get; set; }
    public string? DeviceName { get; set; }
    public DateTimeOffset? PurchaseDate { get; set; }
    public double? Cost { get; set; }
    public string? Supplier { get; set; }
    public string? PONumber { get; set; }
    public string? Location { get; set; }
    public string? CostCenter { get; set; }
    public DateTimeOffset? ShipDate { get; set; }
    public DateTimeOffset? WarrantyEnd { get; set; }
    public string? WarrantyLevel { get; set; }
    public DateTimeOffset? WarrantyChecked { get; set; }
    public string? Notes { get; set; }

    public AssetRecord Clone() => (AssetRecord)MemberwiseClone();
}

/// <summary>One row of the SharePoint "Asset History" list.</summary>
public sealed class AssetHistoryEntry
{
    public string SerialNumber { get; init; } = "";
    public DateTimeOffset When { get; init; } = DateTimeOffset.UtcNow;
    public string Event { get; init; } = "";            // Created, Assigned, Checked out, Checked in, Status changed, Edited, Imported
    public string? FromValue { get; init; }
    public string? ToValue { get; init; }
    public string? By { get; init; }
    public string? Notes { get; init; }

    public string WhenText => When.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string ChangeText => FromValue is null && ToValue is null ? "" : $"{FromValue ?? "–"} → {ToValue ?? "–"}";
}

public sealed class WarrantyInfo
{
    public string ServiceTag { get; init; } = "";
    public DateTimeOffset? ShipDate { get; init; }
    public DateTimeOffset? WarrantyEnd { get; init; }
    public string? ServiceLevel { get; init; }
    public string? Product { get; init; }
    public bool Invalid { get; init; }
}

/// <summary>An asset record joined with its live Intune device (either may be missing).</summary>
public sealed class AssetRow
{
    public AssetRecord? Record { get; init; }
    public ManagedDevice? Device { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;

    public bool InRegister => Record is not null;
    public string SerialNumber => Record?.SerialNumber ?? Device?.SerialNumber ?? "";
    public string AssetTag => Record?.AssetTag ?? "";
    public string Status => Record?.Status ?? "Not in register";
    public string DeviceName => Device?.DeviceName ?? Record?.DeviceName ?? "";
    public string Model => Device?.Model ?? Record?.Model ?? "";
    public string AssignedName => Record?.AssignedName ?? Record?.AssignedTo ?? "";
    public string IntuneUser => Device?.UserDisplayName ?? Device?.UserPrincipalName ?? "";
    public string LastSeen => Device is null ? "Not in Intune" : Device.LastSyncText;

    public DateTimeOffset? WarrantyEnd => Record?.WarrantyEnd;
    public string WarrantyText => WarrantyEnd is null ? "Unknown"
        : WarrantyEnd < Now ? $"Expired {WarrantyEnd.Value.ToLocalTime():yyyy-MM-dd}"
        : $"{WarrantyEnd.Value.ToLocalTime():yyyy-MM-dd}";
    public bool WarrantyExpired => WarrantyEnd is not null && WarrantyEnd < Now;
    public bool WarrantyExpiringSoon => WarrantyEnd is not null && WarrantyEnd >= Now && WarrantyEnd < Now.AddDays(90);

    /// <summary>Age from Dell's ship date, else the purchase date, else Intune enrollment.</summary>
    public DateTimeOffset? AgeFrom => Record?.ShipDate ?? Record?.PurchaseDate ?? Device?.EnrolledDateTime;
    public double? AgeYears => AgeFrom is null ? null : (Now - AgeFrom.Value).TotalDays / 365.25;
    public string AgeText => AgeYears is null ? "" : $"{AgeYears:0.0} y";

    /// <summary>The register says one person, Intune's primary user says another.</summary>
    public bool AssignmentDiffers =>
        Record is not null && Device is not null && Record.Status == AssetStatus.InUse
        && !string.IsNullOrWhiteSpace(Device.UserPrincipalName)
        && !string.Equals(Record.AssignedTo?.Trim(), Device.UserPrincipalName?.Trim(), StringComparison.OrdinalIgnoreCase);

    public string Flag =>
        !InRegister ? "Not in register"
        : AssignmentDiffers ? "Assignment differs"
        : WarrantyExpired && Record!.Status is AssetStatus.InUse or AssetStatus.Loaner ? "Warranty expired"
        : WarrantyExpiringSoon ? "Warranty ends soon"
        : Device is null && Record!.Status == AssetStatus.InUse ? "In use, not in Intune"
        : "";
}
