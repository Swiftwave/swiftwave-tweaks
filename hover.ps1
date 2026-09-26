param([int]$X, [int]$Y)
$src = @"
using System;
using System.Runtime.InteropServices;
public class Mouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@
Add-Type -TypeDefinition $src
[Mouse]::SetCursorPos($X, $Y) | Out-Null
Start-Sleep -Milliseconds 600
