# Datto RMM component: Intune Console - Restart IME
# Category: Scripts - Script type: PowerShell - Runs as: System
# Variables: none
#
# Restarts the Intune Management Extension service, which makes it re-check
# Win32 apps, scripts and remediations straight away.

$ErrorActionPreference = 'Stop'
$name = 'IntuneManagementExtension'

try {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-Output "The Intune Management Extension isn't installed on this device. It installs automatically once a Win32 app, script or remediation is assigned."
        exit 1
    }

    Write-Output "Service status before: $($svc.Status)"
    Restart-Service -Name $name -Force
    $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
    $svc.Refresh()
    Write-Output "Service status after: $($svc.Status)"
    exit 0
}
catch {
    Write-Output "Failed to restart the Intune Management Extension: $($_.Exception.Message)"
    exit 1
}
