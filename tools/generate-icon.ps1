<#
.SYNOPSIS
    Regenerates src/CaptionBridge/Assets/app.ico.

.DESCRIPTION
    Draws an I-beam text cursor on a dark rounded tile at every size Windows asks for,
    then packs the frames into a PNG-compressed .ico. Run this only when the icon
    artwork changes; the generated file is committed.
#>
[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\src\CaptionBridge\Assets\app.ico')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$tile = [System.Drawing.Color]::FromArgb(255, 30, 36, 48)
$edge = [System.Drawing.Color]::FromArgb(255, 62, 74, 96)
$glyph = [System.Drawing.Color]::FromArgb(255, 244, 247, 251)

function New-RoundedRectanglePath {
    param([float] $X, [float] $Y, [float] $Width, [float] $Height, [float] $Radius)

    $diameter = $Radius * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int] $Size)

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $inset = [Math]::Max(0.5, $Size * 0.03)
        $side = $Size - ($inset * 2)
        $path = New-RoundedRectanglePath -X $inset -Y $inset -Width $side -Height $side -Radius ($Size * 0.22)
        try {
            $fill = New-Object System.Drawing.SolidBrush($tile)
            $pen = New-Object System.Drawing.Pen($edge, [Math]::Max(1.0, $Size * 0.03))
            try {
                $graphics.FillPath($fill, $path)
                $graphics.DrawPath($pen, $path)
            }
            finally {
                $fill.Dispose()
                $pen.Dispose()
            }
        }
        finally {
            $path.Dispose()
        }

        # I-beam: a vertical stem capped by serifs, the standard text-cursor shape.
        $brush = New-Object System.Drawing.SolidBrush($glyph)
        try {
            $centerX = $Size / 2.0
            $stemWidth = [Math]::Max(1.5, $Size * 0.085)
            $serifWidth = [Math]::Max(5.0, $Size * 0.34)
            $serifHeight = [Math]::Max(1.5, $Size * 0.085)
            $top = $Size * 0.24
            $bottom = $Size * 0.76

            $graphics.FillRectangle($brush, $centerX - ($stemWidth / 2), $top, $stemWidth, $bottom - $top)
            $graphics.FillRectangle($brush, $centerX - ($serifWidth / 2), $top, $serifWidth, $serifHeight)
            $graphics.FillRectangle($brush, $centerX - ($serifWidth / 2), $bottom - $serifHeight, $serifWidth, $serifHeight)
        }
        finally {
            $brush.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
    }

    # The leading comma stops PowerShell unrolling the return value into the pipeline.
    return , $bitmap
}

function ConvertTo-IconPng {
    param([System.Drawing.Bitmap] $Bitmap)

    $stream = New-Object System.IO.MemoryStream
    $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $stream.ToArray()
}

<#
Builds a classic BITMAPINFOHEADER frame: a bottom-up 32bpp BGRA image followed by the
1bpp AND mask the format still requires even when the alpha channel carries the shape.
Windows reads PNG frames too, but only at 128px and above is the size saving worth
giving up compatibility with older icon readers.
#>
function ConvertTo-IconDib {
    param([System.Drawing.Bitmap] $Bitmap)

    $size = $Bitmap.Width
    $rectangle = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $locked = $Bitmap.LockBits(
        $rectangle,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $pixels = New-Object byte[] ($locked.Stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $stride = $locked.Stride
    $Bitmap.UnlockBits($locked)

    $maskStride = [int][Math]::Floor(($size + 31) / 32) * 4
    $xorSize = $size * $size * 4
    $maskSize = $maskStride * $size

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint32]40)              # biSize
        $writer.Write([int32]$size)            # biWidth
        $writer.Write([int32]($size * 2))      # biHeight: image plus mask
        $writer.Write([uint16]1)               # biPlanes
        $writer.Write([uint16]32)              # biBitCount
        $writer.Write([uint32]0)               # biCompression: BI_RGB
        $writer.Write([uint32]($xorSize + $maskSize))
        $writer.Write([int32]0)                # biXPelsPerMeter
        $writer.Write([int32]0)                # biYPelsPerMeter
        $writer.Write([uint32]0)               # biClrUsed
        $writer.Write([uint32]0)               # biClrImportant

        for ($row = $size - 1; $row -ge 0; $row--) {
            $writer.Write($pixels, $row * $stride, $size * 4)
        }

        $writer.Write((New-Object byte[] $maskSize))
    }
    finally {
        $writer.Dispose()
    }

    return , $stream.ToArray()
}

$frames = foreach ($size in $sizes) {
    $bitmap = New-IconBitmap -Size $size
    try {
        $data = if ($size -ge 128) { ConvertTo-IconPng -Bitmap $bitmap } else { ConvertTo-IconDib -Bitmap $bitmap }
        [pscustomobject]@{ Size = $size; Data = [byte[]] $data }
    }
    finally {
        $bitmap.Dispose()
    }
}

$resolved = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($resolved)) | Out-Null

$file = [System.IO.File]::Create($resolved)
$writer = New-Object System.IO.BinaryWriter($file)
try {
    # ICONDIR
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)

    $offset = 6 + (16 * $frames.Count)
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -ge 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension)   # width
        $writer.Write([byte]$dimension)   # height
        $writer.Write([byte]0)            # palette size
        $writer.Write([byte]0)            # reserved
        $writer.Write([uint16]1)          # colour planes
        $writer.Write([uint16]32)         # bits per pixel
        $writer.Write([uint32]$frame.Data.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Data.Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame.Data)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Wrote $resolved ($((Get-Item $resolved).Length) bytes, $($frames.Count) frames)"
