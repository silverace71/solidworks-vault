<#
.SYNOPSIS
  Builds the SwVault installers: dist\SwVault-<version>-Setup.exe and dist\SwVault-<version>.zip

.DESCRIPTION
  The setup wizard (-Setup.exe) is what people download and run; it needs Inno Setup 6 on this
  PC (winget install JRSoftware.InnoSetup), and without it only the zip is built. Installed
  SwVaults update themselves from the zip.

  Layout inside the zip:
    bin\       SwVault.Agent.exe and swvault.exe (self-contained .NET 10, no runtime to install)
    bin\git\   MinGit (portable Git for Windows), so members don't have to install Git
    bin\team.json   (with -TeamConfig) which vault to join; members then only sign in
    addin\     SwVault.AddIn.dll (+ SOLIDWORKS interop and protocol DLLs)
    Install SwVault.cmd / install.ps1 / uninstall.ps1 / INSTALL.txt
    swvault-package.json   {version}; the server reads it so agents can offer the update
  Without the wizard, teammates unzip it and double-click "Install SwVault.cmd" (one UAC prompt).

.PARAMETER TeamConfig
  team.json written by server/linux/setup.sh (or swvault-admin.sh client-config).

.PARAMETER Publish
  Upload the installers to the team's vault server, so invite links can offer the download
  (https://<server>/swvault-invites/download). Asks for your admin user name and password.
  Everyone's SwVault then offers the update (tray icon > Install update), so bump -Version.

.PARAMETER Required
  With -Publish: mark this update as required. SwVault keeps reminding people until they install it
  (use it when a change needs everyone on the new version).

.PARAMETER Notes
  With -Publish: one line shown with the update notification, e.g. "Adds responsible engineers".

.PARAMETER InviteCode
  Bake an invite code into this package (for a zip you hand out yourself, e.g. on a USB stick).
  Invite links from the server don't need this; they carry the code in the zip's name.

.PARAMETER MinGitUrl
  MinGit zip to bundle. Default: the newest 64-bit MinGit release of Git for Windows.
  Pass -MinGitUrl none to leave Git out (members then need Git for Windows installed).

.EXAMPLE
  .\scripts\package.ps1 -Version 0.2.0 -TeamConfig .\team.json -Publish
.EXAMPLE
  .\scripts\package.ps1 -Version 0.3.0 -TeamConfig .\team.json -Publish -Notes "Adds part numbering" -Required
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$TeamConfig,
    [string]$MinGitUrl,
    [switch]$Publish,
    [switch]$PublishToPreviousVault,
    [string]$InviteCode,
    [switch]$Required,
    [string]$Notes
)
if ($Publish -and -not $TeamConfig) { throw '-Publish needs -TeamConfig (it says which server to publish to).' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must look like 1.2.3 (got '$Version')." }

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $repo "dist\SwVault-$Version"
$zip = Join-Path $repo "dist\SwVault-$Version.zip"
if ($TeamConfig) {
    $teamName = ((Get-Content $TeamConfig -Raw | ConvertFrom-Json).name -replace '[^A-Za-z0-9_-]', '')
    if ($teamName) {
        $stage = Join-Path $repo "dist\SwVault-$teamName-$Version"
        $zip = Join-Path $repo "dist\SwVault-$teamName-$Version.zip"
    }
}
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'bin'), (Join-Path $stage 'addin') | Out-Null

$common = @('-c', 'Release', "-p:Version=$Version", '-nologo', '-v', 'q')
& dotnet publish (Join-Path $repo 'src\SwVault.Agent\SwVault.Agent.csproj') -r win-x64 --self-contained true -o (Join-Path $stage 'bin') @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing the agent failed.' }
& dotnet publish (Join-Path $repo 'src\SwVault.Cli\SwVault.Cli.csproj') -r win-x64 --self-contained true -o (Join-Path $stage 'bin') @common
if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed.' }
& dotnet build (Join-Path $repo 'src\SwVault.AddIn\SwVault.AddIn.csproj') @common
if ($LASTEXITCODE -ne 0) { throw 'Building the add-in failed.' }
$addInOut = (& dotnet msbuild (Join-Path $repo 'src\SwVault.AddIn\SwVault.AddIn.csproj') -getProperty:TargetDir -p:Configuration=Release).Trim()
Copy-Item (Join-Path $addInOut '*') (Join-Path $stage 'addin') -Recurse -Force

if ($TeamConfig) {
    $team = Get-Content $TeamConfig -Raw | ConvertFrom-Json
    if (-not $team.vaultUrl) { throw "$TeamConfig has no vaultUrl." }
    if ($InviteCode) {
        $team | Add-Member -NotePropertyName inviteCode -NotePropertyValue $InviteCode -Force
        $team | ConvertTo-Json | Set-Content (Join-Path $stage 'bin\team.json') -Encoding UTF8
        if ($Publish) { Write-Warning 'The published installer will carry this invite code; anyone who downloads it can use it.' }
    } else {
        Copy-Item $TeamConfig (Join-Path $stage 'bin\team.json')
    }
    Write-Host "Team: $($team.name) -> $($team.vaultUrl)"
}

if ($MinGitUrl -ne 'none') {
    if (-not $MinGitUrl) {
        $headers = @{ 'User-Agent' = 'SwVault-package' }
        if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" } # build servers share an address, and GitHub limits anonymous calls
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/git-for-windows/git/releases/latest' -Headers $headers
        $asset = $release.assets | Where-Object { $_.name -match '^MinGit-[\d.]+-64-bit\.zip$' } | Select-Object -First 1
        if (-not $asset) { throw "No 64-bit MinGit asset in Git for Windows $($release.tag_name); pass -MinGitUrl." }
        $MinGitUrl = $asset.browser_download_url
    }
    $cache = Join-Path $repo 'dist\cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $minGitZip = Join-Path $cache (Split-Path $MinGitUrl -Leaf)
    if (-not (Test-Path $minGitZip)) {
        Write-Host "Downloading $MinGitUrl"
        Invoke-WebRequest -Uri $MinGitUrl -OutFile $minGitZip -UseBasicParsing
    }
    Expand-Archive -Path $minGitZip -DestinationPath (Join-Path $stage 'bin\git') -Force
    if (-not (Test-Path (Join-Path $stage 'bin\git\cmd\git.exe'))) { throw "$minGitZip doesn't look like MinGit (no cmd\git.exe)." }
}

# A team's own tray and window icon: team-icon.png next to team.json (scripts\make-team-icon.ps1 makes one from a logo).
if ($TeamConfig) {
    $teamIcon = Join-Path (Split-Path (Resolve-Path $TeamConfig) -Parent) 'team-icon.png'
    if (Test-Path $teamIcon) { Copy-Item $teamIcon (Join-Path $stage 'bin\team-icon.png') }
}
Copy-Item (Join-Path $PSScriptRoot 'install\install.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\Install SwVault.cmd') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\uninstall.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'install\INSTALL.txt') $stage
@{ version = $Version } | ConvertTo-Json | Set-Content (Join-Path $stage 'swvault-package.json') -Encoding ASCII

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"

# The setup wizard, from the same staged files.
$setup = $null
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    $setupName = (Split-Path $stage -Leaf) + '-Setup'
    $defines = @("/DVersion=$Version", "/DStage=$stage", "/DOutDir=$(Split-Path $zip -Parent)", "/DOutName=$setupName")
    if ($TeamConfig) { $defines += "/DTeamName=$((Get-Content $TeamConfig -Raw | ConvertFrom-Json).name)" }
    Write-Host 'Building the setup wizard (about a minute)...'
    & $iscc /Q @defines (Join-Path $PSScriptRoot 'install\SwVault.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Building the setup wizard failed.' }
    $setup = Join-Path (Split-Path $zip -Parent) "$setupName.exe"
    Write-Host "Setup:   $setup ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"
} else {
    Write-Warning 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup), so only the zip was built; people then install with "Install SwVault.cmd".'
}

if ($Publish) {
    $targetTeam = Get-Content $TeamConfig -Raw | ConvertFrom-Json
    if ($PublishToPreviousVault) {
        if (-not $targetTeam.previousVaultUrl) { throw 'team.json must specify previousVaultUrl when publishing through SWVAULT 1.2.9.' }
        $vaultUrl = [Uri]$targetTeam.previousVaultUrl
        if ($vaultUrl.Scheme -ne 'https') { throw 'The old vault update destination must use HTTPS.' }
        Write-Warning "One-time migration: PUBLISHING to OLD vault $vaultUrl while PACKAGE TARGET remains $($targetTeam.vaultUrl)."
    } else { $vaultUrl = [Uri]$targetTeam.vaultUrl }
    $segments = $vaultUrl.AbsolutePath.Trim('/').Split('/')
    $prefix = ($segments | Select-Object -First ($segments.Length - 2)) -join '/'
    $base = $vaultUrl.GetLeftPart([UriPartial]::Authority) + '/' + $(if ($prefix) { "$prefix/" } else { '' })
    $credential = Get-Credential -Message "Admin sign-in for $base (to publish the installer)"
    $pair = "$($credential.UserName):$($credential.GetNetworkCredential().Password)"
    $headers = @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair)) }
    Write-Host "Uploading to $base ..."
    $query = @()
    if ($Required) { $query += 'required=1' }
    if ($Notes) { $query += 'notes=' + [Uri]::EscapeDataString($Notes) }
    $uri = "${base}swvault-invites/installer" + $(if ($query) { '?' + ($query -join '&') } else { '' })
    $published = Invoke-RestMethod -Method Put -Uri $uri -Headers $headers -InFile $zip -ContentType 'application/zip'
    if ($setup) {
        Invoke-RestMethod -Method Put -Uri "${base}swvault-invites/installer/setup?version=$($published.version)" -Headers $headers -InFile $setup -ContentType 'application/octet-stream' | Out-Null
    }
    Write-Host "Published SwVault $($published.version): ${base}swvault-invites/download"
    Write-Host "Everyone's SwVault offers the update within a few hours (or right away: tray icon > Check for updates)."
    Write-Host 'Invite people from SOLIDWORKS (SwVault tab > Invite People) or the tray icon.'
}
