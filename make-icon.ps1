# Generates icon.ico (indigo circle, white upload arrow) — rerun if you restyle it.
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 63, 81, 181))
    $g.FillEllipse($bg, 0, 0, $s - 1, $s - 1)
    $white = [System.Drawing.Brushes]::White
    # arrow head
    $g.FillPolygon($white, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.50), ($s * 0.16)),
        (New-Object System.Drawing.PointF ($s * 0.26), ($s * 0.46)),
        (New-Object System.Drawing.PointF ($s * 0.74), ($s * 0.46))))
    # arrow shaft
    $g.FillRectangle($white, [single]($s * 0.41), [single]($s * 0.44), [single]($s * 0.18), [single]($s * 0.22))
    # tray line
    $g.FillRectangle($white, [single]($s * 0.26), [single]($s * 0.74), [single]($s * 0.48), [single]($s * 0.09))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) { New-IconPng $s }

$fs = [System.IO.File]::Create("$PSScriptRoot\icon.ico")
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($s -band 0xFF))   # 256 -> 0 per ico spec
    $bw.Write([byte]($s -band 0xFF))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "icon.ico written ($($sizes -join ', ') px)"
