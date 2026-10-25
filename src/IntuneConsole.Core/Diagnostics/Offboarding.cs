using System.Text;
using IntuneConsole.Core.Assets;
using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Everything a leaver has, and how far along returning it is. Progress is worked out from
/// live data (asset status, Intune enrollment), so reopening the checklist shows where things stand.</summary>
public static class Offboarding
{
    public sealed class Item
    {
        public ManagedDevice? Device { get; init; }
        public AssetRecord? Asset { get; init; }
        public string DeviceName => Device?.DeviceName ?? Asset?.DeviceName ?? Asset?.SerialNumber ?? "";
        public string Serial => Asset?.SerialNumber ?? Device?.SerialNumber ?? "";
        public string Platform => Device?.OperatingSystem ?? (Asset is null ? "" : "Not in Intune");
        public string Source => Device is not null && Asset is not null ? "Intune + register"
            : Device is not null ? "Intune" : "Asset register";

        /// <summary>Returned: the register no longer has it assigned to the person (or it's marked lost).</summary>
        public bool Returned => Asset is not null && (string.IsNullOrWhiteSpace(Asset.AssignedTo) || Asset.Status == AssetStatus.Lost)
                                || Asset is null && Device is null;
        public bool Lost => Asset?.Status == AssetStatus.Lost;
        /// <summary>Removed from management: no longer in Intune.</summary>
        public bool RemovedFromIntune => Device is null;
        /// <summary>Done: back with IT (or written off) and no longer managed for this person.</summary>
        public bool Done => (Asset is null || Returned) && RemovedFromIntune;

        public string ReturnText => Asset is null ? (Device is null ? "–" : "Not in register")
            : Lost ? "Marked lost" : Returned ? $"Returned ({Asset.Status})" : $"Still assigned ({Asset.Status})";
        public string IntuneText => Device is null ? "Not in Intune"
            : $"Enrolled, last check-in {Device.LastSyncText}";
        public string NextStep =>
            Asset is null && Device is not null ? "Add to the register, then check it in when it's back"
            : !Returned ? "Collect the device, then check it in"
            : Device is not null && Device.IsWindows ? "Wipe (to reissue) or retire it in Intune"
            : Device is not null ? "Retire it in Intune (removes company data)"
            : "Done";
    }

    /// <summary>Devices from Intune (primary user) and the register (assigned to the person), merged by serial.</summary>
    public static List<Item> Build(DirectoryUser user, IEnumerable<ManagedDevice> userDevices, IEnumerable<AssetRecord>? assets)
    {
        var upn = user.UserPrincipalName ?? "";
        var assigned = (assets ?? []).Where(a => string.Equals(a.AssignedTo?.Trim(), upn, StringComparison.OrdinalIgnoreCase)).ToList();
        var allAssets = (assets ?? []).ToList();

        var items = new List<Item>();
        var usedSerials = new HashSet<string>();
        foreach (var d in userDevices)
        {
            var key = AssetRegister.Key(d.SerialNumber);
            var asset = Reconciler.IsRealSerial(d.SerialNumber)
                ? allAssets.FirstOrDefault(a => AssetRegister.Key(a.SerialNumber) == key)
                : null;
            if (asset is not null) usedSerials.Add(key);
            items.Add(new Item { Device = d, Asset = asset });
        }
        foreach (var a in assigned.Where(a => !usedSerials.Contains(AssetRegister.Key(a.SerialNumber))))
            items.Add(new Item { Asset = a });

        return items.OrderBy(i => i.Done).ThenBy(i => i.DeviceName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Summary(DirectoryUser user, IReadOnlyList<Item> items, bool sessionsRevoked, string by, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Offboarding: {user.DisplayName} <{user.UserPrincipalName}>");
        sb.AppendLine($"Account: {user.AccountText}{(sessionsRevoked ? " · sign-in sessions revoked" : "")}");
        sb.AppendLine($"Devices: {items.Count} · returned {items.Count(i => i.Returned)} · done {items.Count(i => i.Done)}");
        sb.AppendLine();
        foreach (var i in items)
            sb.AppendLine($"- {i.DeviceName} ({i.Serial}): {i.ReturnText}; {i.IntuneText}. Next: {i.NextStep}");
        sb.AppendLine();
        sb.Append($"Checked by {by}, {now.ToLocalTime():yyyy-MM-dd HH:mm} (Intune Troubleshooting Console)");
        return sb.ToString();
    }
}
