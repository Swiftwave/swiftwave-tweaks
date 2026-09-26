param([int]$Pid2, [int]$Index)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$src = @"
using System;
using System.Runtime.InteropServices;
public class Clicker3 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, int extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public static void ClickAt(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(200);
        mouse_event(0x02, 0, 0, 0, 0);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x04, 0, 0, 0, 0);
    }
}
"@
Add-Type -TypeDefinition $src
$proc = Get-Process -Id $Pid2
[Clicker3]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
[Clicker3]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid2)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$btns = @()
foreach ($el in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($el.Current.ClassName -eq "Button" -and $el.Current.Name -eq "") {
        $r = $el.Current.BoundingRectangle
        if ([int]$r.Width -eq 44 -and [int]$r.Height -eq 24) { $btns += $el }
    }
}
"toggles: $($btns.Count)"
$i = 0
foreach ($b in $btns) { $r = $b.Current.BoundingRectangle; "[$i] x=$([int]$r.X) y=$([int]$r.Y) enabled=$($b.Current.IsEnabled)"; $i++ }
if ($Index -lt $btns.Count) {
    $r = $btns[$Index].Current.BoundingRectangle
    $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
    "clicking [$Index] at $cx,$cy"
    [Clicker3]::ClickAt($cx, $cy)
}
