<#
SWVAULT Windows build / installer / optional old-vault publication wizard.
Run from a trusted local checkout on Windows, in PowerShell 5.1+.
No automatic publication without an explicit typed PUBLISH confirmation.
#>
[CmdletBinding()]
param(
  [string]$Version = '1.4.1',
  [string]$TeamConfig = '',
  [switch]$InstallMissingTools,
  [switch]$PublishToOld,
  [switch]$Required
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
function Need([string]$text) {
    do { $value = Read-Host $text; if (-not $value.Trim()) { Write-Warning 'A value is required.' } } while (-not $value.Trim())
    return $value.Trim()
}
function CheckCommand([string]$cmd, [string]$wingetId) {
    if (Get-Command $cmd -ErrorAction SilentlyContinue) { return }
    if ($InstallMissingTools -and (Get-Command winget -ErrorAction SilentlyContinue)) {
       Write-Host "Installing $wingetId using winget. You may be prompted for elevation."
       & winget install --id $wingetId --exact --accept-source-agreements --accept-package-agreements
       if ($LASTEXITCODE -ne 0) { throw "Could not install $wingetId" }
       $env:PATH = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
    }
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "Missing $cmd. Install $wingetId, restart this terminal, then rerun." }
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $Version" }
if (-not $TeamConfig) { $TeamConfig = Need 'Path to exported team.json from NEW 1.4 VM' }
$TeamConfig = (Resolve-Path -LiteralPath $TeamConfig).Path
$team = Get-Content -LiteralPath $TeamConfig -Raw | ConvertFrom-Json
if (-not $team.vaultUrl -or -not $team.previousVaultUrl -or -not $team.directR2Uploads) {
    throw 'team.json must have vaultUrl, previousVaultUrl and directR2Uploads=true.'
}
$new = [uri]$team.vaultUrl; $old = [uri]$team.previousVaultUrl
if ($new.Scheme -ne 'https' -or $old.Scheme -ne 'https' -or $new.Host -eq $old.Host) {
    throw 'New and previous vault URLs must be distinct HTTPS origins.'
}
Write-Host "New server: $($new.AbsoluteUri)" -ForegroundColor Cyan
Write-Host "Old 1.2.9 feed: $($old.AbsoluteUri)" -ForegroundColor Yellow
CheckCommand dotnet 'Microsoft.DotNet.SDK.10'
CheckCommand git 'Git.Git'
& dotnet --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'dotnet is unavailable' }
if (-not ((& dotnet --list-sdks) -match '^10\.')) { throw 'Install .NET SDK 10.x first.' }
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    if ($InstallMissingTools -and (Get-Command winget -ErrorAction SilentlyContinue)) {
        & winget install --id JRSoftware.InnoSetup --exact --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup installation failed.' }
    }
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 is needed for a Windows Setup EXE. Install it and rerun.' }
Write-Host 'Restoring and running Windows tests...'
& dotnet restore .\SwVault.slnx
if ($LASTEXITCODE -ne 0) { throw 'Restore failed; no release created.' }
& dotnet test .\SwVault.slnx -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; no release created.' }
& dotnet build .\SwVault.slnx -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed; no release created.' }
Write-Host 'Packaging the Windows EXE and ZIP...'
& .\scripts\package.ps1 -Version $Version -TeamConfig $TeamConfig
if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
$exe = Get-ChildItem .\dist\*-Setup.exe | Where-Object { $_.Name -match [regex]::Escape($Version) } | Select-Object -First 1
if (-not $exe) { throw 'Setup EXE was not produced.' }
Write-Host "Local installer: $($exe.FullName)" -ForegroundColor Green
Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256 | Format-List
if (-not $PublishToOld) {
    Write-Host 'Build complete. NOT published to any vault. Pilot the EXE on one test PC first.'
    exit 0
}
Write-Warning 'Publishing requires an old 1.2.9 vault that is still reachable and accepting update uploads.'
$confirmation = Need 'Type PUBLISH to push this exact version to the old vault update feed'
if ($confirmation -cne 'PUBLISH') { throw 'Publication cancelled; installer remains in dist/.' }
$publishArgs = @{ Version = $Version; TeamConfig = $TeamConfig; Publish = $true; PublishToPreviousVault = $true; Notes = 'SWVAULT 1.4 migration; install now, URL cutover is separately gated.' }
if ($Required) { $publishArgs.Required = $true }
& .\scripts\package.ps1 @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publication command failed. Check the old vault feed before retrying.' }
Write-Host 'Published to OLD update feed. Users must still accept installation or use managed deployment.'
