<#
.SYNOPSIS
  Installs SwVault on this PC. Run from the unzipped package (double-click "Install SwVault.cmd");
  asks for administrator rights once.

.DESCRIPTION
  Machine part (elevated):
  - Copies SwVault to C:\Program Files\SwVault (bin\ = agent, CLI and bundled Git; addin\ = SOLIDWORKS add-in).
  - Registers the SOLIDWORKS add-in (RegAsm /codebase) and adds swvault to PATH.
  User part (not elevated, so it lands in your own account even if an admin approved the prompt):
  - Starts the SwVault agent at sign-in, and starts it now.
  - Team packages (bin\team.json): the agent immediately asks for your user name and password
    and then downloads the vault. Nothing else to set up.
#>
[CmdletBinding()]
param(
    [string]$Target = (Join-Path $env:ProgramFiles 'SwVault'),
    [switch]$MachineOnly,
    # Where the elevated part writes what it did (the caller passes a file in the user's log folder).
    [string]$LogFile,
    # Run by the SwVault agent to install an update: no console prompts; problems show in a
    # message box, and the (old) agent is started again if the update doesn't go through.
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Pause-Exit([int]$Code) {
    if ($Quiet) {
        $agentExe = Join-Path $Target 'bin\SwVault.Agent.exe'
        # Not from this (update) folder: the agent would keep it locked and the next attempt couldn't replace it.
        if (Test-Path $agentExe) { Start-Process $agentExe -WorkingDirectory (Split-Path $agentExe) }
        exit $Code
    }
    Read-Host 'Press Enter to close'
    exit $Code
}

# Updates run without a console: a small window says what is happening instead.
$script:statusForm = $null
$script:statusLabel = $null
function Show-Status([string]$Text) {
    if (-not $Quiet) { return }
    try {
        if (-not $script:statusForm) {
            Add-Type -AssemblyName System.Windows.Forms, System.Drawing
            $form = New-Object System.Windows.Forms.Form
            $form.Text = 'SwVault - updating'
            $form.StartPosition = 'CenterScreen'
            $form.FormBorderStyle = 'FixedDialog'
            $form.MaximizeBox = $false
            $form.MinimizeBox = $false
            $form.ControlBox = $false
            $form.ClientSize = New-Object System.Drawing.Size(432, 96)
            $form.Font = [System.Drawing.SystemFonts]::MessageBoxFont
            $label = New-Object System.Windows.Forms.Label
            $label.SetBounds(16, 14, 400, 40)
            $bar = New-Object System.Windows.Forms.ProgressBar
            $bar.SetBounds(16, 60, 400, 18)
            $bar.Style = 'Marquee'
            $bar.MarqueeAnimationSpeed = 30
            $form.Controls.Add($label)
            $form.Controls.Add($bar)
            $script:statusForm = $form
            $script:statusLabel = $label
            $form.Show()
        }
        $script:statusLabel.Text = $Text
        [System.Windows.Forms.Application]::DoEvents()
    } catch {
        # The window is only a courtesy; the update goes on without it.
    }
}

function Close-Status {
    if ($script:statusForm) {
        try { $script:statusForm.Close() } catch { }
        $script:statusForm = $null
    }
}

function Warn([string]$Message) {
    if ($Quiet) {
        Close-Status
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show("SwVault update: $Message", 'SwVault', 'OK', 'Warning') | Out-Null
    } else {
        Write-Warning $Message
    }
}

# 0 = not running, 1 = running with no window (it didn't shut down completely), 2 = open.
function Get-SolidWorksState {
    $running = @(Get-Process SLDWORKS -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return 0 }
    if (@($running | Where-Object { $_.MainWindowHandle -ne 0 }).Count -gt 0) { return 2 }
    return 1
}

function Confirm-Step([string]$Question) {
    if ($Quiet) {
        Add-Type -AssemblyName System.Windows.Forms
        return [System.Windows.Forms.MessageBox]::Show($Question, 'SwVault', 'YesNo', 'Question') -eq 'Yes'
    }
    return (Read-Host "$Question [Y/n]") -notmatch '^\s*n'
}

if (-not $MachineOnly) {
    # SOLIDWORKS keeps the add-in loaded. One that is still running without a window can't be
    # closed by hand, so offer to end it.
    if ((Get-SolidWorksState) -eq 1 -and
        (Confirm-Step 'SOLIDWORKS is still running in the background (no window is open; it did not shut down completely). End it and continue?')) {
        Get-Process SLDWORKS -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
    if ((Get-SolidWorksState) -ne 0) {
        Warn 'Close SOLIDWORKS before installing, then run the installer again.'
        Pause-Exit 1
    }
    $bundledGit = Test-Path (Join-Path $here 'bin\git\cmd\git.exe')
    if (-not $bundledGit -and -not (Get-Command git -ErrorAction SilentlyContinue)) {
        Warn 'This package has no bundled Git and Git for Windows is not installed. Install it first (winget install Git.Git), then run this installer again.'
        Pause-Exit 1
    }
    Show-Status 'Stopping SwVault...'
    Get-Process SwVault.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
}

# ---------------------------------------------------------------- machine part (elevated)
function Write-InstallLog([string]$Text) {
    if (-not $LogFile) { return }
    try {
        New-Item -ItemType Directory -Force -Path (Split-Path $LogFile) | Out-Null
        Add-Content -Path $LogFile -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Text" -Encoding UTF8
    } catch { }
}

# Renames can fail for a moment while a just-stopped program lets go of its files.
function Move-WithRetry([string]$From, [string]$To) {
    for ($try = 1; ; $try++) {
        # A plain rename: all or nothing. (Move-Item falls back to copying, and moves half a folder when a file is in use.)
        try { [IO.Directory]::Move($From, $To); return }
        catch { if ($try -ge 10) { throw "'$From' is in use by another program and could not be replaced ($($_.Exception.InnerException.Message))" }; Start-Sleep -Milliseconds 500 }
    }
}

if ($isAdmin) {
    # The new files are copied next to the old ones and swapped in by renaming, so a copy that
    # fails half way (a file in use, antivirus, a full disk) leaves the working install alone.
    $swapped = @()
    try {
        Write-InstallLog "Installing from $here to $Target"
        New-Item -ItemType Directory -Force -Path $Target | Out-Null
        foreach ($name in 'bin', 'addin') {
            foreach ($leftover in "$name.new", "$name.old") {
                $path = Join-Path $Target $leftover
                if (Test-Path $path) { Remove-Item $path -Recurse -Force }
            }
            # A whole new folder (not copied over the old one), so an old bundled Git or team.json doesn't linger.
            Copy-Item (Join-Path $here $name) (Join-Path $Target "$name.new") -Recurse -Force
        }
        # Anything still running from the old bin (a stray git or swvault) would block the swap.
        $oldBin = (Join-Path $Target 'bin') + '\'
        Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($oldBin, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object {
            Write-InstallLog "Stopping $($_.Path) (pid $($_.Id))"
            try { $_ | Stop-Process -Force } catch { }
        }
        foreach ($name in 'bin', 'addin') {
            $current = Join-Path $Target $name
            if (Test-Path $current) { Move-WithRetry $current (Join-Path $Target "$name.old") }
            $swapped += $name
            Move-WithRetry (Join-Path $Target "$name.new") $current
        }
        Copy-Item (Join-Path $here 'uninstall.ps1') $Target -Force

        $regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
        # RegAsm always warns on its error stream that the add-in isn't signed (RA0000). With
        # ErrorActionPreference Stop, Windows PowerShell would turn that warning into a failure,
        # so only the exit code decides.
        $ErrorActionPreference = 'Continue'
        try { $registered = (& $regasm /codebase /nologo (Join-Path $Target 'addin\SwVault.AddIn.dll') 2>&1 | ForEach-Object { "$_" }) -join ' ' }
        finally { $ErrorActionPreference = 'Stop' }
        if ($LASTEXITCODE -ne 0) { throw "Registering the SOLIDWORKS add-in failed: $registered" }
        Write-InstallLog "Registered the add-in. $registered"
    } catch {
        Write-InstallLog "FAILED: $($_.Exception.Message)"
        # Put the old version back so SwVault still runs.
        foreach ($name in $swapped) {
            $current = Join-Path $Target $name
            $old = Join-Path $Target "$name.old"
            try {
                if (Test-Path $old) {
                    if (Test-Path $current) { Remove-Item $current -Recurse -Force }
                    [IO.Directory]::Move($old, $current)
                    Write-InstallLog "Restored the previous $name"
                }
            } catch { Write-InstallLog "Could not restore the previous ${name}: $($_.Exception.Message)" }
        }
        foreach ($name in 'bin', 'addin') {
            $new = Join-Path $Target "$name.new"
            try { if (Test-Path $new) { Remove-Item $new -Recurse -Force } } catch { }
        }
        if (Test-Path (Join-Path $Target 'addin\SwVault.AddIn.dll')) {
            try { & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe') /codebase /nologo (Join-Path $Target 'addin\SwVault.AddIn.dll') | Out-Null } catch { }
        }
        exit 3
    }
    foreach ($name in 'bin', 'addin') {
        # The old copy is only disk space now; a file still open in it mustn't fail the install.
        $old = Join-Path $Target "$name.old"
        try { if (Test-Path $old) { Remove-Item $old -Recurse -Force -ErrorAction Stop } } catch { Write-InstallLog "Left $name.old behind: $($_.Exception.Message)" }
    }
    Write-InstallLog 'Installed.'

    # Installed with the setup wizard: keep the version Windows lists under Installed apps current.
    $uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B0B5C9E2-6C0F-4B7B-9D56-5A1E3C1F7A10}_is1'
    $packageInfo = Join-Path $here 'swvault-package.json'
    if ((Test-Path $uninstallKey) -and (Test-Path $packageInfo)) {
        Set-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value (Get-Content $packageInfo -Raw | ConvertFrom-Json).version
    }

    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $binDir = Join-Path $Target 'bin'
    if ($machinePath -notlike "*$binDir*") { [Environment]::SetEnvironmentVariable('Path', "$machinePath;$binDir", 'Machine') }
} else {
    Show-Status 'Installing the update. Approve the Windows administrator prompt...'
    $installLog = Join-Path $env:LOCALAPPDATA 'SwVault\logs\install.log'
    $declined = $false
    try {
        $p = Start-Process powershell -Verb RunAs -PassThru -WindowStyle $(if ($Quiet) { 'Hidden' } else { 'Normal' }) `
            -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Target `"$Target`" -MachineOnly -LogFile `"$installLog`""
        $null = $p.Handle # keep the handle, or ExitCode is empty once the process has gone
        $shown = $false
        while (-not $p.WaitForExit(150)) {
            if ($Quiet) {
                if (-not $shown) { Show-Status 'Installing the update: copying files and registering the SOLIDWORKS add-in...'; $shown = $true }
                [System.Windows.Forms.Application]::DoEvents()
            }
        }
    } catch {
        $p = $null
        $declined = $true # the Windows prompt was declined (or couldn't be shown)
    }
    if ($declined) {
        Warn 'The Windows administrator prompt was declined, so nothing was installed. You can try again from the SwVault tray icon.'
        Pause-Exit 1
    }
    if ($p.ExitCode -ne 0) {
        $reason = ''
        try { $reason = (Get-Content $installLog -ErrorAction Stop | Where-Object { $_ -match 'FAILED: ' } | Select-Object -Last 1) -replace '^.*FAILED: ', '' } catch { }
        if (Test-Path (Join-Path $Target 'bin\SwVault.Agent.exe')) {
            Warn "The install did not finish$(if ($reason) { ": $reason" } else { '.' })`n`nYour previous SwVault was kept and is starting again. Close anything that may be using SwVault's files and try again from the tray icon. Details: $installLog"
        } else {
            Warn "The install did not finish$(if ($reason) { ": $reason" } else { '.' })`n`nSwVault is not installed correctly now. Install it again by running:`n$(Join-Path $here 'Install SwVault.cmd')`n`nDetails: $installLog"
        }
        Pause-Exit 1
    }
}
if ($MachineOnly) { exit 0 }

# ---------------------------------------------------------------- user part
$agent = Join-Path $Target 'bin\SwVault.Agent.exe'
New-Item -Path 'HKCU:\Software\SwVault' -Force | Out-Null
Set-ItemProperty -Path 'HKCU:\Software\SwVault' -Name 'AgentPath' -Value $agent
Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SwVaultAgent' -Value "`"$agent`""

# The website's buttons (Open in SOLIDWORKS...) are swvault:// links. The agent registers them when
# it starts; doing it here too means they work even before it has run.
try {
    $scheme = 'HKCU:\Software\Classes\swvault'
    New-Item -Path "$scheme\shell\open\command" -Force | Out-Null
    Set-ItemProperty -Path $scheme -Name '(default)' -Value 'URL:SwVault'
    Set-ItemProperty -Path $scheme -Name 'URL Protocol' -Value ''
    Set-ItemProperty -Path "$scheme\shell\open\command" -Name '(default)' -Value "`"$agent`" --link `"%1`""
} catch { }

# Downloaded through an invite link? The zip (and so the extracted folder) is named
# ...-invite-XXXX-XXXX-XXXX; save the code so the sign-in window has it filled in.
if ((Split-Path $here -Leaf) -match 'invite-([2-9A-HJ-NP-Z]{4}-[2-9A-HJ-NP-Z]{4}-[2-9A-HJ-NP-Z]{4})') {
    Set-ItemProperty -Path 'HKCU:\Software\SwVault' -Name 'InviteCode' -Value $Matches[1]
}

# Start the agent without elevation (explorer launches it as the signed-in user).
Show-Status 'Starting the new SwVault...'
if ($isAdmin) { Start-Process explorer.exe -ArgumentList "`"$agent`"" } else { Start-Process $agent -WorkingDirectory (Split-Path $agent) }
if ($Quiet) {
    # The new agent also says "SwVault updated to ...".
    $version = $null
    try { $version = (Get-Content (Join-Path $here 'swvault-package.json') -Raw | ConvertFrom-Json).version } catch { }
    Show-Status "SwVault is updated$(if ($version) { " to $version" }). You can open SOLIDWORKS again."
    $until = (Get-Date).AddSeconds(4)
    while ($script:statusForm -and (Get-Date) -lt $until) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
    Close-Status
    exit 0
}

$team = Join-Path $Target 'bin\team.json'
Write-Host ''
Write-Host 'SwVault installed.'
if (Test-Path $team) {
    $name = (Get-Content $team -Raw | ConvertFrom-Json).name
    Write-Host "A SwVault window is open: sign in, or (new here) choose a user name and password with your invite. Your $name files then start downloading."
    Write-Host 'Then open SOLIDWORKS: the SwVault tab and task pane are ready.'
} else {
    Write-Host 'Next: click the SwVault tray icon > Vaults... and connect with the URL, user name and token from your admin.'
    Write-Host 'Then open SOLIDWORKS: the SwVault tab and task pane are ready.'
}
Read-Host 'Press Enter to close'
