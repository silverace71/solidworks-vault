<#
.SYNOPSIS
  Makes team-icon.png (the tray and window icon of a team's SwVault build) from a logo SVG.

.DESCRIPTION
  Draws the logo in white on a dark rounded tile, so it shows on light and dark taskbars.
  Reads the simple SVGs that hold only filled paths with absolute M, L, C and Z commands
  (what the team's web logo, server/linux/invites/web/logo.svg, is).
  scripts\package.ps1 picks team-icon.png up from next to team.json.

.EXAMPLE
  .\scripts\make-team-icon.ps1 -Svg .\server\linux\invites\web\logo.svg -Out .\team-icon.png
#>
param(
    [Parameter(Mandatory)][string]$Svg,
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'team-icon.png'),
    [string]$Background = '#180e18',
    [string]$Foreground = '#ffffff',
    [int]$Size = 256
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$text = Get-Content $Svg -Raw
if ($text -notmatch 'viewBox="([\d.\s-]+)"') { throw "$Svg has no viewBox." }
$box = $Matches[1].Trim() -split '\s+' | ForEach-Object { [double]$_ }
$inv = [Globalization.CultureInfo]::InvariantCulture

$path = New-Object Drawing.Drawing2D.GraphicsPath ([Drawing.Drawing2D.FillMode]::Winding)
foreach ($d in [regex]::Matches($text, '<path d="([^"]+)"') | ForEach-Object { $_.Groups[1].Value }) {
    $current = $null
    foreach ($command in [regex]::Matches($d, '([MLCZ])([^MLCZ]*)')) {
        $n = @($command.Groups[2].Value.Trim() -split '[\s,]+' | Where-Object { $_ } | ForEach-Object { [single]::Parse($_, $inv) })
        switch ($command.Groups[1].Value) {
            'M' { $path.StartFigure(); $current = New-Object Drawing.PointF $n[0], $n[1] }
            'L' { $next = New-Object Drawing.PointF $n[0], $n[1]; $path.AddLine($current, $next); $current = $next }
            'C' {
                $next = New-Object Drawing.PointF $n[4], $n[5]
                $path.AddBezier($current, (New-Object Drawing.PointF $n[0], $n[1]), (New-Object Drawing.PointF $n[2], $n[3]), $next)
                $current = $next
            }
            'Z' { $path.CloseFigure() }
        }
    }
}

$bitmap = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [Drawing.Graphics]::FromImage($bitmap)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([Drawing.Color]::Transparent)

# The tile: a square with rounded corners.
$r = [single]($Size * 0.18); $e = [single]($Size - 1)
$tile = New-Object Drawing.Drawing2D.GraphicsPath
$tile.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90); $tile.AddArc($e - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
$tile.AddArc($e - 2 * $r, $e - 2 * $r, 2 * $r, 2 * $r, 0, 90); $tile.AddArc(0, $e - 2 * $r, 2 * $r, 2 * $r, 90, 90)
$tile.CloseFigure()
$g.FillPath((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($Background))), $tile)

# The logo, as large as fits with a small margin.
$margin = $Size * 0.06
$scale = [Math]::Min(($Size - 2 * $margin) / $box[2], ($Size - 2 * $margin) / $box[3])
$matrix = New-Object Drawing.Drawing2D.Matrix
$matrix.Translate([single](($Size - $box[2] * $scale) / 2 - $box[0] * $scale), [single](($Size - $box[3] * $scale) / 2 - $box[1] * $scale))
$matrix.Scale([single]$scale, [single]$scale)
$path.Transform($matrix)
$g.FillPath((New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($Foreground))), $path)
$g.Dispose()

$bitmap.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
Write-Host "Icon: $Out"
