param([string]$In, [string]$Points)
Add-Type -AssemblyName System.Drawing
$nums = $Points -split ' ' | ForEach-Object { [int]$_ }
$b = [System.Drawing.Bitmap]::FromFile($In)
for ($i = 0; $i -lt $nums.Count; $i += 2) {
    $c = $b.GetPixel($nums[$i], $nums[$i + 1])
    "{0},{1} -> #{2:X2}{3:X2}{4:X2}" -f $nums[$i], $nums[$i + 1], $c.R, $c.G, $c.B
}
$b.Dispose()
