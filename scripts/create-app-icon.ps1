$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$destination = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/Airlift.Desktop/Assets'))
[xml]$svg = Get-Content -LiteralPath (Join-Path $destination 'airlift.svg') -Raw
$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.ScaleTransform(4, 4)
$shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
$shape.AddArc(1, 1, 32, 32, 180, 90)
$shape.AddArc(31, 1, 32, 32, 270, 90)
$shape.AddArc(31, 31, 32, 32, 0, 90)
$shape.AddArc(1, 31, 32, 32, 90, 90)
$shape.CloseFigure()
$fill = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($svg.svg.rect.fill))
$pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($svg.svg.rect.stroke), 1)
$graphics.FillPath($fill, $shape)
$graphics.DrawPath($pen, $shape)
foreach ($polygon in $svg.svg.polygon) {
    [System.Drawing.PointF[]]$points = $polygon.points.Split(' ') | ForEach-Object {
        $xy = $_.Split(','); [System.Drawing.PointF]::new([float]$xy[0], [float]$xy[1])
    }
    $wing = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($polygon.fill))
    try { $graphics.FillPolygon($wing, $points) } finally { $wing.Dispose() }
}
$bitmap.Save((Join-Path $destination 'airlift.png'), [System.Drawing.Imaging.ImageFormat]::Png)
# A PNG-backed ICO retains alpha transparency and the full-resolution Windows icon.
$png = [System.IO.File]::ReadAllBytes((Join-Path $destination 'airlift.png'))
$stream = [System.IO.File]::Create((Join-Path $destination 'airlift.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22); $writer.Write($png)
} finally { $writer.Dispose(); $pen.Dispose(); $fill.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
$public = Join-Path $PSScriptRoot '../public/brand'
New-Item -ItemType Directory -Force $public | Out-Null
Copy-Item -LiteralPath (Join-Path $destination 'airlift.svg') -Destination (Join-Path $public 'airlift.svg')
