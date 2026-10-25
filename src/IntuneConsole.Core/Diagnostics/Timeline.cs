using IntuneConsole.Core.Models;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Merges events from Intune, sign-ins, the console's own action log and the device record
/// into one list, newest first.</summary>
public static class Timeline
{
    public static List<TimelineEvent> Build(ManagedDevice device, IEnumerable<TimelineEvent>? intuneAudit,
        IEnumerable<SignInEvent>? userSignIns, IEnumerable<ActionLogEntry>? consoleActions,
        IEnumerable<LogCollectionRequest>? diagnostics, int maxSignIns = 40)
    {
        var events = new List<TimelineEvent>();

        if (device.EnrolledDateTime is { } enrolled)
            events.Add(new TimelineEvent { When = enrolled, Source = TimelineSource.Device, Title = "Enrolled in Intune",
                Detail = device.EnrollmentType });
        if (device.LastSyncDateTime is { } sync)
            events.Add(new TimelineEvent { When = sync, Source = TimelineSource.Device, Title = "Last Intune check-in" });
        if (device.Datto?.LastSeen is { } seen)
            events.Add(new TimelineEvent { When = seen, Source = TimelineSource.Device, Title = "Last seen by Datto",
                Detail = device.Datto.Online ? "Agent online" : "Agent offline" });

        if (intuneAudit is not null) events.AddRange(intuneAudit);

        if (userSignIns is not null && !string.IsNullOrEmpty(device.AzureAdDeviceId))
        {
            // Only sign-ins that happened on this device. Group repeats of the same app within an hour
            // so a chatty app doesn't flood the timeline.
            var onDevice = userSignIns
                .Where(s => s.When is not null && string.Equals(s.EntraDeviceId, device.AzureAdDeviceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.When)
                .ToList();
            var kept = new List<SignInEvent>();
            foreach (var s in onDevice)
            {
                var dup = s.Succeeded && kept.Any(k => k.Succeeded && k.App == s.App && (k.When!.Value - s.When!.Value).Duration() < TimeSpan.FromHours(1));
                if (!dup) kept.Add(s);
                if (kept.Count >= maxSignIns) break;
            }
            events.AddRange(kept.Select(s => new TimelineEvent
            {
                When = s.When!.Value,
                Source = TimelineSource.SignIn,
                Title = s.Succeeded ? $"Signed in to {s.App}" : $"Sign-in to {s.App} failed",
                Detail = s.Succeeded ? s.LocationText : $"{s.FailureReason} ({s.ErrorCode})",
                Actor = device.UserPrincipalName,
                IsFailure = !s.Succeeded,
            }));
        }

        if (consoleActions is not null)
            events.AddRange(consoleActions
                .Where(a => a.DeviceName.Equals(device.DeviceName, StringComparison.OrdinalIgnoreCase))
                .Select(a => new TimelineEvent
                {
                    When = a.When,
                    Source = TimelineSource.Console,
                    Title = $"{a.Action} (via {a.Via})",
                    Detail = a.Outcome,
                    Actor = a.User,
                    IsFailure = a.Outcome.Contains("fail", StringComparison.OrdinalIgnoreCase),
                }));

        if (diagnostics is not null)
            foreach (var d in diagnostics)
            {
                if (d.RequestedUtc is { } req)
                    events.Add(new TimelineEvent { When = req, Source = TimelineSource.Intune, Title = "Diagnostics requested",
                        Actor = d.InitiatedBy });
                if (d.ReceivedUtc is { } rec && d.IsCompleted)
                    events.Add(new TimelineEvent { When = rec, Source = TimelineSource.Device, Title = "Diagnostics uploaded",
                        Detail = d.SizeInKb is > 0 ? $"{d.SizeInKb.Value / 1024:0.0} MB" : null });
            }

        // The same console action can also appear in Intune's audit log; keep both, they carry different detail.
        return events.OrderByDescending(e => e.When).ToList();
    }
}
