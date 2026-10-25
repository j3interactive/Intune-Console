# Datto RMM component: Intune Console - Clear IME Download Cache
# Category: Scripts - Script type: PowerShell - Runs as: System
# Variables: none
#
# Stops the Intune Management Extension, deletes its downloaded app content
# (partial or corrupt downloads are a common cause of stuck Win32 installs),
# then starts it again. Installed apps are not touched.

$ErrorActionPreference = 'Stop'
$name = 'IntuneManagementExtension'
$imeRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Intune Management Extension'
$folders = @(
    (Join-Path $imeRoot 'Content\Incoming'),
    (Join-Path $imeRoot 'Content\Staging'),
    (Join-Path $env:WINDIR 'IMECache')
)

try {
    if (-not (Get-Service -Name $name -ErrorAction SilentlyContinue)) {
        Write-Output "The Intune Management Extension isn't installed on this device."
        exit 1
    }

    Write-Output 'Stopping the Intune Management Extension...'
    Stop-Service -Name $name -Force
    (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))

    foreach ($f in $folders) {
        if (Test-Path -LiteralPath $f) {
            $items = Get-ChildItem -LiteralPath $f -Force -ErrorAction SilentlyContinue
            $count = @($items).Count
            $items | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            Write-Output "Cleared $count item(s) from $f"
        }
    }
}
catch {
    Write-Output "Problem while clearing the cache: $($_.Exception.Message)"
}
finally {
    # Always try to bring the service back.
    Start-Service -Name $name -ErrorAction SilentlyContinue
    $status = (Get-Service -Name $name -ErrorAction SilentlyContinue).Status
    Write-Output "Service status: $status"
}

if ($status -eq 'Running') { exit 0 } else { exit 1 }
