using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>A 0–100 score per device from what the device list already knows, so the worst devices
/// can be sorted to the top. Each deduction carries a reason.</summary>
public static class HealthScore
{
    public sealed record Result(int Score, IReadOnlyList<string> Reasons)
    {
        public string Grade => Score >= 85 ? "Good" : Score >= 60 ? "Fair" : "Poor";
        public string ReasonsText => Reasons.Count == 0 ? "No problems found" : string.Join("\n", Reasons);
    }

    public static Result Compute(ManagedDevice d, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var score = 100;
        var reasons = new List<string>();
        void Deduct(int points, string why) { score -= points; reasons.Add($"−{points}  {why}"); }

        switch (d.ComplianceState)
        {
            case "noncompliant": Deduct(30, "Noncompliant"); break;
            case "inGracePeriod": Deduct(10, "In compliance grace period"); break;
            case "error" or "conflict": Deduct(20, $"Compliance {d.ComplianceState}"); break;
            case null or "" or "unknown": Deduct(5, "Compliance not evaluated"); break;
        }

        if (d.LastSyncDateTime is null) Deduct(30, "Never checked in");
        else
        {
            var days = (at - d.LastSyncDateTime.Value).TotalDays;
            if (days > 30) Deduct(30, $"No check-in for {(int)days} days");
            else if (days > 7) Deduct(20, $"No check-in for {(int)days} days");
            else if (days > 3) Deduct(8, $"No check-in for {(int)days} days");
        }

        if (d.IsEncrypted == false) Deduct(20, "BitLocker off");

        if (d.TotalStorageBytes is > 0 && d.FreeStorageBytes is >= 0)
        {
            var freeGb = d.FreeStorageBytes.Value / 1024d / 1024 / 1024;
            var pct = (double)d.FreeStorageBytes.Value / d.TotalStorageBytes.Value;
            if (freeGb < 5 || pct < 0.05) Deduct(15, $"Disk nearly full ({freeGb:0.0} GB free)");
            else if (freeGb < 15 || pct < 0.10) Deduct(7, $"Low disk space ({freeGb:0.0} GB free)");
        }

        var build = AppInstallCheck.BuildOf(d.OsVersion);
        if (build is not null && build < 22000) Deduct(15, "Windows 10 (out of support)");

        if (d.Datto is null) Deduct(10, "No Datto agent");
        else if (!d.Datto.Online && d.Datto.LastSeen is { } seen && (at - seen).TotalDays > 7) Deduct(5, "Datto agent offline over 7 days");

        if (string.IsNullOrWhiteSpace(d.UserPrincipalName)) Deduct(3, "No primary user");

        return new Result(Math.Clamp(score, 0, 100), reasons);
    }
}
