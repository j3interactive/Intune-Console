# Intune Remediation: "Intune Console - Restart IME" (detection script)
# Settings: Run as System = Yes - Run in 64-bit PowerShell = Yes - Don't assign to groups.
#
# Meant to be run on demand from the console ("Run on this device"), so it always
# reports "needs remediation" when the IME is installed.

if (Get-Service -Name 'IntuneManagementExtension' -ErrorAction SilentlyContinue) {
    Write-Output 'Restart requested'
    exit 1
}
Write-Output 'Intune Management Extension not installed'
exit 0
