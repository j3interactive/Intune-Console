using System.ComponentModel;
using System.Globalization;

namespace IntuneConsole.Core.Diagnostics;

/// <summary>Plain-English explanations for the error codes admins hit most with Win32 apps.
/// Anything not listed falls back to the Windows message for the underlying Win32 code.</summary>
public static class ErrorCodes
{
    private static readonly Dictionary<uint, string> Known = new()
    {
        [0x87D1041C] = "The installer finished, but the detection rule didn't find the app afterward. Usually a wrong path, version or registry key in the detection rule.",
        [0x80070643] = "The MSI hit a fatal error during install (MSI 1603). The MSI's own verbose log has the real reason.",
        [0x80070652] = "Another installation was already running (MSI 1618). Usually clears on the next retry.",
        [0x80070BC2] = "Install succeeded but needs a restart (3010). Check the app's restart behavior setting.",
        [0x80070BC9] = "Install needs a restart before it can finish (3017).",
        [0x800704C7] = "The install was cancelled, often by the user or by a timeout (1223).",
        [0x80070005] = "Access denied. Check whether the app is set to install as System or User.",
        [0x80070002] = "A file wasn't found. Often the install command points at a file name that isn't in the package.",
        [0x80073CF3] = "MSIX/Appx package failed update, dependency or conflict checks.",
        [0x87D1313C] = "The connection was lost while downloading the app content. Usually network-related; retries automatically.",
    };

    /// <summary>Returns an explanation for a hex string like "0x87D1041C" or a decimal code, or null.</summary>
    public static string? Explain(string? hex, long? dec = null)
    {
        uint? code = null;
        if (!string.IsNullOrWhiteSpace(hex))
        {
            var h = hex.Trim();
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
            if (uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)) code = parsed;
        }
        if (code is null && dec is not null and not 0) code = unchecked((uint)dec.Value);
        if (code is null or 0) return null;

        if (Known.TryGetValue(code.Value, out var known)) return known;

        // HRESULT_FROM_WIN32: 0x8007xxxx -> Win32 error xxxx; ask Windows for its message.
        if ((code.Value & 0xFFFF0000) == 0x80070000)
        {
            var win32 = (int)(code.Value & 0xFFFF);
            var msg = Win32Message(win32);
            if (msg is not null) return $"{msg} (Windows error {win32}).";
        }
        return null;
    }

    public static string Format(uint code) => $"0x{code:X8}";

    private static string? Win32Message(int code)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var m = new Win32Exception(code).Message;
        return string.IsNullOrWhiteSpace(m) || m.StartsWith("Unknown error", StringComparison.OrdinalIgnoreCase) ? null : m.TrimEnd('.');
    }
}
