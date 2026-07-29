param(
    [string]$Output = (Join-Path $PSScriptRoot "..\src\XiurenDownloader\Assets\app.ico")
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$background = [System.Drawing.Color]::FromArgb(20, 125, 120)
$highlight = [System.Drawing.Color]::FromArgb(205, 83, 58)
$white = [System.Drawing.Color]::White
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
$radius = 42
$diameter = $radius * 2
$bounds = [System.Drawing.Rectangle]::new(12, 12, 232, 232)
$path.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
$path.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
$path.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
$path.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
$path.CloseFigure()
$graphics.FillPath([System.Drawing.SolidBrush]::new($background), $path)

$cameraPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
$cameraPath.AddArc(48, 76, 36, 36, 180, 90)
$cameraPath.AddArc(172, 76, 36, 36, 270, 90)
$cameraPath.AddArc(172, 156, 36, 36, 0, 90)
$cameraPath.AddArc(48, 156, 36, 36, 90, 90)
$cameraPath.CloseFigure()
$graphics.DrawPath([System.Drawing.Pen]::new($white, 12), $cameraPath)
$graphics.FillEllipse([System.Drawing.SolidBrush]::new($white), 92, 91, 72, 72)
$graphics.FillEllipse([System.Drawing.SolidBrush]::new($background), 109, 108, 38, 38)
$graphics.FillEllipse([System.Drawing.SolidBrush]::new($highlight), 169, 91, 22, 22)

$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $pngStream.ToArray()

$directory = Split-Path -Parent $Output
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$stream = [System.IO.File]::Open($Output, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$png.Length)
$writer.Write([uint32]22)
$writer.Write($png)
$writer.Dispose()

$cameraPath.Dispose()
$path.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$pngStream.Dispose()
