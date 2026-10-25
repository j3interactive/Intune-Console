using System.Diagnostics.Eventing.Reader;
using IntuneConsole.Core.Logs;

namespace IntuneConsole.App.Services;

/// <summary>Reads an exported .evtx file from a diagnostics bundle into log lines (newest first).</summary>
public static class EventLogFile
{
    public static List<LogLine> Read(string path, int max = 5000)
    {
        var lines = new List<LogLine>();
        var query = new EventLogQuery(path, PathType.FilePath) { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        var name = Path.GetFileName(path);
        for (var i = 0; i < max; i++)
        {
            using var rec = reader.ReadEvent();
            if (rec is null) break;

            string message;
            try { message = rec.FormatDescription() ?? ""; }
            catch (EventLogException) { message = ""; }
            if (string.IsNullOrWhiteSpace(message))
                message = $"Event {rec.Id} (no message text available on this machine)";

            lines.Add(new LogLine
            {
                Time = rec.TimeCreated,
                Level = rec.Level switch { 1 or 2 => LogLevel.Error, 3 => LogLevel.Warning, _ => LogLevel.Info },
                Component = $"{rec.ProviderName} · {rec.Id}",
                Message = message,
                SourceFile = name,
                LineNumber = i + 1,
            });
        }
        return lines;
    }
}
