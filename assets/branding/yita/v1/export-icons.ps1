param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'yita-app-icon-master.png')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16,20,24,32,40,48,64,128,256,512,1024)
$iconSizes = @(16,20,24,32,40,48,64,128,256)
$source = [System.Drawing.Bitmap]::new($SourcePath)
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($source,[System.Drawing.Rectangle]::new(0,0,$size,$size),0,0,$source.Width,$source.Height,[System.Drawing.GraphicsUnit]::Pixel,$attributes)
            $bitmap.Save((Join-Path $PSScriptRoot "yita-icon-$size.png"),[System.Drawing.Imaging.ImageFormat]::Png)
        } finally {
            $attributes.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
} finally { $source.Dispose() }

$entries = foreach ($size in $iconSizes) {
    [PSCustomObject]@{ Size = $size; Data = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot "yita-icon-$size.png")) }
}
$stream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'yita.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($entry in $entries) {
        $dimension = if ($entry.Size -eq 256) { 0 } else { $entry.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $entry.Data.Length
    }
    foreach ($entry in $entries) { $writer.Write([byte[]]$entry.Data) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output 'Exported 11 PNG sizes and a 9-frame Windows ICO.'
