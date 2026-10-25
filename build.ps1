<#
.SYNOPSIS
  Builds the Intune Troubleshooting Console and packages it for Intune.

.DESCRIPTION
  1. Publishes a single, self-contained IntuneConsole.exe (no .NET install needed on admin PCs).
  2. Optionally code-signs it.
  3. Assembles out\package (exe + Install/Uninstall/Detect scripts + optional defaults.json).
  4. Wraps out\package into out\IntuneConsole.intunewin with Microsoft's Win32 Content Prep Tool.

  Needs the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0

.EXAMPLE
  .\build.ps1
.EXAMPLE
  .\build.ps1 -Version 0.4.0 -CertThumbprint 1A2B3C...   # sign with a cert in your store
#>
[CmdletBinding()]
param(
    [string]$Version = '0.3.0',
    [string]$CertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipIntuneWin
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$out     = Join-Path $root 'out'
$publish = Join-Path $out 'publish'
$package = Join-Path $out 'package'
$project = Join-Path $root 'src\IntuneConsole.App\IntuneConsole.App.csproj'

function Step($t) { Write-Host "`n==> $t" -ForegroundColor Cyan }

Step 'Checking .NET SDK'
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks -match '^10\.')) {
    throw 'The .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this again.'
}

Step 'Cleaning out\'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $publish, $package -Force | Out-Null

Step "Publishing IntuneConsole.exe $Version (self-contained, win-x64)"
& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:Version=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed. Copy the errors above and send them over.' }

$exe = Join-Path $publish 'IntuneConsole.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }

if ($CertThumbprint) {
    Step 'Signing'
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK, or build without -CertThumbprint.' }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $exe
    if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
}

Step 'Assembling package folder'
Copy-Item $exe $package
Copy-Item (Join-Path $root 'packaging\Install.ps1') $package
Copy-Item (Join-Path $root 'packaging\Uninstall.ps1') $package
$defaults = Join-Path $root 'packaging\defaults.json'
if (Test-Path $defaults) {
    Copy-Item $defaults $package
    Write-Host '   Included packaging\defaults.json (pre-filled settings for every admin).'
}
# Detection script stays outside the package: you upload it separately in Intune.
(Get-Content (Join-Path $root 'packaging\Detect.ps1') -Raw).Replace('__VERSION__', $Version) |
    Set-Content -Path (Join-Path $out 'Detect.ps1') -Encoding UTF8

if (-not $SkipIntuneWin) {
    Step 'Creating .intunewin'
    $tool = Join-Path $out 'IntuneWinAppUtil.exe'
    $toolUrl = 'https://raw.githubusercontent.com/microsoft/Microsoft-Win32-Content-Prep-Tool/master/IntuneWinAppUtil.exe'
    try {
        Invoke-WebRequest -Uri $toolUrl -OutFile $tool -UseBasicParsing
        $sig = Get-AuthenticodeSignature $tool
        if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
            throw "IntuneWinAppUtil.exe signature check failed ($($sig.Status))."
        }
        & $tool -c $package -s 'Install.ps1' -o $out -q | Out-Host
        $iw = Join-Path $out 'Install.intunewin'
        if (Test-Path $iw) { Rename-Item $iw 'IntuneConsole.intunewin' -Force }
    }
    catch {
        Write-Warning "Couldn't create the .intunewin automatically: $($_.Exception.Message)"
        Write-Warning 'Run IntuneWinAppUtil.exe yourself against out\package with Install.ps1 as the setup file.'
    }
}

Step 'Done'
Write-Host "  App:        $exe"
Write-Host "  Package:    $package"
if (Test-Path (Join-Path $out 'IntuneConsole.intunewin')) { Write-Host "  Intune:     $(Join-Path $out 'IntuneConsole.intunewin')" }
Write-Host "  Detection:  $(Join-Path $out 'Detect.ps1')"
Write-Host "`nTry it locally first: $exe"
