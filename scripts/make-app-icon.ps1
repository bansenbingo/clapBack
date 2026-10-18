# 生成应用图标：src/ClapBack.Windows/Assets/ClapBack.ico
#
# 设计：深蓝圆角方块（沿用主窗口的强调蓝）+ 白色对话气泡 + 气泡里三根高低不同的声波柱，
# 一眼是「说话 → 出字」。多尺寸打包成 ICO（16/20/24/32/40/48/64/128/256），
# 每个尺寸都用 GDI+ 重新绘制，而不是把小图放大，这样 16px 下也清晰。
#
# 用法：pwsh -File scripts/make-app-icon.ps1
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\ClapBack.Windows\Assets\ClapBack.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = [Math]::Max(0.5, $R)
    $d = $radius * 2
    if ($d -ge $W) { $d = $W / 2 }
    if ($d -ge $H) { $d = $H / 2 }
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 背景：圆角方块 + 对角渐变
    $bg = New-RoundedPath -X 0.5 -Y 0.5 -W ($Size - 1) -H ($Size - 1) -R ($Size * 0.22)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($Size, $Size)),
        [System.Drawing.Color]::FromArgb(255, 0x3B, 0x8D, 0xE4),
        [System.Drawing.Color]::FromArgb(255, 0x13, 0x3F, 0x77))
    $g.FillPath($bgBrush, $bg)

    # 对话气泡
    $bx = $Size * 0.14
    $by = $Size * 0.17
    $bw = $Size * 0.72
    $bh = $Size * 0.44
    $bubble = New-RoundedPath -X $bx -Y $by -W $bw -H $bh -R ($Size * 0.13)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(250, 255, 255, 255))
    $g.FillPath($white, $bubble)

    # 气泡尾巴（左下角斜切）
    $tail = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tailPoints = @(
        (New-Object System.Drawing.PointF(($bx + $bw * 0.18), ($by + $bh - 1))),
        (New-Object System.Drawing.PointF(($bx + $bw * 0.46), ($by + $bh - 1))),
        (New-Object System.Drawing.PointF(($bx + $bw * 0.14), ($by + $bh + $Size * 0.13)))
    )
    $tail.AddPolygon([System.Drawing.PointF[]]$tailPoints)
    $g.FillPath($white, $tail)

    # 声波柱：三根高低不同的圆角柱，用背景蓝
    $barColor = [System.Drawing.Color]::FromArgb(255, 0x1D, 0x55, 0x96)
    $barBrush = New-Object System.Drawing.SolidBrush $barColor
    $barWidth = $Size * 0.085
    $gap = $Size * 0.055
    $heights = @(($bh * 0.34), ($bh * 0.62), ($bh * 0.44))
    $totalWidth = ($barWidth * 3) + ($gap * 2)
    $startX = ($bx + (($bw - $totalWidth) / 2))
    $centerY = $by + ($bh / 2)
    for ($i = 0; $i -lt 3; $i++) {
        $h = $heights[$i]
        $x = $startX + ($i * ($barWidth + $gap))
        $y = $centerY - ($h / 2)
        $bar = New-RoundedPath -X $x -Y $y -W $barWidth -H $h -R ($barWidth / 2)
        $g.FillPath($barBrush, $bar)
        $bar.Dispose()
    }

    $g.Dispose()
    $bg.Dispose(); $bgBrush.Dispose(); $bubble.Dispose(); $white.Dispose(); $tail.Dispose(); $barBrush.Dispose()
    return $bmp
}

# ---- 把每个尺寸的 PNG 打包成 ICO ----
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngBlobs = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap -Size $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngBlobs += , ($ms.ToArray())
    $ms.Dispose()
}

$outputFull = [System.IO.Path]::GetFullPath($OutputPath)
$dir = [System.IO.Path]::GetDirectoryName($outputFull)
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$stream = [System.IO.File]::Create($outputFull)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0)            # reserved
$writer.Write([uint16]1)            # type = icon
$writer.Write([uint16]$sizes.Count) # image count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $blob = $pngBlobs[$i]
    $dimension = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dimension)  # width (0 = 256)
    $writer.Write([byte]$dimension)  # height (0 = 256)
    $writer.Write([byte]0)           # color count
    $writer.Write([byte]0)           # reserved
    $writer.Write([uint16]1)         # color planes
    $writer.Write([uint16]32)        # bits per pixel
    $writer.Write([uint32]$blob.Length)
    $writer.Write([uint32]$offset)
    $offset += $blob.Length
}
foreach ($blob in $pngBlobs) { $writer.Write($blob) }
$writer.Flush(); $writer.Dispose(); $stream.Dispose()

$info = Get-Item $outputFull
Write-Output ("ICON_WRITTEN=" + $info.FullName)
Write-Output ("ICON_BYTES=" + $info.Length)
Write-Output ("ICON_SIZES=" + ($sizes -join ','))

# 回读校验：System.Drawing.Icon 能读出最大尺寸才算有效
$icon = New-Object System.Drawing.Icon($outputFull)
Write-Output ("ICON_READBACK=" + $icon.Width + "x" + $icon.Height)
$icon.Dispose()
