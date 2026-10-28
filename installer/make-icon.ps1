# Генерирует Assets/app.ico: тёмный «нотч» с белым треугольником play.
# Запуск: powershell -NoProfile -ExecutionPolicy Bypass -File installer\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$icoPath = Join-Path $assets 'app.ico'

function New-MasterBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size
    $pad = [double]($s * 0.04)
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))

    # Скруглённый квадрат — «корпус» виджета.
    $radius = [double]($s * 0.22)
    $d = $radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 34, 34, 42),
        [System.Drawing.Color]::FromArgb(255, 14, 14, 18),
        90.0)
    $g.FillPath($bg, $path)

    $penWidth = [float][Math]::Max(1.0, $s * 0.012)
    $border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(46, 255, 255, 255), $penWidth)
    $g.DrawPath($border, $path)

    # Треугольник play по центру.
    $cx = [double]($s * 0.535)
    $cy = [double]($s * 0.5)
    $h = [double]($s * 0.34)
    $w = [double]($s * 0.30)

    $triangle = New-Object System.Drawing.Drawing2D.GraphicsPath
    $triangle.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF([float]($cx - $w / 2), [float]($cy - $h / 2))),
        (New-Object System.Drawing.PointF([float]($cx - $w / 2), [float]($cy + $h / 2))),
        (New-Object System.Drawing.PointF([float]($cx + $w / 2), [float]$cy))
    ))

    $fg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 245, 245, 250))
    $g.FillPath($fg, $triangle)

    $fg.Dispose()
    $triangle.Dispose()
    $border.Dispose()
    $bg.Dispose()
    $path.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width
    $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)

    $maskStride = [int]([Math]::Ceiling($w / 32.0) * 4)
    $header = New-Object System.Collections.Generic.List[byte]
    $header.AddRange([byte[]]@(40, 0, 0, 0))
    $header.AddRange([BitConverter]::GetBytes([int]$w))
    $header.AddRange([BitConverter]::GetBytes([int]($h * 2)))
    $header.AddRange([BitConverter]::GetBytes([int16]1))
    $header.AddRange([BitConverter]::GetBytes([int16]32))
    $header.AddRange([BitConverter]::GetBytes([int]0))
    $header.AddRange([BitConverter]::GetBytes([int]($w * $h * 4)))
    $header.AddRange([BitConverter]::GetBytes([int]0))
    $header.AddRange([BitConverter]::GetBytes([int]0))
    $header.AddRange([BitConverter]::GetBytes([int]0))
    $header.AddRange([BitConverter]::GetBytes([int]0))

    $total = $header.Count + $w * 4 * $h + $maskStride * $h
    $buffer = New-Object byte[] $total
    $header.CopyTo($buffer, 0)

    $pos = $header.Count
    # Пиксели снизу вверх (BGRA уже в нужном порядке для Format32bppArgb).
    for ($y = $h - 1; $y -ge 0; $y--) {
        [Array]::Copy($raw, $y * $stride, $buffer, $pos, $w * 4)
        $pos += $w * 4
    }

    # AND-маска: всё непрозрачное (нули), строки выровнены по 4 байта.
    $pos += $maskStride * $h

    return ,$buffer
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    [byte[]]$bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$master = New-MasterBitmap 1024

$entries = New-Object System.Collections.Generic.List[object]
foreach ($size in $sizes) {
    $scaled = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($scaled)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($master, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g.Dispose()

    # Крупные размеры — PNG, мелкие — DIB (максимальная совместимость с оболочкой Windows).
    if ($size -ge 128) {
        [byte[]]$bytes = Get-PngBytes $scaled
    }
    else {
        [byte[]]$bytes = Get-DibBytes $scaled
    }

    $entries.Add([pscustomobject]@{ Size = $size; Bytes = $bytes })
    $scaled.Dispose()
}
$master.Dispose()

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$entries.Count)

$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
    $size = [int]$entry.Size
    $bw.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $bw.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$entry.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}

foreach ($entry in $entries) {
    $bw.Write([byte[]]$entry.Bytes)
}

$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

# Проверка, что оболочка Windows сможет прочитать файл.
$icon = [System.Drawing.Icon]::new([string]$icoPath)
"icon: $icoPath ($((Get-Item $icoPath).Length) bytes), size=$($icon.Width)x$($icon.Height)"
$icon.Dispose()