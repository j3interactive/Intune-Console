using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace IntuneConsole.Core.Logs;

public enum LogLevel { Info, Warning, Error }

public sealed class LogLine
{
    public DateTime? Time { get; init; }
    public LogLevel Level { get; init; }
    public string Component { get; init; } = "";
    public string Message { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public int LineNumber { get; init; }

    public string TimeText => Time?.ToString("MM-dd HH:mm:ss") ?? "";
    public string LevelText => Level switch { LogLevel.Error => "Error", LogLevel.Warning => "Warn", _ => "Info" };
    /// <summary>First line of the message, for the grid.</summary>
    public string Summary
    {
        get
        {
            var i = Message.IndexOfAny(['\r', '\n']);
            var s = i >= 0 ? Message[..i] : Message;
            return s.Length > 500 ? s[..500] + "…" : s;
        }
    }
}

/// <summary>Parses CMTrace-format logs (used by the Intune Management Extension) and falls back to
/// plain text with keyword-based levels for anything else.</summary>
public static class LogParser
{
    // <![LOG[message]LOG]!><time="10:18:02.1234567" date="10-22-2026" component="AppWorkload" context="" type="1" thread="12" file="">
    private static readonly Regex CmTrace = new(
        @"<!\[LOG\[(?<msg>.*?)\]LOG\]!><time=""(?<time>[^""]*)""\s+date=""(?<date>[^""]*)""\s+component=""(?<comp>[^""]*)""[^>]*?type=""(?<type>\d)""[^>]*>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ErrorWords = new(@"\b(error|failed|failure|exception|0x8[0-9a-f]{7})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WarnWords = new(@"\b(warn|warning|retry|timed out|timeout)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<LogLine> ParseFile(string path)
    {
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = sr.ReadToEnd();
        return Parse(text, Path.GetFileName(path));
    }

    public static List<LogLine> Parse(string text, string sourceName)
    {
        var lines = new List<LogLine>();
        var matches = CmTrace.Matches(text);
        if (matches.Count > 0)
        {
            var n = 0;
            foreach (Match m in matches)
            {
                n++;
                lines.Add(new LogLine
                {
                    Message = m.Groups["msg"].Value.Trim(),
                    Component = m.Groups["comp"].Value,
                    Time = ParseCmTime(m.Groups["date"].Value, m.Groups["time"].Value),
                    Level = m.Groups["type"].Value switch { "3" => LogLevel.Error, "2" => LogLevel.Warning, _ => LogLevel.Info },
                    SourceFile = sourceName,
                    LineNumber = n,
                });
            }
            return lines;
        }

        // Plain text: one entry per line.
        var i = 0;
        foreach (var raw in text.Split('\n'))
        {
            i++;
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            lines.Add(new LogLine
            {
                Message = line,
                Level = ErrorWords.IsMatch(line) ? LogLevel.Error : WarnWords.IsMatch(line) ? LogLevel.Warning : LogLevel.Info,
                SourceFile = sourceName,
                LineNumber = i,
                Time = TryLeadingTimestamp(line),
            });
        }
        return lines;
    }

    private static DateTime? ParseCmTime(string date, string time)
    {
        // time can carry a UTC offset suffix such as "+240" or "-300"; drop it.
        var t = time;
        var cut = t.IndexOfAny(['+', '-'], 1);
        if (cut > 0) t = t[..cut];
        var dot = t.IndexOf('.');
        if (dot > 0 && t.Length - dot > 4) t = t[..(dot + 4)]; // keep milliseconds only
        string[] formats = ["M-d-yyyy H:mm:ss.fff", "M-d-yyyy H:mm:ss", "M-d-yyyy H:mm:ss.ff", "M-d-yyyy H:mm:ss.f"];
        return DateTime.TryParseExact($"{date} {t}", formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt : null;
    }

    private static DateTime? TryLeadingTimestamp(string line)
    {
        if (line.Length < 19) return null;
        return DateTime.TryParse(line[..19], CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }
}

public enum BundleFileKind { Log, EventLog, Html, Text, Other }

public sealed class BundleFile
{
    public string FullPath { get; init; } = "";
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public BundleFileKind Kind { get; init; }
    public long SizeBytes { get; init; }
    public int? ErrorCount { get; set; }
    public int? WarningCount { get; set; }
    public bool IsImeLog => Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
                            (Folder.Contains("IntuneManagementExtension", StringComparison.OrdinalIgnoreCase) ||
                             ImeLogNames.Contains(Name));

    private static readonly HashSet<string> ImeLogNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "IntuneManagementExtension.log", "AppWorkload.log", "AppActionProcessor.log", "AgentExecutor.log",
        "HealthScripts.log", "ClientHealth.log", "Sensor.log", "DeviceHealthMonitoring.log", "Win32AppInventory.log",
    };

    public string Badge => ErrorCount is > 0 ? $"{ErrorCount} err" : WarningCount is > 0 ? $"{WarningCount} warn" : ErrorCount is 0 ? "clean" : "";
    public string Group => Kind switch
    {
        BundleFileKind.Log when IsImeLog => "1 · Intune Management Extension",
        BundleFileKind.Log => "2 · Other logs",
        BundleFileKind.EventLog => "3 · Event logs",
        BundleFileKind.Html => "4 · Reports",
        _ => "5 · Other files",
    };
}

/// <summary>An unpacked Intune diagnostics bundle on disk.</summary>
public sealed class LogBundle
{
    public string Folder { get; }
    public List<BundleFile> Files { get; }

    private LogBundle(string folder, List<BundleFile> files)
    {
        Folder = folder;
        Files = files;
    }

    /// <summary>Extracts the zip into <paramref name="targetFolder"/> (safely: no paths outside it) and indexes the files.</summary>
    public static LogBundle Extract(string zipPath, string targetFolder)
    {
        Directory.CreateDirectory(targetFolder);
        var root = Path.GetFullPath(targetFolder) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(targetFolder, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);

                // Some bundles nest zips (e.g. a zipped log folder); unpack one level.
                if (dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    try { Extract(dest, Path.Combine(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(dest))); }
                    catch (InvalidDataException) { }
                }
            }
        }
        return Open(targetFolder);
    }

    public static LogBundle Open(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var info = new FileInfo(p);
                var rel = Path.GetRelativePath(folder, info.DirectoryName ?? folder);
                return new BundleFile
                {
                    FullPath = p,
                    Name = info.Name,
                    Folder = rel == "." ? "" : rel,
                    SizeBytes = info.Length,
                    Kind = Classify(info.Extension),
                };
            })
            .OrderBy(f => f.Group).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new LogBundle(folder, files);
    }

    private static BundleFileKind Classify(string ext) => ext.ToLowerInvariant() switch
    {
        ".log" or ".lo_" => BundleFileKind.Log,
        ".evtx" => BundleFileKind.EventLog,
        ".html" or ".htm" => BundleFileKind.Html,
        ".txt" or ".reg" or ".xml" or ".csv" or ".json" or ".ini" or ".cab.txt" => BundleFileKind.Text,
        _ => BundleFileKind.Other,
    };

    /// <summary>Parses every text-based log and fills in error/warning counts.</summary>
    public Dictionary<BundleFile, List<LogLine>> ParseAll()
    {
        var result = new Dictionary<BundleFile, List<LogLine>>();
        foreach (var f in Files.Where(f => f.Kind is BundleFileKind.Log or BundleFileKind.Text && f.SizeBytes < 200_000_000))
        {
            try
            {
                var lines = LogParser.ParseFile(f.FullPath);
                if (f.Kind == BundleFileKind.Log)
                {
                    // Keyword-guessed levels in registry dumps and other text files are too noisy to count.
                    f.ErrorCount = lines.Count(l => l.Level == LogLevel.Error);
                    f.WarningCount = lines.Count(l => l.Level == LogLevel.Warning);
                }
                result[f] = lines;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
}
