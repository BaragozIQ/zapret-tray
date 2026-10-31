# Рисует иконку программы (зелёный круг с Z) и сохраняет .ico с PNG внутри
param([string]$Out)
Add-Type -AssemblyName System.Drawing
$size = 64
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([System.Drawing.Color]::Transparent)
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46, 170, 80))
$g.FillEllipse($brush, 3, 3, 58, 58)
$font = New-Object System.Drawing.Font 'Segoe UI', 32, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
$g.DrawString('Z', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 2, $size, $size), $sf)
$g.Dispose()
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]1)
$w.Write([byte]$size); $w.Write([byte]$size); $w.Write([byte]0); $w.Write([byte]0)
$w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$png.Length); $w.Write([uint32]22)
$w.Write($png)
$w.Close()
