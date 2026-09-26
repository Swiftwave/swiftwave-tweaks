# Generates Assets\GameReady.ico: a multi-size icon with the
# "swiftwave" brand mark — dark rounded square, two soft wave strokes (near-white
# over muted gray). Entries are stored as PNG blobs (supported on Vista+).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$iconPath = Join-Path $PSScriptRoot 'Assets\GameReady.ico'
New-Item (Split-Path $iconPath) -ItemType Directory -Force | Out-Null

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

  $scale = $s / 256.0
  $inset = [Math]::Max(1, [int]([Math]::Round(16 * $scale)))   # 6.25% inset
  $w = $s - 2 * $inset
  $radius = 0.24 * $w

  # Dark rounded-square backdrop (brand background)
  $bgPath = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $radius
  $bgPath.AddArc($inset, $inset, $d, $d, 180, 90)
  $bgPath.AddArc($inset + $w - $d, $inset, $d, $d, 270, 90)
  $bgPath.AddArc($inset + $w - $d, $inset + $w - $d, $d, $d, 0, 90)
  $bgPath.AddArc($inset, $inset + $w - $d, $d, $d, 90, 90)
  $bgPath.CloseFigure()
  $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x0B, 0x10, 0x1A))
  $g.FillPath($bg, $bgPath)

  # Wave strokes: main near-white, secondary muted gray
  $x0 = $inset + 0.18 * $w; $x1 = $inset + 0.82 * $w
  $mid = $inset + $w / 2
  $hump = 0.16 * $w
  $yMain = $inset + 0.50 * $w
  $yAlt = $inset + 0.68 * $w

  $penMain = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0xF0, 0xF6, 0xFB), ([Math]::Max(2, [int](0.085 * $w))))
  $penMain.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $penMain.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $penAlt = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0x7E, 0x93, 0xA9), ([Math]::Max(1, [int](0.06 * $w))))
  $penAlt.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $penAlt.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

  foreach ($pair in @(@($penMain, $yMain), @($penAlt, $yAlt))) {
    $pen = $pair[0]; $y = $pair[1]
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddBezier([float]$x0, [float]$y, [float]($x0 + 0.24 * ($mid - $x0) * 2), [float]($y - $hump), [float]($mid - 0.24 * ($mid - $x0) * 2), [float]($y - $hump), [float]$mid, [float]$y)
    $p.AddBezier([float]$mid, [float]$y, [float]($mid + 0.24 * ($x1 - $mid) * 2), [float]($y + $hump), [float]($x1 - 0.24 * ($x1 - $mid) * 2), [float]($y + $hump), [float]$x1, [float]$y)
    $g.DrawPath($pen, $p)
    $p.Dispose()
  }

  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  , [pscustomobject]@{ Size = $s; Data = $ms.ToArray() }
}

# Assemble the ICO container.
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($entry in $pngs) {
  $sz = $entry.Size
  $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$entry.Data.Length)
  $bw.Write([uint32]$offset)
  $offset += $entry.Data.Length
}
foreach ($entry in $pngs) { $bw.Write($entry.Data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($iconPath, $out.ToArray())
$bw.Close()
Write-Host "Icon written: $iconPath ($((Get-Item $iconPath).Length) bytes, $($pngs.Count) sizes)"
