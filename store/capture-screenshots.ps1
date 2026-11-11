Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$native = @'
using System;using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RC r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint dx,uint dy,uint d,UIntPtr e);
  public struct RC{public int L;public int T;public int R;public int B;}
}
'@
Add-Type -TypeDefinition $native
$outDir = Join-Path $PSScriptRoot 'screenshots'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$p = Get-Process SwiftwaveTweaks | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$pages = 'Dashboard','Recommended','Gaming','Power','Startup'
foreach ($page in $pages) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $page)
  $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if (-not $item) { Write-Output "MISSING $page"; continue }
  $invoked = $false
  try {
    $pat = $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pat.Invoke(); $invoked = $true
  } catch {
    $r0 = $item.Current.BoundingRectangle
    [W]::SetCursorPos([int]($r0.X + $r0.Width/2), [int]($r0.Y + $r0.Height/2)) | Out-Null
    Start-Sleep -Milliseconds 200
    [W]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 80
    [W]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
  }
  Start-Sleep -Milliseconds 1400
  $rc = New-Object W+RC
  [W]::GetWindowRect($p.MainWindowHandle,[ref]$rc) | Out-Null
  $w = $rc.R - $rc.L; $h = $rc.B - $rc.T
  $bmp = New-Object System.Drawing.Bitmap($w,$h)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($rc.L,$rc.T,0,0,$bmp.Size)
  $final = New-Object System.Drawing.Bitmap(1920,1080)
  $g2 = [System.Drawing.Graphics]::FromImage($final)
  $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g2.DrawImage($bmp,(New-Object System.Drawing.Rectangle(0,0,1920,1080)),(New-Object System.Drawing.Rectangle(0,0,1920,1080)),[System.Drawing.GraphicsUnit]::Pixel)
  $path = Join-Path $outDir ("{0}.png" -f $page)
  $final.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose();$g2.Dispose();$bmp.Dispose();$final.Dispose()
  Write-Output ("saved {0} ({1})" -f $path,$(if($invoked){'invoke'}else{'click'}))
}
