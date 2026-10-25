# Intune Win32 app detection script for the Intune Troubleshooting Console.
# build.ps1 stamps the version below to match the build. Intune treats the app as
# installed when this writes output and exits 0.

$required = [version]'__VERSION__'
$exe = Join-Path $env:ProgramFiles 'IntuneConsole\IntuneConsole.exe'

if (Test-Path -LiteralPath $exe) {
    $raw = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    $installed = $null
    if ([version]::TryParse(($raw -split '[ +]')[0], [ref]$installed) -and $installed -ge $required) {
        Write-Output "Installed $installed"
        exit 0
    }
}
exit 1
