#!/usr/bin/env pwsh
# v0.36 K1: fetch 20 starter icons from game-icons.net PNG endpoint and resize
# to 32x32 + 64x64 with transparent background. Uses only PowerShell built-ins
# (Invoke-WebRequest + System.Drawing) so no external rasterizer is required.
#
# Idempotent — re-runs produce byte-identical outputs.
# Source manifest: scratchpad/starter-icons-raw/_final-pairings.tsv
# Output dir: assets/starter-icons/

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$tsvPath  = Join-Path $repoRoot 'scratchpad/starter-icons-raw/_final-pairings.tsv'
$outDir   = Join-Path $repoRoot 'assets/starter-icons'
$sizes    = @(32, 64)

if (-not (Test-Path $tsvPath)) { throw "Manifest not found: $tsvPath" }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$rows = @()
Get-Content $tsvPath | Select-Object -Skip 1 | ForEach-Object {
    if (-not $_.Trim()) { return }
    $parts = $_.Split("`t")
    if ($parts.Length -lt 5) { throw "Malformed TSV row: $_" }
    $rows += [pscustomobject]@{
        Key       = $parts[0]
        Author    = $parts[1]
        Slug      = $parts[2]
        License   = $parts[3]
        SourceUrl = $parts[4]
    }
}

Write-Host "Rasterizing $($rows.Count) icons at sizes: $($sizes -join ', ')"

foreach ($row in $rows) {
    # PNG endpoint: black icon on transparent background at 512x512 (default).
    $pngUrl = "https://game-icons.net/icons/000000/transparent/1x1/$($row.Author)/$($row.Slug).png"
    $tempSrc = Join-Path $env:TEMP "starter-icon-$($row.Key)-src.png"
    try {
        Invoke-WebRequest -Uri $pngUrl -OutFile $tempSrc -UseBasicParsing -ErrorAction Stop | Out-Null
    } catch {
        throw "Failed to download $($row.Key) from $pngUrl : $_"
    }

    $src = [System.Drawing.Image]::FromFile($tempSrc)
    try {
        foreach ($size in $sizes) {
            $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.Clear([System.Drawing.Color]::Transparent)
                $g.CompositingMode      = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $g.CompositingQuality   = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $g.InterpolationMode    = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode        = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode      = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
            } finally {
                $g.Dispose()
            }
            $outPath = Join-Path $outDir "$($row.Key)@$size.png"
            $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
            $bmp.Dispose()
        }
    } finally {
        $src.Dispose()
        Remove-Item $tempSrc -ErrorAction SilentlyContinue
    }

    Write-Host ("  ok {0} <- {1}/{2}" -f $row.Key, $row.Author, $row.Slug)
}

Write-Host ""
Write-Host "Rasterized $($rows.Count) icons x $($sizes.Count) sizes = $($rows.Count * $sizes.Count) PNGs -> $outDir"
