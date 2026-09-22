# Exports app icons and the picker preview from maintained design masters.
# The picker preview must use the Windows host's 300x304 canvas.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = $PSScriptRoot
$sourcePath = Join-Path $root 'DesignAssets\CodexUsageIcon.png'
$previewSourcePath = Join-Path $root 'DesignAssets\CodexUsagePreview.png'
$previewPath = Join-Path $root 'ProviderAssets\Codex_Screenshot.png'

if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw 'Missing icon master: restore DesignAssets\CodexUsageIcon.png from source control.'
}
if (-not (Test-Path -LiteralPath $previewSourcePath)) {
    throw 'Missing preview master: restore DesignAssets\CodexUsagePreview.png from source control.'
}

function Export-Asset([System.Drawing.Image]$Source, [string]$OutPath, [int]$Width, [int]$Height) {
    $directory = Split-Path $OutPath -Parent
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    try {
        $bitmap.SetResolution(96, 96)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        # Fit the entire artwork; never fill-and-crop a tall source image.
        $scale = [Math]::Min($Width / $Source.Width, $Height / $Source.Height)
        $drawWidth = [int][Math]::Round($Source.Width * $scale)
        $drawHeight = [int][Math]::Round($Source.Height * $scale)
        $left = [int][Math]::Floor(($Width - $drawWidth) / 2)
        $top = [int][Math]::Floor(($Height - $drawHeight) / 2)
        $destination = [System.Drawing.Rectangle]::new($left, $top, $drawWidth, $drawHeight)
        $graphics.DrawImage($Source, $destination, 0, 0, $Source.Width, $Source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        $bitmap.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $attributes.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
    Write-Host "Wrote $OutPath ($Width x $Height)"
}

$source = [System.Drawing.Image]::FromFile($sourcePath)
try {
    if ($source.Width -ne $source.Height) {
        throw 'The icon master must have a square canvas.'
    }
    Export-Asset $source (Join-Path $root 'Assets\Square44x44Logo.png') 44 44
    Export-Asset $source (Join-Path $root 'Assets\StoreLogo.png') 50 50
    Export-Asset $source (Join-Path $root 'ProviderAssets\Codex_Icon.png') 48 48
    Export-Asset $source (Join-Path $root 'Assets\Square150x150Logo.png') 150 150
}
finally {
    $source.Dispose()
}

$previewSource = [System.Drawing.Image]::FromFile($previewSourcePath)
try {
    Export-Asset $previewSource $previewPath 300 304
}
finally {
    $previewSource.Dispose()
}

Write-Host 'App icons exported; widget picker preview exported at 300 x 304.'
