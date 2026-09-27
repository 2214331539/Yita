[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
Add-Type -AssemblyName System.Drawing
$canvas = [System.Drawing.Bitmap]::new(656, 1256)
$graphics = [System.Drawing.Graphics]::FromImage($canvas)
$mascot = [System.Drawing.Image]::FromFile((Join-Path $projectRoot 'assets\branding\yita\v1\yita-mascot.png'))
$brushes = @()
try {
    $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#EFE9DF'))
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $green = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#24756B'))
    $ink = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#302D29'))
    $muted = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#696158'))
    $brushes = @($green, $ink, $muted)
    $titleFont = [System.Drawing.Font]::new('Segoe UI', 72, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $captionFont = [System.Drawing.Font]::new('Segoe UI', 25, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    try {
        $graphics.FillRectangle($green, 64, 100, 64, 7)
        $graphics.DrawString('Yita', $titleFont, $ink, 58, 138)
        $graphics.DrawString('A little more understanding.', $captionFont, $muted, 64, 244)
        $graphics.DrawImage($mascot, [System.Drawing.Rectangle]::new(38, 390, 580, 580))
        $graphics.DrawString('YOUR READING COMPANION', $captionFont, $muted, 64, 1120)
    } finally { $titleFont.Dispose(); $captionFont.Dispose() }
    $canvas.Save((Join-Path $OutputDirectory 'wizard.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    foreach ($brush in $brushes) { $brush.Dispose() }
    $mascot.Dispose(); $graphics.Dispose(); $canvas.Dispose()
}
