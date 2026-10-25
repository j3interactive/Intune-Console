using System.Text;
using IntuneConsole.Core.Diagnostics;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Assets;

/// <summary>Pure logic for the asset register: joining records to Intune, building new records,
/// applying warranty data, recording changes and the built-in reports.</summary>
public static class AssetRegister
{
    /// <summary>Joins register records with Intune Windows devices by serial number. Devices with no
    /// record appear as "Not in register"; records with no device keep their saved details.</summary>
    public static List<AssetRow> Merge(IEnumerable<AssetRecord> records, IEnumerable<ManagedDevice> devices, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        // Newest Intune record per serial, so leftover duplicates don't create extra rows.
        var bySerial = devices.Where(d => d.IsWindows && Reconciler.IsRealSerial(d.SerialNumber))
            .GroupBy(d => Key(d.SerialNumber))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.LastSyncDateTime ?? DateTimeOffset.MinValue).First());

        var rows = new List<AssetRow>();
        var used = new HashSet<string>();
        foreach (var r in records)
        {
            var k = Key(r.SerialNumber);
            bySerial.TryGetValue(k, out var d);
            if (d is not null) used.Add(k);
            rows.Add(new AssetRow { Record = r, Device = d, Now = at });
        }
        foreach (var (k, d) in bySerial)
            if (!used.Contains(k)) rows.Add(new AssetRow { Device = d, Now = at });

        return rows.OrderBy(r => r.InRegister).ThenBy(r => r.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Key(string? serial) => (serial ?? "").Trim().ToUpperInvariant();

    /// <summary>A starting record for an Intune device that isn't in the register yet.</summary>
    public static AssetRecord NewRecordFor(ManagedDevice d) => new()
    {
        SerialNumber = Key(d.SerialNumber),
        Status = AssetStatus.InUse,
        AssignedTo = d.UserPrincipalName,
        AssignedName = d.UserDisplayName,
        Model = d.Model,
        DeviceName = d.DeviceName,
    };

    /// <summary>Copies warranty data onto a record. Returns true if anything changed.</summary>
    public static bool ApplyWarranty(AssetRecord r, WarrantyInfo w, DateTimeOffset checkedAt)
    {
        var changed = r.WarrantyEnd != w.WarrantyEnd || r.WarrantyLevel != w.ServiceLevel || r.ShipDate != w.ShipDate;
        if (w.WarrantyEnd is not null) r.WarrantyEnd = w.WarrantyEnd;
        if (w.ServiceLevel is not null) r.WarrantyLevel = w.ServiceLevel;
        if (w.ShipDate is not null) r.ShipDate = w.ShipDate;
        if (string.IsNullOrWhiteSpace(r.Model) && !string.IsNullOrWhiteSpace(w.Product)) r.Model = w.Product;
        r.WarrantyChecked = checkedAt;
        return changed;
    }

    /// <summary>History entries describing what changed between two versions of a record.</summary>
    public static List<AssetHistoryEntry> Changes(AssetRecord? before, AssetRecord after, string by, string? notes = null, string? eventOverride = null)
    {
        var list = new List<AssetHistoryEntry>();
        var serial = Key(after.SerialNumber);
        var when = DateTimeOffset.UtcNow;
        if (before is null)
        {
            list.Add(new AssetHistoryEntry { SerialNumber = serial, When = when, Event = eventOverride ?? "Created", ToValue = Describe(after), By = by, Notes = notes });
            return list;
        }

        if (!Same(before.AssignedTo, after.AssignedTo))
            list.Add(new AssetHistoryEntry
            {
                SerialNumber = serial, When = when, By = by, Notes = notes,
                Event = eventOverride ?? (string.IsNullOrWhiteSpace(after.AssignedTo) ? "Unassigned" : "Assigned"),
                FromValue = Person(before), ToValue = Person(after),
            });
        if (!Same(before.Status, after.Status))
            list.Add(new AssetHistoryEntry { SerialNumber = serial, When = when, By = by, Notes = notes, Event = "Status changed",
                FromValue = before.Status, ToValue = after.Status });

        var other = new List<string>();
        void Cmp(string name, object? a, object? b) { if (!Equals(Norm(a), Norm(b))) other.Add(name); }
        Cmp("asset tag", before.AssetTag, after.AssetTag);
        Cmp("location", before.Location, after.Location);
        Cmp("cost center", before.CostCenter, after.CostCenter);
        Cmp("purchase date", before.PurchaseDate?.Date, after.PurchaseDate?.Date);
        Cmp("cost", before.Cost, after.Cost);
        Cmp("supplier", before.Supplier, after.Supplier);
        Cmp("PO number", before.PONumber, after.PONumber);
        Cmp("notes", before.Notes, after.Notes);
        if (other.Count > 0)
            list.Add(new AssetHistoryEntry { SerialNumber = serial, When = when, By = by, Notes = notes, Event = "Edited", ToValue = string.Join(", ", other) });
        return list;
    }

    private static object? Norm(object? v) => v is string s ? (string.IsNullOrWhiteSpace(s) ? null : s.Trim()) : v;
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
    private static string? Person(AssetRecord r) => string.IsNullOrWhiteSpace(r.AssignedTo) ? null
        : string.IsNullOrWhiteSpace(r.AssignedName) ? r.AssignedTo : $"{r.AssignedName} ({r.AssignedTo})";
    private static string Describe(AssetRecord r) => string.Join(", ", new[] { r.Status, Person(r), r.AssetTag }.Where(s => !string.IsNullOrWhiteSpace(s)));

    // ---------- Reports ----------

    public const string ReportAll = "All";
    public const string ReportNotInRegister = "Not in register";
    public const string ReportAssignmentDiffers = "Assignment differs";
    public const string ReportWarrantySoon = "Warranty ends in 90 days";
    public const string ReportWarrantyExpired = "Warranty expired (in use)";
    public const string ReportOld = "Older than 4 years";
    public const string ReportLoanersOut = "Loaners out";
    public const string ReportInUseNotInIntune = "In use, not in Intune";
    public const string ReportNoWarranty = "Warranty unknown";

    public static readonly string[] Reports =
    [
        ReportAll, ReportNotInRegister, ReportAssignmentDiffers, ReportWarrantySoon, ReportWarrantyExpired, ReportOld,
        ReportLoanersOut, ReportInUseNotInIntune, ReportNoWarranty,
        .. AssetStatus.All,
    ];

    public static bool InReport(AssetRow r, string report) => report switch
    {
        ReportAll => true,
        ReportNotInRegister => !r.InRegister,
        ReportAssignmentDiffers => r.AssignmentDiffers,
        ReportWarrantySoon => r.InRegister && r.WarrantyExpiringSoon && r.Status is not (AssetStatus.Retired or AssetStatus.Lost),
        ReportWarrantyExpired => r.WarrantyExpired && r.Status is AssetStatus.InUse or AssetStatus.Loaner,
        ReportOld => r.AgeYears >= 4 && r.Status is not (AssetStatus.Retired or AssetStatus.Lost),
        ReportLoanersOut => r.Status == AssetStatus.Loaner && !string.IsNullOrWhiteSpace(r.Record?.AssignedTo),
        ReportInUseNotInIntune => r.InRegister && r.Device is null && r.Status == AssetStatus.InUse,
        ReportNoWarranty => r.InRegister && r.WarrantyEnd is null && r.Status is not (AssetStatus.Retired or AssetStatus.Lost),
        _ => r.Status == report,
    };

    public static bool Matches(AssetRow r, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;
        return new[] { r.SerialNumber, r.AssetTag, r.DeviceName, r.Model, r.AssignedName, r.Record?.AssignedTo, r.IntuneUser,
                       r.Record?.Location, r.Record?.CostCenter, r.Record?.PONumber, r.Record?.Notes }
            .Any(s => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);
    }

    // ---------- Handover sheet ----------

    /// <summary>A printable HTML handover sheet for checking a device out to (or back in from) someone.</summary>
    public static string HandoverSheet(AssetRecord r, string kind, string? personName, string? personUpn, string issuedBy,
        string? accessories, DateTimeOffset when, string organization = "")
    {
        static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>").Append(H(kind)).Append(" – ").Append(H(r.SerialNumber)).Append("</title>");
        sb.Append("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:40px;color:#15191E}h1{font-size:22px;margin:0 0 4px}");
        sb.Append(".muted{color:#5B6470}table{border-collapse:collapse;width:100%;margin:20px 0}td{border:1px solid #C9CED5;padding:8px 10px}");
        sb.Append("td:first-child{width:32%;background:#F4F5F7;font-weight:600}.sig{margin-top:48px;display:flex;gap:48px}");
        sb.Append(".sig div{flex:1;border-top:1px solid #15191E;padding-top:6px}@media print{button{display:none}}</style></head><body>");
        sb.Append("<button onclick=\"window.print()\" style=\"float:right;padding:8px 16px\">Print</button>");
        sb.Append("<h1>Device ").Append(H(kind.ToLowerInvariant())).Append("</h1>");
        sb.Append("<div class=\"muted\">").Append(H(organization)).Append(organization.Length > 0 ? " · " : "").Append(H(when.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm"))).Append("</div>");
        sb.Append("<table>");
        void Row(string k, string? v) => sb.Append("<tr><td>").Append(H(k)).Append("</td><td>").Append(H(string.IsNullOrWhiteSpace(v) ? "–" : v)).Append("</td></tr>");
        Row("Person", string.IsNullOrWhiteSpace(personUpn) ? personName : $"{personName} ({personUpn})");
        Row("Device", r.DeviceName);
        Row("Model", r.Model);
        Row("Service tag / serial", r.SerialNumber);
        Row("Asset tag", r.AssetTag);
        Row("Warranty until", r.WarrantyEnd?.ToLocalTime().ToString("yyyy-MM-dd"));
        Row("Accessories", accessories);
        Row("Handled by", issuedBy);
        Row("Notes", r.Notes);
        sb.Append("</table>");
        sb.Append(kind.StartsWith("Check-in", StringComparison.OrdinalIgnoreCase)
            ? "<p>The device and accessories listed above were returned in the condition noted.</p>"
            : "<p>I confirm I have received the device and accessories listed above in working order, and will look after them and return them when asked or when I leave.</p>");
        sb.Append("<div class=\"sig\"><div>Signature: person</div><div>Signature: IT</div><div>Date</div></div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
