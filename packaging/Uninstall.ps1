# Removes the Intune Troubleshooting Console.
# Intune Win32 app uninstall command:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall.ps1
#
# Per-admin settings (%LOCALAPPDATA%\IntuneConsole) and saved credentials are left in
# place so a reinstall picks them up; delete them by hand if you want a clean slate.

$ErrorActionPreference = 'SilentlyContinue'
$target    = Join-Path $env:ProgramFiles 'IntuneConsole'
$shortcut  = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Intune Troubleshooting Console.lnk'
$uninstKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IntuneConsole'

Get-Process -Name 'IntuneConsole' | Stop-Process -Force
Remove-Item -LiteralPath $shortcut -Force
Remove-Item -Path $uninstKey -Recurse -Force

# This script may be running from inside the install folder; delete files, then the folder.
Get-ChildItem -LiteralPath $target -Force | Where-Object { $_.FullName -ne $PSCommandPath } | Remove-Item -Recurse -Force
Remove-Item -LiteralPath $target -Recurse -Force

if (Test-Path -LiteralPath (Join-Path $target 'IntuneConsole.exe')) {
    Write-Output 'Uninstall incomplete: the app could not be removed.'
    exit 1
}
Write-Output 'Uninstalled.'
exit 0
