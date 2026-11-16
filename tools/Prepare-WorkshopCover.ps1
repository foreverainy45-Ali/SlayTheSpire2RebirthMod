param([Parameter(Mandatory)][string]$GeneratedImage)
$ErrorActionPreference='Stop'
$workspace=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assets=Join-Path $workspace 'assets\workshop'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$original=Join-Path $assets 'rebirth-original.png'
$cover=Join-Path $assets 'rebirth-cover.png'
if((Test-Path -LiteralPath $original) -or (Test-Path -LiteralPath $cover)){throw 'Cover v1 already exists; use a new version instead of overwriting.'}
Copy-Item -LiteralPath $GeneratedImage -Destination $original
# Delivery-only thumbnail sizing; retain the untouched generated master above.
Add-Type -AssemblyName System.Drawing
$source=[System.Drawing.Image]::FromFile($original)
try {
    $side=512
    $bitmap=[System.Drawing.Bitmap]::new($side,$side)
    try {
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.CompositingQuality=[System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.DrawImage($source,0,0,$side,$side)
        } finally {$graphics.Dispose()}
        $bitmap.Save($cover,[System.Drawing.Imaging.ImageFormat]::Png)
    } finally {$bitmap.Dispose()}
    Write-Output "Master dimensions: $($source.Width)x$($source.Height)"
} finally {$source.Dispose()}
$file=Get-Item -LiteralPath $cover
if($file.Length -ge 1000000){throw 'Cover exceeds conservative 1 MB limit'}
Write-Output "Workshop cover: $cover (512x512, $($file.Length) bytes)"
