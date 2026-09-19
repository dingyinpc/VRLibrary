param([long]$Hwnd = 8914880)
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@
$h = [IntPtr]$Hwnd
Write-Output ("before iconic: " + [Win]::IsIconic($h))
[Win]::ShowWindow($h, 9) | Out-Null      # SW_RESTORE
Start-Sleep -Milliseconds 600
[Win]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 600
Write-Output ("after  iconic: " + [Win]::IsIconic($h))
Write-Output ("foreground   : " + [Win]::GetForegroundWindow())
