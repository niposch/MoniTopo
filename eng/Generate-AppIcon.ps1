param([string]$OutputPath = (Join-Path $PSScriptRoot '../src/MoniTopo.App/Assets/MoniTopo.ico'))

# Deterministic multi-resolution icon from the geometry in Assets/tray-icon.svg.
# The dark screen fill keeps white outlines legible on light window/taskbar surfaces.
Add-Type -AssemblyName System.Drawing.Common
$frames = @()
foreach ($size in @(16, 20, 24, 32, 48, 64, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 2.5)
    $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
    $buffer = [System.IO.MemoryStream]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 32.0, $size / 32.0)
        foreach ($rect in @(@(2, 5, 18, 13), @(14, 14, 16, 12))) {
            $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
            try {
                $x, $y, $w, $h = $rect
                $path.AddArc($x, $y, 4, 4, 180, 90)
                $path.AddArc(($x + $w - 4), $y, 4, 4, 270, 90)
                $path.AddArc(($x + $w - 4), ($y + $h - 4), 4, 4, 0, 90)
                $path.AddArc($x, ($y + $h - 4), 4, 4, 90, 90)
                $path.CloseFigure()
                $graphics.FillPath($fill, $path)
                $graphics.DrawPath($pen, $path)
            } finally { $path.Dispose() }
            if ($x -eq 2) {
                $graphics.DrawLine($pen, 11, 18, 11, 21)
                $graphics.DrawLine($pen, 8, 21, 14, 21)
            }
        }
        $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += [pscustomobject]@{ Size = $size; Bytes = $buffer.ToArray() }
    } finally { $buffer.Dispose(); $fill.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$stream = [System.IO.File]::Create([System.IO.Path]::GetFullPath($OutputPath))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $stream.Dispose() }
