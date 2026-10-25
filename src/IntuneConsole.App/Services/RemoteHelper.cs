using System.Diagnostics;
using IntuneConsole.Core.Models;

namespace IntuneConsole.App.Services;

/// <summary>Hands remote control off to Datto Web Remote (screen, PowerShell/terminal, file transfer).</summary>
public static class RemoteHelper
{
    public static void Open(ManagedDevice d)
    {
        var url = d.Datto?.WebRemoteUrl ?? d.Datto?.PortalUrl;
        if (d.Datto is null)
        {
            Shell.Status($"{d.DeviceName} isn't in Datto, so there's no remote session to open.", isError: true);
            return;
        }
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Shell.Status($"Datto didn't return a remote link for {d.DeviceName}. Open it in Datto instead.", isError: true);
            return;
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        Session.Current.Log(d.DeviceName, "Remote session", "Datto", d.Datto.WebRemoteUrl is null ? "Opened device in Datto" : "Opened Web Remote");
        if (!d.Datto.Online) Shell.Status($"{d.DeviceName} is offline in Datto; the session will connect when it's back.");
    }
}
