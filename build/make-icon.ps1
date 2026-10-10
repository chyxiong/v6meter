# Regenerates src/V6Meter.App/app.ico: the tray badge (green rounded square with a white "6", see
# TrayIcons.cs) at the sizes Explorer asks for, stored as PNG frames.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-Frame([int]$size) {
    $bmp = [Drawing.Bitmap]::new($size, $size)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([Drawing.Color]::Transparent)
    $r = $size / 4.0
    $w = $size - 1
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(0x2E, 0x7D, 0x32))
    $g.FillPath($brush, $path)
    $font = [Drawing.Font]::new('Segoe UI', [float]($size * 0.62), [Drawing.FontStyle]::Bold, [Drawing.GraphicsUnit]::Pixel)
    $sf = [Drawing.StringFormat]::new()
    $sf.Alignment = 'Center'
    $sf.LineAlignment = 'Center'
    $g.DrawString('6', $font, [Drawing.Brushes]::White, [Drawing.RectangleF]::new(0, 0, $size, $size + 1), $sf)
    $g.Dispose()
    $ms = [IO.MemoryStream]::new()
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = $sizes | ForEach-Object { , (New-Frame $_) }
$out = [IO.MemoryStream]::new()
$bw = [IO.BinaryWriter]::new($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i] % 256 # 256 is stored as 0
    $bw.Write([byte]$s); $bw.Write([byte]$s); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
$target = Join-Path $PSScriptRoot '..\src\V6Meter.App\app.ico'
[IO.File]::WriteAllBytes($target, $out.ToArray())
"wrote $((Resolve-Path $target).Path) ($($out.Length) bytes)"
