# Regenerate the synthetic OCR regression fixture on Windows. No race data is used.
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap(1400, 1250)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$font = New-Object System.Drawing.Font('Consolas', 26, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$ink = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(65, 65, 65))
try {
    $graphics.Clear([System.Drawing.Color]::FromArgb(135, 140, 145))
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        $shade = 245 - [int](65 * $y / $bitmap.Height)
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($shade, $shade, $shade))
        try { $graphics.DrawLine($pen, 345, $y, 1120, $y) } finally { $pen.Dispose() }
    }
    for ($i = 0; $i -lt 16; $i++) {
        $y = 120 + $i * 60
        $channel = $i % 2
        $minute = 10 + $i
        $text = '{0} C{1}M 12:{2}:23.4567' -f (123400 + $i), $channel, $minute
        $graphics.DrawString('weave', $font, $ink, 285, $y)
        $graphics.DrawString($text, $font, $ink, 375, $y)
    }
    $bitmap.Save((Join-Path $PSScriptRoot 'timing-receipt-noisy.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $clipped = $bitmap.Clone([System.Drawing.Rectangle]::new(0, 0, 720, 1250), $bitmap.PixelFormat)
    try {
        $clipped.Save((Join-Path $PSScriptRoot 'timing-receipt-clipped.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $clipped.Dispose() }
} finally {
    $ink.Dispose()
    $font.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
