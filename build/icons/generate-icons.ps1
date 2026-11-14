<#
.SYNOPSIS
  Renders the DotDeck icon SVG sources to PNG and ICO files (no extra tools: WPF from Windows PowerShell).

.DESCRIPTION
  Sources (edit these): src/DotDeck.App/Assets/Icons/Source/*.svg
    app.svg              master app icon, used for 40, 48, 64, 128 and 256 px
    app-16/20/24/32.svg  hand-tuned small app icons (pixel-grid edges, thicker strokes, bigger dot)
    tray-16/20/24/32/48.svg  tray symbol (no tile), drawn dark for a light taskbar; rendered white for a dark one

  Output (commit these): src/DotDeck.App/Assets/Icons/
    DotDeck.ico          app icon: 16 20 24 32 40 48 64 128 256 (exe ApplicationIcon, Window.Icon, shortcuts)
    tray-on-light.ico    dark symbol for a light taskbar: 16 20 24 32 48
    tray-on-dark.ico     white symbol for a dark taskbar: 16 20 24 32 48
    png/*.png            every frame above as PNG

  Only the SVG subset the sources use is supported: <path> (d, fill, fill-opacity, fill-rule, clip-path), <rect>,
  <linearGradient> (bounding-box units) and <clipPath>. Frames under 256 px are 32-bit BMP, 256 px is PNG.

.EXAMPLE
  powershell -NoProfile -STA -File build/icons/generate-icons.ps1
  powershell -NoProfile -STA -File build/icons/generate-icons.ps1 -PreviewPath $env:TEMP\dotdeck-icons.png
#>
[CmdletBinding()]
param(
    # Optional contact sheet (all frames on light/dark backgrounds, also enlarged) for checking small sizes.
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$iconDir = Join-Path $root 'src/DotDeck.App/Assets/Icons'
$sourceDir = Join-Path $iconDir 'Source'
$pngDir = Join-Path $iconDir 'png'
New-Item -ItemType Directory -Force -Path $pngDir | Out-Null

$appSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$traySizes = 16, 20, 24, 32, 48
$trayInk = '#1C1528'      # tray SVG color (light taskbar)
$trayInkDark = '#FFFFFF'  # same symbol for a dark taskbar

function Get-SvgColor([string]$value, [hashtable]$recolor) {
    if ($recolor.ContainsKey($value.ToUpperInvariant())) { $value = $recolor[$value.ToUpperInvariant()] }
    [System.Windows.Media.Color][System.Windows.Media.ColorConverter]::ConvertFromString($value)
}

function Get-Fraction([string]$value, [double]$default) {
    if (-not $value) { return $default }
    if ($value.EndsWith('%')) { return [double]::Parse($value.TrimEnd('%'), [Globalization.CultureInfo]::InvariantCulture) / 100 }
    [double]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Geometry([System.Xml.XmlElement]$node, [string]$ruleAttribute) {
    $inv = [Globalization.CultureInfo]::InvariantCulture
    if ($node.LocalName -eq 'rect') {
        $num = { param($n) if ($node.GetAttribute($n)) { [double]::Parse($node.GetAttribute($n), $inv) } else { 0.0 } }
        $rx = & $num 'rx'; $ry = if ($node.GetAttribute('ry')) { & $num 'ry' } else { $rx }
        return [System.Windows.Media.RectangleGeometry]::new([System.Windows.Rect]::new((& $num 'x'), (& $num 'y'), (& $num 'width'), (& $num 'height')), $rx, $ry)
    }
    # WPF path markup reads SVG path data; it defaults to even-odd, SVG to nonzero.
    $rule = if ($node.GetAttribute($ruleAttribute) -eq 'evenodd') { 'F0 ' } else { 'F1 ' }
    [System.Windows.Media.Geometry]::Parse($rule + $node.GetAttribute('d'))
}

# Renders one SVG file to a straight-alpha BGRA bitmap of size x size.
function Render-Svg([string]$file, [int]$size, [hashtable]$recolor = @{}) {
    [xml]$doc = Get-Content -Raw -Encoding utf8 $file
    $svg = $doc.DocumentElement
    $viewBox = $svg.GetAttribute('viewBox').Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [double]$_ }
    $gradients = @{}; $clips = @{}
    foreach ($node in $svg.SelectNodes("//*[local-name()='linearGradient']")) {
        $brush = [System.Windows.Media.LinearGradientBrush]::new()
        $brush.StartPoint = [System.Windows.Point]::new((Get-Fraction $node.GetAttribute('x1') 0), (Get-Fraction $node.GetAttribute('y1') 0))
        $brush.EndPoint = [System.Windows.Point]::new((Get-Fraction $node.GetAttribute('x2') 1), (Get-Fraction $node.GetAttribute('y2') 0))
        foreach ($stop in $node.SelectNodes("*[local-name()='stop']")) {
            $color = Get-SvgColor $stop.GetAttribute('stop-color') $recolor
            if ($stop.GetAttribute('stop-opacity')) { $color.A = [byte][math]::Round(255 * (Get-Fraction $stop.GetAttribute('stop-opacity') 1)) }
            $brush.GradientStops.Add([System.Windows.Media.GradientStop]::new($color, (Get-Fraction $stop.GetAttribute('offset') 0)))
        }
        $gradients[$node.GetAttribute('id')] = $brush
    }
    foreach ($node in $svg.SelectNodes("//*[local-name()='clipPath']")) {
        $group = [System.Windows.Media.GeometryGroup]::new()
        foreach ($child in $node.SelectNodes("*[local-name()='path' or local-name()='rect']")) { $group.Children.Add((Get-Geometry $child 'clip-rule')) }
        $clips[$node.GetAttribute('id')] = $group
    }

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $dc.PushTransform([System.Windows.Media.MatrixTransform]::new($size / $viewBox[2], 0, 0, $size / $viewBox[3], -$viewBox[0] * $size / $viewBox[2], -$viewBox[1] * $size / $viewBox[3]))
    foreach ($node in $svg.SelectNodes("*[local-name()='path' or local-name()='rect']")) {
        $geometry = Get-Geometry $node 'fill-rule'
        $clipRef = $node.GetAttribute('clip-path')
        if ($clipRef -match '^url\(#(.+)\)$') {
            $geometry = [System.Windows.Media.Geometry]::Combine($geometry, $clips[$Matches[1]], [System.Windows.Media.GeometryCombineMode]::Intersect, $null, 1e-4, [System.Windows.Media.ToleranceType]::Absolute)
        }
        $fill = $node.GetAttribute('fill')
        $brush = if ($fill -match '^url\(#(.+)\)$') { $gradients[$Matches[1]].Clone() } else { [System.Windows.Media.SolidColorBrush]::new((Get-SvgColor $fill $recolor)) }
        if ($node.GetAttribute('fill-opacity')) { $brush.Opacity = Get-Fraction $node.GetAttribute('fill-opacity') 1 }
        $dc.DrawGeometry($brush, $null, $geometry)
    }
    $dc.Close()

    $target = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($visual)
    [System.Windows.Media.Imaging.FormatConvertedBitmap]::new($target, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
}

function Get-Pixels([System.Windows.Media.Imaging.BitmapSource]$bitmap) {
    $pixels = [byte[]]::new($bitmap.PixelWidth * $bitmap.PixelHeight * 4)
    $bitmap.CopyPixels($pixels, $bitmap.PixelWidth * 4, 0)
    , $pixels
}

function Get-PngBytes([System.Windows.Media.Imaging.BitmapSource]$bitmap) {
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    , $stream.ToArray()
}

# 32-bit DIB as stored in an ICO: BITMAPINFOHEADER (double height), bottom-up BGRA rows, then the 1-bit AND mask.
function Get-DibBytes([System.Windows.Media.Imaging.BitmapSource]$bitmap) {
    $size = $bitmap.PixelWidth
    $pixels = Get-Pixels $bitmap
    $maskStride = [int]([math]::Ceiling($size / 32) * 4)
    $stream = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($stream)
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($size * $size * 4)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $size * 4, $size * 4) }
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = [byte[]]::new($maskStride)
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[($y * $size + $x) * 4 + 3] -eq 0) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) }
        }
        $w.Write($row)
    }
    $w.Flush()
    , $stream.ToArray()
}

function Write-Ico([string]$path, [object[]]$frames) {
    $images = foreach ($frame in $frames) {
        , $(if ($frame.PixelWidth -ge 256) { Get-PngBytes $frame } else { Get-DibBytes $frame })
    }
    $stream = [IO.MemoryStream]::new()
    $w = [IO.BinaryWriter]::new($stream)
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $s = $frames[$i].PixelWidth
        $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($image in $images) { $w.Write($image) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $stream.ToArray())
}

# Reads an ICO back: frame sizes and whether the top-left pixel (outside the tile / symbol) is fully transparent.
function Test-Ico([string]$path, [int[]]$expected) {
    $decoder = [System.Windows.Media.Imaging.IconBitmapDecoder]::new([Uri]::new($path), [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $sizes = @($decoder.Frames | ForEach-Object { $_.PixelWidth } | Sort-Object)
    if (($sizes -join ',') -ne (($expected | Sort-Object) -join ',')) { throw "$path frames $($sizes -join ',') != $($expected -join ',')" }
    foreach ($frame in $decoder.Frames) {
        $pixels = Get-Pixels ([System.Windows.Media.Imaging.FormatConvertedBitmap]::new($frame, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0))
        $transparent = 0
        for ($i = 3; $i -lt $pixels.Length; $i += 4) { if ($pixels[$i] -eq 0) { $transparent++ } }
        if ($pixels[3] -ne 0 -or $transparent -eq 0) { throw "$path $($frame.PixelWidth)px: background is not transparent" }
    }
    Write-Host ("{0,-18} {1}  (transparent corners ok)" -f (Split-Path -Leaf $path), ($sizes -join ' '))
}

$app = foreach ($s in $appSizes) {
    $file = Join-Path $sourceDir $(if ($s -le 32) { "app-$s.svg" } else { 'app.svg' })
    , (Render-Svg $file $s)
}
$trayLight = foreach ($s in $traySizes) { , (Render-Svg (Join-Path $sourceDir "tray-$s.svg") $s) }
$trayDark = foreach ($s in $traySizes) { , (Render-Svg (Join-Path $sourceDir "tray-$s.svg") $s @{ $trayInk = $trayInkDark }) }

Write-Ico (Join-Path $iconDir 'DotDeck.ico') $app
Write-Ico (Join-Path $iconDir 'tray-on-light.ico') $trayLight
Write-Ico (Join-Path $iconDir 'tray-on-dark.ico') $trayDark
for ($i = 0; $i -lt $appSizes.Count; $i++) { [IO.File]::WriteAllBytes((Join-Path $pngDir "app-$($appSizes[$i]).png"), (Get-PngBytes $app[$i])) }
for ($i = 0; $i -lt $traySizes.Count; $i++) {
    [IO.File]::WriteAllBytes((Join-Path $pngDir "tray-on-light-$($traySizes[$i]).png"), (Get-PngBytes $trayLight[$i]))
    [IO.File]::WriteAllBytes((Join-Path $pngDir "tray-on-dark-$($traySizes[$i]).png"), (Get-PngBytes $trayDark[$i]))
}

Test-Ico (Join-Path $iconDir 'DotDeck.ico') $appSizes
Test-Ico (Join-Path $iconDir 'tray-on-light.ico') $traySizes
Test-Ico (Join-Path $iconDir 'tray-on-dark.ico') $traySizes

if ($PreviewPath) {
    # Rows: app / tray-on-light / tray-on-dark, each on a light and a dark taskbar color; actual size, then 4x.
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $backgrounds = '#F3F3F3', '#202020'
    $rows = @(@{ Frames = $app; Bg = $backgrounds }, @{ Frames = $trayLight; Bg = @('#F3F3F3') }, @{ Frames = $trayDark; Bg = @('#202020') })
    $y = 0
    foreach ($row in $rows) {
        foreach ($bg in $row.Bg) {
            $dc.DrawRectangle([System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString($bg)), $null, [System.Windows.Rect]::new(0, $y, 2000, 300))
            $x = 10
            foreach ($f in $row.Frames) { $dc.DrawImage($f, [System.Windows.Rect]::new($x, $y + 10, $f.PixelWidth, $f.PixelHeight)); $x += $f.PixelWidth + 10 }
            $x += 30
            foreach ($f in $row.Frames | Where-Object { $_.PixelWidth -le 48 }) {
                $dc.DrawImage($f, [System.Windows.Rect]::new($x, $y + 10, $f.PixelWidth * 4, $f.PixelHeight * 4)); $x += $f.PixelWidth * 4 + 10
            }
            $y += 280
        }
    }
    $dc.Close()
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::NearestNeighbor)
    $sheet = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(2000, $y, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $sheet.Render($visual)
    [IO.File]::WriteAllBytes($PreviewPath, (Get-PngBytes $sheet))
    Write-Host "Preview: $PreviewPath"
}
