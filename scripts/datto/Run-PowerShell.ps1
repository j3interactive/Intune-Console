# Datto RMM component: Intune Console - Run PowerShell
# Category: Scripts - Script type: PowerShell - Runs as: System
# Variables:
#   ScriptB64   (String)  The PowerShell to run, Base64-encoded UTF-8. The console fills this in.
#
# Runs one PowerShell script as SYSTEM and prints its output, so the console can show it.
# Output is captured as text (wide, no truncation). Errors go to standard error.
# Exit code: 0 = ran without terminating errors, 1 = failed.

$ErrorActionPreference = 'Continue'

# Datto runs components in 32-bit PowerShell on some agents; relaunch in 64-bit so registry,
# Get-BitLockerVolume and friends see the real system.
if ($env:PROCESSOR_ARCHITEW6432 -eq 'AMD64' -and $PSCommandPath) {
    & "$env:WINDIR\SysNative\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    exit $LASTEXITCODE
}

$b64 = "$env:ScriptB64".Trim()
if (-not $b64) { [Console]::Error.WriteLine('No script was provided.'); exit 1 }

try {
    $code = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
}
catch {
    [Console]::Error.WriteLine("The script couldn't be decoded: $($_.Exception.Message)")
    exit 1
}

Write-Output "== $env:COMPUTERNAME - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') - running as $([Security.Principal.WindowsIdentity]::GetCurrent().Name) =="

$failed = $false
try {
    $block = [ScriptBlock]::Create($code)
    # 2>&1 keeps non-terminating errors in order with normal output; they're split out below.
    $results = & $block 2>&1
    foreach ($r in $results) {
        if ($r -is [System.Management.Automation.ErrorRecord]) {
            [Console]::Error.WriteLine($r.ToString())
        }
        else {
            # Wide text so tables aren't cut off at 80 columns.
            $r | Out-String -Width 220 -Stream | ForEach-Object { if ($_.TrimEnd()) { Write-Output $_.TrimEnd() } }
        }
    }
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { Write-Output "(last native command exit code: $LASTEXITCODE)" }
}
catch {
    [Console]::Error.WriteLine("Terminating error: $($_.Exception.Message)")
    $failed = $true
}

if ($failed) { exit 1 } else { exit 0 }
