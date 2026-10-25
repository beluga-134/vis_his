# Drag test phase A: start app, open settings panel, take two shots (render lag tolerant), dump rect.
$ErrorActionPreference = 'Stop'
$Proj = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\'))
$Bin  = [IO.Path]::GetFullPath((Join-Path $Proj '..\snap_vision\bin'))
$Out  = Join-Path $Proj 'scripts\verify'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class TB2 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
"@
[TB2]::SetProcessDPIAware() | Out-Null

function Get-AppProc { Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1 }
function Get-AppRect {
    $proc = Get-AppProc
    $r = New-Object TB2+RECT
    [TB2]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    @{ L=$r.Left; T=$r.Top; R=$r.Right; B=$r.Bottom }
}
function Click($x, $y) {
    [TB2]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 260
    [TB2]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [TB2]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}
function Save-Chat([string]$Name) {
    $proc = Get-Process snap-vision -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { Write-Output "SAVE_SKIP no process ($Name)"; return }
    if ($proc.MainWindowHandle -eq 0) { Write-Output "SAVE_SKIP hidden ($Name)"; return }
    $r = New-Object TB2+RECT
    [TB2]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right-$r.Left; $h = $r.Bottom-$r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "SHOT $Name"
}

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Start-Process (Join-Path $Bin 'snap-vision.exe')
Start-Sleep -Seconds 6

$rc = Get-AppRect
"$($rc.L) $($rc.T) $($rc.R) $($rc.B)" | Set-Content (Join-Path $Out 'drag_rect.txt')

Click ($rc.R - 157) ($rc.T + 19)          # gear: open settings panel
Start-Sleep -Milliseconds 1200
Save-Chat 'drag_0.png'
Start-Sleep -Milliseconds 1000
Save-Chat 'drag_0b.png'
Write-Output 'ALL_DONE_DRAG_A'
