# Generates the app logos and widget picker images with GDI+.
# English-only text so the file has no encoding pitfalls.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $gp.AddArc($x, $y, $d, $d, 180, 90)
    $gp.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $gp.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $gp.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $gp.CloseFigure()
    return $gp
}

function New-WhiteFont([float]$px) {
    return New-Object System.Drawing.Font('Segoe UI', $px, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
}

function New-SubtleFont([float]$px) {
    return New-Object System.Drawing.Font('Segoe UI', $px, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
}

function New-Tile([string]$OutPath, [int]$Size, [scriptblock]$DrawContent) {
    $dir = Split-Path $OutPath -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    try {
        $radius = [Math]::Max(3.0, $Size * 0.17)
        $path = New-RoundedRect 0 0 ($Size - 1) ($Size - 1) $radius
        $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x23, 0x97, 0xC9))
        $g.FillPath($bg, $path)
        & $DrawContent $g $Size
    }
    finally { $g.Dispose() }
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Wrote $OutPath ($Size x $Size)"
}

$root = $PSScriptRoot

# --- single "C" glyphs ---
$singleC = {
    param($g, $size)
    $font = New-WhiteFont ([float]($size * 0.60))
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $g.DrawString('C', $font, [System.Drawing.Brushes]::White, $rect, $sf)
}
New-Tile "$root\Assets\Square44x44Logo.png" 44 $singleC
New-Tile "$root\Assets\StoreLogo.png" 50 $singleC
New-Tile "$root\ProviderAssets\Codex_Icon.png" 48 $singleC

# --- 150x150 app logo with "Codex" ---
New-Tile "$root\Assets\Square150x150Logo.png" 150 {
    param($g, $size)
    $font = New-WhiteFont ([float]($size * 0.28))
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $rect = New-Object System.Drawing.RectangleF(0, 0, $size, $size)
    $g.DrawString('Codex', $font, [System.Drawing.Brushes]::White, $rect, $sf)
}

# --- 288x288 mock screenshot for the widget picker ---
New-Tile "$root\ProviderAssets\Codex_Screenshot.png" 288 {
    param($g, $size)
    $pad = [float]($size * 0.12)

    $titleFont = New-WhiteFont ([float]($size * 0.10))
    $g.DrawString('Codex', $titleFont, [System.Drawing.Brushes]::White, $pad, $pad)

    $pctFont = New-WhiteFont ([float]($size * 0.30))
    $g.DrawString('68%', $pctFont, [System.Drawing.Brushes]::White, $pad, ($size * 0.18))

    $labelFont = New-SubtleFont ([float]($size * 0.075))
    $subtle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0xE6, 0xF5, 0xFF))
    $g.DrawString('Weekly remaining', $labelFont, $subtle, $pad, ($size * 0.50))
    $g.DrawString('Resets Sep 28, 08:00', $labelFont, $subtle, $pad, ($size * 0.58))
    $g.DrawString('5h: 82%   Week: 68%', $labelFont, $subtle, $pad, ($size * 0.72))
}

Write-Host 'All images generated.'
