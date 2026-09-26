param([int]$Pid2, [string]$CardTitle)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid2)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
"window: $($win.Current.Name)"
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
"total elements: $($all.Count)"
$classes = @{}
foreach ($el in $all) {
    $cn = $el.Current.ClassName
    if (-not $classes.ContainsKey($cn)) { $classes[$cn] = 0 }
    $classes[$cn]++
}
$classes.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 25 | ForEach-Object { "$($_.Value) x $($_.Key)" }
"--- control types ---"
$cts = @{}
foreach ($el in $all) {
    $ct = $el.Current.ControlType.ProgrammaticName
    if (-not $cts.ContainsKey($ct)) { $cts[$ct] = 0 }
    $cts[$ct]++
}
$cts.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Value) x $($_.Key)" }
"--- buttons/customs/thumbs ---"
foreach ($el in $all) {
    $ct = $el.Current.ControlType.ProgrammaticName
    if ($ct -match "Button|Custom|Thumb") {
        $r = $el.Current.BoundingRectangle
        $toggle = "no"
        try { $p = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); if ($null -ne $p) { $toggle = $p.ToggleState } } catch {}
        "$ct | cls=$($el.Current.ClassName) | name=$($el.Current.Name) | toggle=$toggle | x=$([int]$r.X) y=$([int]$r.Y) w=$([int]$r.Width) h=$([int]$r.Height)"
    }
}
