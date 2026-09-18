[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Windows is required to convert the icon using System.Drawing.'
}
# Format conversion only: preserve the approved artwork and its alpha channel.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/TaskAssist.Desktop/Assets'
$source = [Drawing.Bitmap]::new((Join-Path $assets 'TaskAssist.png'))
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
try {
    if ($source.Width -ne $source.Height) { throw 'The source icon must be square.' }
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $buffer = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.DrawImage($source, [Drawing.Rectangle]::new(0, 0, $size, $size))
            $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($buffer.ToArray())
        }
        finally { $buffer.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $source.Dispose() }

$output = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()
    $target = Join-Path $assets 'TaskAssist.ico'
    [IO.File]::WriteAllBytes($target, $output.ToArray())
    Write-Host "Icon: $target ($($sizes -join ', ') px)"
}
finally { $writer.Dispose(); $output.Dispose() }
