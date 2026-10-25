using System.Text.RegularExpressions;
using IntuneConsole.Core.Logs;

namespace IntuneConsole.Core.Diagnostics;

public sealed class Finding
{
    public string Title { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string NextStep { get; init; } = "";
    public LogLine? Evidence { get; init; }
    public int Severity { get; init; } // higher = more important
    public string EvidenceText => Evidence is null ? "" : $"{Evidence.SourceFile} · {Evidence.TimeText} · {Evidence.Summary}";
}

/// <summary>Simple pattern rules that turn common IME log errors into a plain-English "likely cause".
/// They are hints, not verdicts: each finding shows the log line it matched.</summary>
public static class LikelyCauses
{
    private sealed record Rule(string Id, Regex Pattern, int Severity, string Title, string Explanation, string NextStep);

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Rule[] Rules =
    [
        new("detection", R(@"0x87D1041C|not detected after|detection.{0,60}(not detected|notdetected|is not detected)"), 90,
            "App installed, but the detection rule didn't find it",
            "The installer reported success, but the app's detection rule still says it's missing. Intune then reports 0x87D1041C and retries.",
            "Compare the detection rule (path, version or registry key) with what's actually on the device."),
        new("msi1603", R(@"\b1603\b|0x80070643"), 80,
            "MSI fatal error during install (1603)",
            "Windows Installer failed partway through. The cause is in the MSI's own log, not the IME log.",
            "Add /l*v to the install command (or open the MSI log in this bundle) and search for 'Return value 3'."),
        new("msi1618", R(@"\b1618\b|0x80070652"), 50,
            "Another install was already running (1618)",
            "Windows Installer only runs one install at a time. Intune will retry.",
            "Usually no action needed. If it keeps happening, look for a stuck msiexec process."),
        new("hash", R(@"hash (validation|mismatch|check) fail|hash doesn't match|failed to verify hash"), 85,
            "Downloaded content failed its integrity check",
            "The app package downloaded, but its hash didn't match, so the IME discarded it.",
            "Clear the IME download cache and retry. If it persists, re-upload the .intunewin."),
        new("download", R(@"download(ing)? .{0,40}(fail|error|timed out)|content download.{0,20}fail|0x87D1313C"), 70,
            "App content download failed",
            "The device couldn't download the app package from Intune's content network.",
            "Check proxy/firewall rules for Intune content URLs and Delivery Optimization; then retry."),
        new("timeout", R(@"(install|installation|process).{0,40}(timed out|timeout)"), 75,
            "Install timed out",
            "The installer ran longer than the app's maximum install time and was stopped.",
            "Check whether the installer is waiting for user input (needs a silent switch) or raise the timeout."),
        new("access", R(@"0x80070005|access is denied|access denied"), 60,
            "Access denied",
            "Something was blocked by permissions, often because the app runs in the wrong context (System vs User).",
            "Check the app's install behavior and any paths the installer writes to."),
        new("notfound", R(@"0x80070002|the system cannot find the file|file not found"), 55,
            "A file wasn't found",
            "Often the install command references a file name that isn't inside the package.",
            "Check the install command against the files in the .intunewin."),
        new("requirement", R(@"requirement rule.{0,40}(not met|false|failed)|applicability.{0,40}not ?applicable"), 40,
            "Device doesn't meet the app's requirement rules",
            "The app was skipped because a requirement rule (OS version, architecture, disk space, script) isn't met.",
            "Check the app's requirement rules against this device."),
        new("reboot", R(@"\b3010\b|0x80070BC2|pending reboot|reboot required"), 30,
            "Restart pending",
            "An install finished but Windows needs a restart before it's complete or before the next install.",
            "Restart the device, then sync."),
    ];

    public static List<Finding> Analyze(IEnumerable<LogLine> lines, int max = 5)
    {
        var found = new Dictionary<string, Finding>();
        foreach (var line in lines)
        {
            if (line.Level == LogLevel.Info && !line.Message.Contains("0x8", StringComparison.OrdinalIgnoreCase)
                && !line.Message.Contains("detect", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var rule in Rules)
            {
                if (!rule.Pattern.IsMatch(line.Message)) continue;
                // Keep the most recent evidence for each rule.
                if (found.TryGetValue(rule.Id, out var existing) && existing.Evidence?.Time > line.Time) continue;
                found[rule.Id] = new Finding
                {
                    Title = rule.Title,
                    Explanation = rule.Explanation,
                    NextStep = rule.NextStep,
                    Evidence = line,
                    Severity = rule.Severity,
                };
            }
        }
        return found.Values.OrderByDescending(f => f.Severity).ThenByDescending(f => f.Evidence?.Time).Take(max).ToList();
    }
}
