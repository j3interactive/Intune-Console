using System.Text;
using System.Text.Json;

namespace IntuneConsole.Core.Datto;

public sealed class Snippet
{
    public string Name { get; set; } = "";
    public string Script { get; set; } = "";
    public bool BuiltIn { get; set; }
    public override string ToString() => BuiltIn ? Name : $"{Name} (mine)";
}

/// <summary>Ready-made PowerShell commands for the device page, plus the admin's own saved ones.
/// Everything runs as SYSTEM on the device through the Datto "Run PowerShell" component.</summary>
public static class Snippets
{
    /// <summary>Datto component variables have a size limit; keep scripts comfortably under it.</summary>
    public const int MaxScriptLength = 6000;

    public static readonly IReadOnlyList<Snippet> BuiltIns =
    [
        new() { BuiltIn = true, Name = "Free disk space", Script =
            "Get-CimInstance Win32_LogicalDisk -Filter \"DriveType=3\" | Select-Object DeviceID, @{n='SizeGB';e={[math]::Round($_.Size/1GB,1)}}, @{n='FreeGB';e={[math]::Round($_.FreeSpace/1GB,1)}}, @{n='Free%';e={[math]::Round(100*$_.FreeSpace/$_.Size)}} | Format-Table -AutoSize" },
        new() { BuiltIn = true, Name = "Uptime and last restart", Script =
            "$os = Get-CimInstance Win32_OperatingSystem\n\"Last boot: $($os.LastBootUpTime)\"\n\"Uptime:    $((Get-Date) - $os.LastBootUpTime)\"" },
        new() { BuiltIn = true, Name = "Pending restart?", Script =
            "$p = @()\nif (Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing\\RebootPending') { $p += 'Component servicing' }\nif (Test-Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired') { $p += 'Windows Update' }\nif ((Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager' -Name PendingFileRenameOperations -ErrorAction SilentlyContinue).PendingFileRenameOperations) { $p += 'File rename' }\nif ($p) { \"Restart pending: $($p -join ', ')\" } else { 'No restart pending' }" },
        new() { BuiltIn = true, Name = "Entra join status (dsregcmd)", Script =
            "dsregcmd /status | Select-String 'AzureAdJoined|DomainJoined|DeviceId|TenantName|AzureAdPrt |MdmUrl|WorkplaceJoined|DeviceAuthStatus'" },
        new() { BuiltIn = true, Name = "Intune Management Extension status", Script =
            "Get-Service IntuneManagementExtension | Format-List Name, Status, StartType\n$log = \"$env:ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\IntuneManagementExtension.log\"\nif (Test-Path $log) { \"Log last written: $((Get-Item $log).LastWriteTime)\" }" },
        new() { BuiltIn = true, Name = "Installed apps (name, version)", Script =
            "$keys = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*','HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*'\nGet-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object DisplayName | Sort-Object DisplayName -Unique | Format-Table DisplayName, DisplayVersion, Publisher -AutoSize" },
        new() { BuiltIn = true, Name = "Signed-in users", Script = "quser 2>&1" },
        new() { BuiltIn = true, Name = "BitLocker status", Script =
            "Get-BitLockerVolume | Format-Table MountPoint, VolumeStatus, ProtectionStatus, EncryptionPercentage, EncryptionMethod -AutoSize" },
        new() { BuiltIn = true, Name = "TPM status", Script = "Get-Tpm | Format-List TpmPresent, TpmReady, TpmEnabled, TpmActivated, ManufacturerVersion" },
        new() { BuiltIn = true, Name = "Network adapters and IPs", Script =
            "Get-NetIPConfiguration | Where-Object IPv4Address | Format-List InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer" },
        new() { BuiltIn = true, Name = "Windows Update history (last 10)", Script =
            "$s = New-Object -ComObject Microsoft.Update.Session\n$h = $s.CreateUpdateSearcher()\n$h.QueryHistory(0, 10) | Select-Object Date, @{n='Result';e={switch($_.ResultCode){2{'Succeeded'}3{'Succeeded with errors'}4{'Failed'}5{'Aborted'}default{$_}}}}, Title | Format-Table -Wrap" },
        new() { BuiltIn = true, Name = "Group Policy / MDM policy result", Script =
            "Get-ChildItem 'HKLM:\\SOFTWARE\\Microsoft\\PolicyManager\\current\\device' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PSChildName" },
        new() { BuiltIn = true, Name = "Dell BIOS version and service tag", Script =
            "Get-CimInstance Win32_BIOS | Format-List Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SerialNumber" },
        new() { BuiltIn = true, Name = "Recent application errors (24 h)", Script =
            "Get-WinEvent -FilterHashtable @{LogName='Application'; Level=2; StartTime=(Get-Date).AddDays(-1)} -MaxEvents 25 -ErrorAction SilentlyContinue | Format-Table TimeCreated, ProviderName, Id, @{n='Message';e={$_.Message.Split(\"`n\")[0]}} -Wrap" },
    ];

    /// <summary>Base64 (UTF-8) so quotes, newlines and $ survive the trip through Datto's variable.</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.UTF8.GetBytes(script));

    public static List<Snippet> LoadMine(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException) { return new(); }
    }

    public static void SaveMine(string path, IEnumerable<Snippet> mine) =>
        File.WriteAllText(path, JsonSerializer.Serialize(mine.Where(s => !s.BuiltIn).ToList(), new JsonSerializerOptions { WriteIndented = true }));
}
