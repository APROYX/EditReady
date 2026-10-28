# Draws EditReady.ico (violet rounded square, white play mark with speed lines) at 16-256 px.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [single]($s * 0.24)
    $w = [single]($s - 1)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90)
    $path.AddArc(0, $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x9B, 0x82, 0xFF)), ([System.Drawing.Color]::FromArgb(255, 0x5B, 0x3D, 0xF5)), 45.0
    $g.FillPath($grad, $path)

    $u = $s / 40.0
    $white = [System.Drawing.Brushes]::White
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (16 * $u), (12 * $u)),
        (New-Object System.Drawing.PointF (29 * $u), (20 * $u)),
        (New-Object System.Drawing.PointF (16 * $u), (28 * $u)))
    $g.FillPolygon($white, $tri)
    if ($s -ge 24) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 255, 255, 255)), ([single](2.2 * $u))
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawLine($pen, (7.5 * $u), (16 * $u), (12 * $u), (16 * $u))
        $g.DrawLine($pen, (5.5 * $u), (20 * $u), (12 * $u), (20 * $u))
        $g.DrawLine($pen, (7.5 * $u), (24 * $u), (12 * $u), (24 * $u))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 256, 64, 48, 32, 24, 16
$pngs = foreach ($s in $sizes) { , (New-IconPng $s) }
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $here 'EditReady.ico'), $out.ToArray())
