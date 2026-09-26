param([string]$In, [string]$Out, [int]$X, [int]$Y, [int]$W, [int]$H, [int]$Scale = 4)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($In)
$rect = New-Object System.Drawing.Rectangle($X, $Y, $W, $H)
$crop = $bmp.Clone($rect, $bmp.PixelFormat)
$bw = $W * $Scale
$bh = $H * $Scale
$big = New-Object System.Drawing.Bitmap($bw, $bh)
$g = [System.Drawing.Graphics]::FromImage($big)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.DrawImage($crop, 0, 0, $bw, $bh)
$big.Save($Out)
$g.Dispose(); $big.Dispose(); $crop.Dispose(); $bmp.Dispose()
"saved $Out"
