# Minimal visual check for settings panel (2026-10-25):
# open gear panel -> shot -> click engine combobox -> shot -> Esc -> click chat to close -> shot.
# Does NOT clear chat, does NOT exit app (leaves it running for the user).
$ErrorActionPreference = 'Stop'
$Proj = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\'))
$Bin  = [IO.Path]::GetFullPath((Join-Path $Proj '..\snap_vision\bin'))
$Out  = Join-Path $Proj 'scripts\verify'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class TB {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
"@
[TB]::SetProcessDPIAware() | Out-Null

function Get-AppProc { Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1 }
function Get-AppRect {
    $proc = Get-AppProc
    $r = New-Object TB+RECT
    [TB]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    @{ L=$r.Left; T=$r.Top; R=$r.Right; B=$r.Bottom }
}
function Click($x, $y) {
    [TB]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 260
    [TB]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [TB]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}
function Press-Key([byte]$b) {
    [TB]::keybd_event($b, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [TB]::keybd_event($b, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}
function Save-Chat([string]$Name) {
    $proc = Get-Process snap-vision -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { Write-Output "SAVE_SKIP no process ($Name)"; return }
    if ($proc.MainWindowHandle -eq 0) { Write-Output "SAVE_SKIP hidden ($Name)"; return }
    $r = New-Object TB+RECT
    [TB]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right-$r.Left; $h = $r.Bottom-$r.Top
    if ($w -le 0 -or $h -le 0) { Write-Output "SAVE_SKIP invalid rect ($Name)"; return }
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
$btnY = $rc.T + 19

Click ($rc.R - 157) $btnY          # gear: open settings panel
Start-Sleep -Milliseconds 2600    # occlusion throttle: panel needs 1-2s to first frame
Save-Chat 'setchk_1_panel.png'

Click ($rc.R - 150) ($rc.T + 118)  # engine combobox row: open dropdown
Start-Sleep -Milliseconds 1400
Save-Chat 'setchk_2_dropdown.png'

Press-Key 0x1B                     # Esc: close dropdown
Click ($rc.L + 120) ($rc.T + 150)  # click chat area: close panel
Save-Chat 'setchk_3_closed.png'

Write-Output 'ALL_DONE_SETTINGSHOT'
