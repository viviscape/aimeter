# Regenerates assets\AIMeter.ico (16-256 px, PNG-compressed entries).
# Same gauge artwork that UI\AppIcon.cs draws at runtime for the tray.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'assets\AIMeter.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-GaugePng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $pad = $size * 0.06; $d = $size - $pad * 2
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 30, 36))
    $g.FillEllipse($bg, $pad, $pad, $d, $d)

    $w = $size * 0.14; $inset = $pad + $w
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, ($size - $inset * 2), ($size - $inset * 2)
    foreach ($arc in @(
            @{ C = [System.Drawing.Color]::FromArgb(90, 255, 255, 255); S = 135; L = 270 },
            @{ C = [System.Drawing.Color]::FromArgb(255, 217, 119, 87); S = 135; L = 150 },
            @{ C = [System.Drawing.Color]::FromArgb(255, 63, 185, 138); S = 300; L = 90 })) {
        $pen = New-Object System.Drawing.Pen $arc.C, $w
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, $rect, $arc.S, $arc.L)
        $pen.Dispose()
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-GaugePng $s) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))   # 256 is stored as 0
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Dispose()
Write-Host "Wrote $out"
