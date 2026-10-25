# Reproduce titlebar drag failure: rect before -> synthetic titlebar drag -> rect after.
$ErrorActionPreference = 'Stop'
$Proj = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\'))
$Out  = Join-Path $Proj 'scripts\verify'

Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class TB4 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
}
"@
[TB4]::SetProcessDPIAware() | Out-Null

$proc = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
function Get-Rect {
    $r = New-Object TB4+RECT
    [TB4]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    @{ L=$r.Left; T=$r.Top; R=$r.Right; B=$r.Bottom }
}
$b = Get-Rect
$grabX = $b.L + 280; $grabY = $b.T + 19
$hwnd = [TB4]::WindowFromPoint($grabX, $grabY)
Write-Output ("BEFORE L={0} T={1}" -f $b.L, $b.T)
Write-Output ("HIT hwnd=0x{0:X} (expect main 0x{1:X})" -f $hwnd.ToInt64(), $proc.MainWindowHandle.ToInt64())

[TB4]::SetCursorPos($grabX, $grabY) | Out-Null
Start-Sleep -Milliseconds 300
[TB4]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
[TB4]::SetCursorPos(($grabX + 130), ($grabY + 90)) | Out-Null
Start-Sleep -Milliseconds 300
[TB4]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 700

$a = Get-Rect
Write-Output ("AFTER  L={0} T={1}" -f $a.L, $a.T)
$dx = $a.L - $b.L; $dy = $a.T - $b.T
if ([Math]::Abs($dx) -ge 80 -and [Math]::Abs($dy) -ge 40) { Write-Output "DRAG_PASS dx=$dx dy=$dy" }
else { Write-Output "DRAG_FAIL dx=$dx dy=$dy" }

# move window back near original position via another drag (courtesy restore)
[TB4]::SetCursorPos(($a.L + 280), ($a.T + 19)) | Out-Null
Start-Sleep -Milliseconds 300
[TB4]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
[TB4]::SetCursorPos($b.L + 280, ($b.T + 19)) | Out-Null
Start-Sleep -Milliseconds 300
[TB4]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Write-Output 'ALL_DONE_DRAGREPRO'
