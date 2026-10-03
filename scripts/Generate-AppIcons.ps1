# Windows-only asset generation; the checked-in ICO files are used on every build host.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../assets/icons'))
$sizes = @(16, 24, 32, 48, 64, 128, 256)
function Number($node, [string] $name) {
    $value = $node.GetAttribute($name)
    if (!$value) { return [single]0 }
    return [single]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
}
foreach ($name in @('openskitime', 'live-timing')) {
    [xml]$svg = Get-Content -LiteralPath (Join-Path $root "$name.svg") -Raw
    $frames = @()
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 64.0, $size / 64.0)
        try {
            foreach ($shape in $svg.DocumentElement.ChildNodes) {
                if ($shape -isnot [Xml.XmlElement]) { continue }
                $path = [Drawing.Drawing2D.GraphicsPath]::new()
                try {
                    switch ($shape.LocalName) {
                        'rect' {
                            $x = Number $shape 'x'; $y = Number $shape 'y'
                            $w = Number $shape 'width'; $h = Number $shape 'height'; $r = Number $shape 'rx'
                            if ($r -gt 0) {
                                $d = 2 * $r
                                $path.AddArc($x, $y, $d, $d, 180, 90)
                                $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
                                $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
                                $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
                                $path.CloseFigure()
                            } else { $path.AddRectangle([Drawing.RectangleF]::new($x, $y, $w, $h)) }
                        }
                        'circle' {
                            $r = Number $shape 'r'
                            $path.AddEllipse((Number $shape 'cx') - $r, (Number $shape 'cy') - $r, 2 * $r, 2 * $r)
                        }
                        'line' { $path.AddLine((Number $shape 'x1'), (Number $shape 'y1'), (Number $shape 'x2'), (Number $shape 'y2')) }
                        { $_ -in 'polygon', 'polyline' } {
                            [Drawing.PointF[]]$points = foreach ($pair in ($shape.GetAttribute('points') -split '\s+')) {
                                $xy = $pair -split ','
                                [Drawing.PointF]::new([single]::Parse($xy[0], [Globalization.CultureInfo]::InvariantCulture), [single]::Parse($xy[1], [Globalization.CultureInfo]::InvariantCulture))
                            }
                            $path.AddLines($points)
                            if ($shape.LocalName -eq 'polygon') { $path.CloseFigure() }
                        }
                        default { throw "Unsupported icon SVG element: $($shape.LocalName)" }
                    }
                    $fill = $shape.GetAttribute('fill')
                    if ($fill -and $fill -ne 'none') {
                        $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($fill))
                        try { $graphics.FillPath($brush, $path) } finally { $brush.Dispose() }
                    }
                    $stroke = $shape.GetAttribute('stroke')
                    if ($stroke) {
                        $pen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml($stroke), (Number $shape 'stroke-width'))
                        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
                        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                        try { $graphics.DrawPath($pen, $path) } finally { $pen.Dispose() }
                    }
                } finally { $path.Dispose() }
            }
            $stream = [IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                $frames += ,$stream.ToArray()
                if ($size -eq 256) { [IO.File]::WriteAllBytes((Join-Path $root "$name.png"), $stream.ToArray()) }
            } finally { $stream.Dispose() }
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $root "$name.ico"))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    } finally { $writer.Dispose() }
    Write-Host "Generated $name.ico ($($sizes -join ', ') px)."
}
