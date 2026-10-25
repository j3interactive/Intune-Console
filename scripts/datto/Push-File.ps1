# Datto RMM component: Intune Console - Push File
# Category: Scripts - Script type: PowerShell - Runs as: System
# Variables (all String; the console fills them in):
#   FileUrl      Read-only link to the staged file in the file drop container
#   FileName     Name to save the file as
#   Destination  Folder on the device, e.g. C:\ProgramData\Contoso\
#   Sha256       Expected SHA-256 hash of the file (hex)
#   Overwrite    "true" or "false"
#
# Downloads the file, checks its hash, and only then moves it into place.

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$url       = "$env:FileUrl".Trim()
$fileName  = "$env:FileName".Trim()
$dest      = "$env:Destination".Trim().Trim('"')
$expected  = "$env:Sha256".Trim().ToUpperInvariant()
$overwrite = "$env:Overwrite".Trim() -eq 'true'

# --- Validate inputs ---
if ($url -notmatch '^https://[a-z0-9-]+\.blob\.core\.windows\.net/') {
    Write-Output 'FileUrl must be an https link to an Azure Blob Storage container.'; exit 1
}
if ([string]::IsNullOrWhiteSpace($fileName) -or $fileName -ne [IO.Path]::GetFileName($fileName) -or
    $fileName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    Write-Output "Invalid file name: '$fileName'"; exit 1
}
if ($dest -notmatch '^[A-Za-z]:\\' -or $dest.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0) {
    Write-Output "Destination must be a full local folder path, got: '$dest'"; exit 1
}
if ($expected -notmatch '^[0-9A-F]{64}$') {
    Write-Output 'Sha256 is missing or malformed.'; exit 1
}

$target = Join-Path ([IO.Path]::GetFullPath($dest)) $fileName
if ((Test-Path -LiteralPath $target) -and -not $overwrite) {
    Write-Output "$target already exists and Overwrite is off. Nothing changed."; exit 1
}

$temp = Join-Path $env:TEMP ("ic-" + [guid]::NewGuid().ToString('N'))
try {
    Write-Output "Downloading $fileName..."
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $temp -UseBasicParsing

    $actual = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $expected) {
        Write-Output "Hash mismatch. Expected $expected, got $actual. File discarded."
        exit 1
    }

    if (-not (Test-Path -LiteralPath $dest)) {
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        Write-Output "Created folder $dest"
    }
    Move-Item -LiteralPath $temp -Destination $target -Force
    $size = (Get-Item -LiteralPath $target).Length
    Write-Output "Saved $target ($size bytes, hash verified)."
    exit 0
}
catch {
    Write-Output "Push failed: $($_.Exception.Message)"
    exit 1
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
}
