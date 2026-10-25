# Datto RMM component: Intune Console - Reset Win32 App
# Category: Scripts - Script type: PowerShell - Runs as: System
# Variables:
#   AppId   (String)  The Intune app ID (GUID). The console fills this in.
#
# Clears the Intune Management Extension's stored result for one Win32 app
# (its Win32Apps registry entries and its retry/"GRS" record), then restarts
# the service so Intune re-evaluates the app and retries the install.
# It does not uninstall anything.

# The IME's registry keys live in the 64-bit view; relaunch in 64-bit PowerShell if needed.
if ($env:PROCESSOR_ARCHITEW6432 -eq 'AMD64' -and $PSCommandPath) {
    & "$env:WINDIR\SysNative\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
$name = 'IntuneManagementExtension'
$appId = "$env:AppId".Trim()
$guid = [guid]::Empty

if (-not [guid]::TryParse($appId, [ref]$guid)) {
    Write-Output "AppId must be an Intune app ID (GUID). Got: '$appId'"
    exit 1
}
$appId = $guid.ToString()

$base = 'HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps'
if (-not (Test-Path $base)) {
    Write-Output 'No Win32 app records found on this device.'
    exit 1
}

$removed = 0
try {
    Stop-Service -Name $name -Force -ErrorAction SilentlyContinue

    # One subkey per user SID (or 00000000-0000-0000-0000-000000000000 for device context).
    foreach ($userKey in Get-ChildItem -Path $base -ErrorAction SilentlyContinue) {
        # App result keys are named "<appId>" or "<appId>_<revision>".
        foreach ($k in Get-ChildItem -Path $userKey.PSPath -ErrorAction SilentlyContinue |
                       Where-Object { $_.PSChildName -like "$appId*" }) {
            Remove-Item -Path $k.PSPath -Recurse -Force
            Write-Output "Removed $($k.Name)"
            $removed++
        }

        # GRS (retry schedule) keys are hashed, so match on their contents.
        $grs = Join-Path $userKey.PSPath 'GRS'
        if (Test-Path $grs) {
            foreach ($g in Get-ChildItem -Path $grs -ErrorAction SilentlyContinue) {
                $props = Get-ItemProperty -Path $g.PSPath -ErrorAction SilentlyContinue
                $match = $false
                if ($props) {
                    foreach ($p in $props.PSObject.Properties) {
                        if ($p.Name -like "*$appId*" -or "$($p.Value)" -like "*$appId*") { $match = $true; break }
                    }
                }
                if ($match) {
                    Remove-Item -Path $g.PSPath -Recurse -Force
                    Write-Output "Removed retry record $($g.Name)"
                    $removed++
                }
            }
        }
    }
}
catch {
    Write-Output "Problem while resetting the app: $($_.Exception.Message)"
}
finally {
    Start-Service -Name $name -ErrorAction SilentlyContinue
}

if ($removed -eq 0) {
    Write-Output "Nothing found for app $appId on this device. It may not have been evaluated here yet."
    exit 0
}
Write-Output "Reset $removed record(s) for app $appId. The Intune Management Extension will re-evaluate it shortly."
exit 0
