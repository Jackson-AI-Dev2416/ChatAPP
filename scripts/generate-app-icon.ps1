$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "src\Ts3Messenger\Assets\app.ico"

Add-Type -AssemblyName System.Drawing

function Add-RoundRect([System.Drawing.Drawing2D.GraphicsPath]$path, [single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $d = $r * 2
    if ($d -gt $w) { $d = $w }
    if ($d -gt $h) { $d = $h }
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc(($x + $w - $d), $y, $d, $d, 270, 90)
    $path.AddArc(($x + $w - $d), ($y + $h - $d), $d, $d, 0, 90)
    $path.AddArc($x, ($y + $h - $d), $d, $d, 90, 90)
    $path.CloseFigure()
}

function New-Ts3ChatBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $u = $size / 64.0
    $radius = [single](18 * $u)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $tile 0 0 ([single]$size) ([single]$size) $radius

    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new($size, $size),
        [System.Drawing.Color]::FromArgb(255, 56, 189, 248),
        [System.Drawing.Color]::FromArgb(255, 79, 70, 229))
    $g.FillPath($grad, $tile)

    $sheenPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sheenPath.AddEllipse([single](-0.15 * $size), [single](-0.35 * $size), [single](0.95 * $size), [single](0.95 * $size))
    $sheen = New-Object System.Drawing.Drawing2D.PathGradientBrush $sheenPath
    $sheen.CenterColor = [System.Drawing.Color]::FromArgb(90, 255, 255, 255)
    $sheen.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.SetClip($tile)
    $g.FillPath($sheen, $sheenPath)
    $g.ResetClip()

    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = [single](13 * $u)
    $by = [single](13 * $u)
    $bw = [single](38 * $u)
    $bh = [single](28 * $u)
    $br = [single](13 * $u)
    $shadow = New-Object System.Drawing.Drawing2D.GraphicsPath
    Add-RoundRect $shadow $bx ([single]($by + 1.5 * $u)) $bw $bh $br
    $shade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(36, 15, 23, 42))
    $g.FillPath($shade, $shadow)
    Add-RoundRect $body $bx $by $bw $bh $br
    $g.FillPath([System.Drawing.Brushes]::White, $body)
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]((24 - 5.5) * $u), [single]((42 - 5.5) * $u), [single](11 * $u), [single](11 * $u))
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]((17 - 3.4) * $u), [single]((48 - 3.4) * $u), [single](6.8 * $u), [single](6.8 * $u))

    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 67, 56, 202))
    $dot = [single](6.3 * $u)
    foreach ($x in @(24.0, 32.0, 40.0)) {
        $g.FillEllipse($ink, [single](($x * $u) - ($dot / 2)), [single]((27 * $u) - ($dot / 2)), $dot, $dot)
    }

    $g.Dispose()
    $grad.Dispose()
    $sheen.Dispose()
    $shade.Dispose()
    $ink.Dispose()
    $tile.Dispose()
    $body.Dispose()
    $shadow.Dispose()
    $sheenPath.Dispose()
    return $bmp
}

function Write-PngIco([string]$path, [int[]]$sizes) {
    $pngs = @()
    foreach ($size in $sizes) {
        $bmp = New-Ts3ChatBitmap $size
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        $pngs += ,$ms.ToArray()
        $ms.Dispose()
    }

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$pngs.Count)
    $offset = 6 + (16 * $pngs.Count)
    for ($i = 0; $i -lt $pngs.Count; $i++) {
        $dim = $sizes[$i]
        $bw.Write([byte]($(if ($dim -ge 256) { 0 } else { $dim })))
        $bw.Write([byte]($(if ($dim -ge 256) { 0 } else { $dim })))
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$pngs[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($png in $pngs) { $bw.Write($png) }
    $bw.Flush()
    $bw.Dispose()
    $fs.Dispose()
}

Write-PngIco $out @(16, 24, 32, 48, 64, 256)
Write-Host "Wrote $out"
