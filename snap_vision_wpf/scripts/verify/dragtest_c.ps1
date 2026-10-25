# Drag test phase C: drag the slider thumb from -> to (params from drag_params.json),
# shot after, dump log tail, close panel. Leaves app running.
$ErrorActionPreference = 'Stop'
$Proj = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\'))
$Bin  = [IO.Path]::GetFullPath((Join-Path $Proj '..\snap_vision\bin'))
$Out  = Join-Path $Proj 'scripts\verify'
$Log  = Join-Path $Bin 'his_vis.log'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class TB3 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
"@
[TB3]::SetProcessDPIAware() | Out-Null

function Get-AppProc { Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1 }
function Get-AppRect {
    $proc = Get-AppProc
    $r = New-Object TB3+RECT
    [TB3]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    @{ L=$r.Left; T=$r.Top; R=$r.Right; B=$r.Bottom }
}
function Save-Chat([string]$Name) {
    $proc = Get-Process snap-vision -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { Write-Output "SAVE_SKIP no process ($Name)"; return }
    if ($proc.MainWindowHandle -eq 0) { Write-Output "SAVE_SKIP hidden ($Name)"; return }
    $r = New-Object TB3+RECT
    [TB3]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right-$r.Left; $h = $r.Bottom-$r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "SHOT $Name"
}

$p = Get-Content (Join-Path $Out 'drag_params.json') -Raw | ConvertFrom-Json
$x0 = [int]$p.thumb_abs_x; $y = [int]$p.thumb_abs_y; $x1 = [int]$p.target_abs_x
Write-Output "DRAG ($x0,$y) -> ($x1,$y)"

[TB3]::SetCursorPos($x0, $y) | Out-Null
Start-Sleep -Milliseconds 350
[TB3]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)   # left down on thumb
Start-Sleep -Milliseconds 200
$steps = 14
for ($i = 1; $i -le $steps; $i++) {
    $xx = [int]($x0 + ($x1 - $x0) * $i / $steps)
    [TB3]::SetCursorPos($xx, $y) | Out-Null
    Start-Sleep -Milliseconds 55
}
Start-Sleep -Milliseconds 150
[TB3]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)   # left up
Start-Sleep -Milliseconds 900
Save-Chat 'drag_1.png'

Get-Content $Log -Tail 80 -Encoding UTF8 | Set-Content (Join-Path $Out 'drag_logtail.txt')

# close panel (click chat area left of panel), leave app running
$rc = Get-AppRect
[TB3]::SetCursorPos(($rc.L + 120), ($rc.T + 150)) | Out-Null
Start-Sleep -Milliseconds 200
[TB3]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 90
[TB3]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Write-Output 'ALL_DONE_DRAG_C'
