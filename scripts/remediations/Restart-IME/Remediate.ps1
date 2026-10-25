# Intune Remediation: "Intune Console - Restart IME" (remediation script)
#
# Remediations are run BY the Intune Management Extension, so restarting it directly
# would kill this script mid-run and Intune would never get the result. Instead this
# schedules a one-time SYSTEM task, 30 seconds out, that restarts the service and
# then deletes itself.

$ErrorActionPreference = 'Stop'
$taskName = 'IntuneConsole-RestartIME'

$work = @'
Restart-Service -Name IntuneManagementExtension -Force
Unregister-ScheduledTask -TaskName 'IntuneConsole-RestartIME' -Confirm:$false
'@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($work))

try {
    $action    = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    $trigger   = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(30)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Write-Output 'IME restart scheduled in 30 seconds'
    exit 0
}
catch {
    Write-Output "Couldn't schedule the restart: $($_.Exception.Message)"
    exit 1
}
