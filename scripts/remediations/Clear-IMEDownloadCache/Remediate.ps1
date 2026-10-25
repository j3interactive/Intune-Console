# Intune Remediation: "Intune Console - Clear IME Download Cache" (remediation script)
#
# Like the restart remediation, the work happens in a one-time SYSTEM task so that
# stopping the IME doesn't kill this script before Intune records the result.
# The task stops the IME, deletes downloaded app content, starts the IME again,
# and removes itself. Installed apps are not touched.

$ErrorActionPreference = 'Stop'
$taskName = 'IntuneConsole-ClearIMECache'

$work = @'
$name = 'IntuneManagementExtension'
$imeRoot = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Intune Management Extension'
try {
    Stop-Service -Name $name -Force
    (Get-Service -Name $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    foreach ($f in @((Join-Path $imeRoot 'Content\Incoming'), (Join-Path $imeRoot 'Content\Staging'), (Join-Path $env:WINDIR 'IMECache'))) {
        if (Test-Path -LiteralPath $f) { Get-ChildItem -LiteralPath $f -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
finally {
    Start-Service -Name $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'IntuneConsole-ClearIMECache' -Confirm:$false
}
'@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($work))

try {
    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    $trigger   = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(30)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Write-Output 'IME cache clear scheduled in 30 seconds'
    exit 0
}
catch {
    Write-Output "Couldn't schedule the cache clear: $($_.Exception.Message)"
    exit 1
}
