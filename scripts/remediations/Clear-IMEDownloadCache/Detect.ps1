# Intune Remediation: "Intune Console - Clear IME Download Cache" (detection script)
# Settings: Run as System = Yes - Run in 64-bit PowerShell = Yes - Don't assign to groups.
#
# Run on demand from the console; always reports "needs remediation" when the IME is installed.

if (Get-Service -Name 'IntuneManagementExtension' -ErrorAction SilentlyContinue) {
    Write-Output 'Cache clear requested'
    exit 1
}
Write-Output 'Intune Management Extension not installed'
exit 0
