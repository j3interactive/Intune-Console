using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Helpers for running one action on many devices.</summary>
public static class Bulk
{
    public sealed record Resolution(List<ManagedDevice> Matched, List<string> Unmatched);

    /// <summary>Turns a pasted list (one per line, or separated by commas, semicolons or tabs) of device
    /// names or serial numbers into devices. Where a serial has several records, the most recently
    /// active one is used.</summary>
    public static Resolution ResolveTargets(string pasted, IEnumerable<ManagedDevice> devices)
    {
        var all = devices.ToList();
        var byName = all.GroupBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.LastSyncDateTime ?? DateTimeOffset.MinValue).First(), StringComparer.OrdinalIgnoreCase);
        var bySerial = all.Where(d => Reconciler.IsRealSerial(d.SerialNumber))
            .GroupBy(d => d.SerialNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.LastSyncDateTime ?? DateTimeOffset.MinValue).First(), StringComparer.OrdinalIgnoreCase);

        var matched = new List<ManagedDevice>();
        var seen = new HashSet<string>();
        var unmatched = new List<string>();
        foreach (var raw in pasted.Split(new[] { '\n', '\r', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim('"', '\'', ' ');
            if (token.Length == 0) continue;
            var d = byName.GetValueOrDefault(token) ?? bySerial.GetValueOrDefault(token);
            if (d is null) { if (!unmatched.Contains(token, StringComparer.OrdinalIgnoreCase)) unmatched.Add(token); }
            else if (seen.Add(d.Id)) matched.Add(d);
        }
        return new Resolution(matched, unmatched);
    }

    /// <summary>Runs an action for each item with limited parallelism, reporting each result as it finishes.
    /// One failure doesn't stop the others.</summary>
    public static async Task RunAsync<T>(IEnumerable<T> items, Func<T, CancellationToken, Task<string>> action,
        Action<T, bool, string> onResult, int maxParallel = 4, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = items.Select(async item =>
        {
            var entered = false;
            try
            {
                await gate.WaitAsync(ct);
                entered = true;
                var msg = await action(item, ct);
                onResult(item, true, msg);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                onResult(item, false, "Cancelled");
            }
            catch (Exception ex)
            {
                onResult(item, false, ex.Message);
            }
            finally
            {
                if (entered) gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
    }
}
