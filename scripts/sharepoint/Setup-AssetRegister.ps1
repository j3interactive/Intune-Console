<#
.SYNOPSIS
  One-time setup for the console's asset register in SharePoint.

.DESCRIPTION
  1. Creates the "Assets" and "Asset History" lists on your IT site (skips any that already exist,
     and adds missing columns to existing lists).
  2. Grants the console's app registration "write" access to that one site (Sites.Selected).

  Run it once, as a SharePoint Administrator (or Global Administrator), on any PC:
    Install-Module Microsoft.Graph.Sites -Scope CurrentUser    # first time only
    .\Setup-AssetRegister.ps1 -SiteUrl https://contoso.sharepoint.com/sites/IT -ClientId <app registration client ID>

  Your admins also need Edit (Member) access to the site itself: with delegated Sites.Selected,
  each person can only do what both the app and they are allowed to do.

.PARAMETER SiteUrl
  The SharePoint site that will hold the register, e.g. https://contoso.sharepoint.com/sites/IT

.PARAMETER ClientId
  The Application (client) ID of the console's Entra app registration.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$SiteUrl,
    [Parameter(Mandatory)] [string]$ClientId,
    [string]$AssetsList = 'Assets',
    [string]$HistoryList = 'Asset History'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable Microsoft.Graph.Sites)) {
    throw 'Install the Microsoft Graph PowerShell module first: Install-Module Microsoft.Graph.Sites -Scope CurrentUser'
}
Import-Module Microsoft.Graph.Sites

Write-Host 'Signing in to Microsoft Graph (needs a SharePoint Administrator)...' -ForegroundColor Cyan
Connect-MgGraph -Scopes 'Sites.FullControl.All' -NoWelcome

# --- Find the site ---
$uri = [Uri]$SiteUrl.TrimEnd('/')
$sitePath = if ($uri.AbsolutePath -eq '/') { $uri.Host } else { "$($uri.Host):$($uri.AbsolutePath)" }
$site = Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/sites/$sitePath`?`$select=id,displayName"
Write-Host "Site: $($site.displayName) ($($site.id))"

$statusChoices = @('In use', 'In stock', 'Loaner', 'Repair', 'Retired', 'Lost')

$assetColumns = @(
    @{ name = 'AssetTag';        text = @{} }
    @{ name = 'Status';          choice = @{ allowTextEntry = $false; choices = $statusChoices; displayAs = 'dropDownMenu' } }
    @{ name = 'AssignedTo';      text = @{} }
    @{ name = 'AssignedName';    text = @{} }
    @{ name = 'Model';           text = @{} }
    @{ name = 'DeviceName';      text = @{} }
    @{ name = 'PurchaseDate';    dateTime = @{ format = 'dateOnly' } }
    @{ name = 'Cost';            number = @{ decimalPlaces = 'two' } }
    @{ name = 'Supplier';        text = @{} }
    @{ name = 'PONumber';        text = @{} }
    @{ name = 'Location';        text = @{} }
    @{ name = 'CostCenter';      text = @{} }
    @{ name = 'ShipDate';        dateTime = @{ format = 'dateOnly' } }
    @{ name = 'WarrantyEnd';     dateTime = @{ format = 'dateOnly' } }
    @{ name = 'WarrantyLevel';   text = @{} }
    @{ name = 'WarrantyChecked'; dateTime = @{ format = 'dateTime' } }
    @{ name = 'Notes';           text = @{ allowMultipleLines = $true } }
)

$historyColumns = @(
    @{ name = 'EventType'; text = @{} }
    @{ name = 'FromValue'; text = @{} }
    @{ name = 'ToValue';   text = @{} }
    @{ name = 'ChangedBy'; text = @{} }
    @{ name = 'EventDate'; dateTime = @{ format = 'dateTime' } }
    @{ name = 'Notes';     text = @{ allowMultipleLines = $true } }
)

function Ensure-List([string]$name, [array]$columns, [string]$titleLabel) {
    $base = "https://graph.microsoft.com/v1.0/sites/$($site.id)/lists"
    $filter = [Uri]::EscapeDataString("displayName eq '$($name.Replace("'", "''"))'")
    $existing = (Invoke-MgGraphRequest -Method GET -Uri "$base`?`$filter=$filter").value | Select-Object -First 1

    if (-not $existing) {
        Write-Host "Creating list '$name'..." -ForegroundColor Cyan
        $body = @{ displayName = $name; columns = $columns; list = @{ template = 'genericList' } } | ConvertTo-Json -Depth 6
        $existing = Invoke-MgGraphRequest -Method POST -Uri $base -Body $body -ContentType 'application/json'
    }
    else {
        Write-Host "List '$name' exists; checking columns..."
        $have = (Invoke-MgGraphRequest -Method GET -Uri "$base/$($existing.id)/columns?`$select=name").value.name
        foreach ($c in $columns | Where-Object { $have -notcontains $_.name }) {
            Write-Host "  adding column $($c.name)"
            Invoke-MgGraphRequest -Method POST -Uri "$base/$($existing.id)/columns" -Body ($c | ConvertTo-Json -Depth 6) -ContentType 'application/json' | Out-Null
        }
    }

    # Show "Serial number" instead of "Title" in SharePoint's own views.
    try {
        $title = (Invoke-MgGraphRequest -Method GET -Uri "$base/$($existing.id)/columns?`$filter=name eq 'Title'").value | Select-Object -First 1
        if ($title -and $title.displayName -ne $titleLabel) {
            Invoke-MgGraphRequest -Method PATCH -Uri "$base/$($existing.id)/columns/$($title.id)" -Body (@{ displayName = $titleLabel } | ConvertTo-Json) -ContentType 'application/json' | Out-Null
        }
    } catch { Write-Warning "Couldn't rename the Title column: $($_.Exception.Message)" }
}

Ensure-List -name $AssetsList  -columns $assetColumns   -titleLabel 'Serial number'
Ensure-List -name $HistoryList -columns $historyColumns -titleLabel 'Serial number'

# --- Grant the app write access to this site only ---
$perms = (Invoke-MgGraphRequest -Method GET -Uri "https://graph.microsoft.com/v1.0/sites/$($site.id)/permissions").value
$already = $perms | Where-Object { $_.grantedToIdentitiesV2.application.id -contains $ClientId -or $_.grantedToIdentities.application.id -contains $ClientId }
if ($already) {
    Write-Host 'The app already has access to this site.'
}
else {
    Write-Host 'Granting the app write access to this site...' -ForegroundColor Cyan
    $body = @{
        roles = @('write')
        grantedToIdentities = @(@{ application = @{ id = $ClientId; displayName = 'Intune Troubleshooting Console' } })
    } | ConvertTo-Json -Depth 5
    Invoke-MgGraphRequest -Method POST -Uri "https://graph.microsoft.com/v1.0/sites/$($site.id)/permissions" -Body $body -ContentType 'application/json' | Out-Null
}

Write-Host "`nDone. In the console: Settings > Asset register > enter $SiteUrl" -ForegroundColor Green
Write-Host 'Reminder: admins need Edit (Member) access to the site, and the app registration needs the delegated Sites.Selected permission with admin consent.'
