# Installs the Intune Troubleshooting Console for all users.
# Intune Win32 app install command:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
# Install behavior: System

$ErrorActionPreference = 'Stop'
$appName   = 'Intune Troubleshooting Console'
$target    = Join-Path $env:ProgramFiles 'IntuneConsole'
$exe       = Join-Path $target 'IntuneConsole.exe'
$shortcut  = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Intune Troubleshooting Console.lnk'
$uninstKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IntuneConsole'
$logDir    = Join-Path $env:ProgramData 'IntuneConsole'
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
Start-Transcript -Path (Join-Path $logDir 'install.log') -Append | Out-Null

try {
    $src = $PSScriptRoot
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    # Close any running copy so the exe can be replaced.
    Get-Process -Name 'IntuneConsole' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    Copy-Item -LiteralPath (Join-Path $src 'IntuneConsole.exe') -Destination $exe -Force
    foreach ($f in 'defaults.json', 'Uninstall.ps1') {
        $p = Join-Path $src $f
        if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $target -Force }
    }

    # Start Menu shortcut for everyone.
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($shortcut)
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $target
    $lnk.Description = $appName
    $lnk.Save()

    # Show in Settings > Apps.
    $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    New-Item -Path $uninstKey -Force | Out-Null
    $values = @{
        DisplayName     = $appName
        DisplayVersion  = "$version"
        Publisher       = 'Internal IT'
        InstallLocation = $target
        DisplayIcon     = $exe
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$target\Uninstall.ps1`""
        NoModify        = 1
        NoRepair        = 1
    }
    foreach ($k in $values.Keys) {
        $type = if ($values[$k] -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $uninstKey -Name $k -Value $values[$k] -PropertyType $type -Force | Out-Null
    }

    Write-Output "Installed $appName $version to $target"
    exit 0
}
catch {
    Write-Output "Install failed: $($_.Exception.Message)"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
