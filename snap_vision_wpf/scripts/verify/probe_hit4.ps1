# 探测4：新鲜实例逐按钮测试 + 像素差分（⚙/🗄/🧹/拖拽）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WP5 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
}
"@
[WP5]::SetProcessDPIAware() | Out-Null
$Out = 'E:\qq_codex\vis_his\snap_vision_wpf\scripts\verify'

function Click($x, $y) {
    [WP5]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 350
    [WP5]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [WP5]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 700
}
function Shot([string]$name) {
    $p = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
    if ($p.MainWindowHandle -eq 0) { Write-Output "SHOT $name SKIP_HIDDEN"; return }
    $r = New-Object WP5+RECT
    [WP5]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right-$r.Left; $h = $r.Bottom-$r.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "SHOT $name"
}

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process 'E:\qq_codex\vis_his\snap_vision\bin\snap-vision.exe'
Start-Sleep -Seconds 7

$p = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
$hw = $p.MainWindowHandle
$r = New-Object WP5+RECT
[WP5]::GetWindowRect($hw, [ref]$r) | Out-Null
Write-Output ("app rect=({0},{1})-({2},{3}) hwnd=0x{4:X}" -f $r.Left, $r.Top, $r.Right, $r.Bottom, $hw)
$btnY = $r.Top + 19
$chatX = [int](($r.Left+$r.Right)/2); $chatY = [int](($r.Top+$r.Bottom)/2)

Shot 'p4_0_base.png'

# 1) ⚙
Click ($r.Right - 159) $btnY
Shot 'p4_1_gear.png'
# 2) 面板外点击（聊天区）
Click $chatX $chatY
Shot 'p4_2_after_outside.png'
# 3) 🗄
Click ($r.Right - 125) $btnY
Shot 'p4_3_db.png'
# 4) 面板外点击
Click $chatX $chatY
Shot 'p4_4_after_outside2.png'
# 5) 🧹 清空
Click ($r.Right - 91) $btnY
Shot 'p4_5_clear.png'
# 6) 拖拽标题栏（先刷新句柄/矩形）
$p = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
$hw = $p.MainWindowHandle
$r = New-Object WP5+RECT
[WP5]::GetWindowRect($hw, [ref]$r) | Out-Null
Write-Output ("before-drag rect=({0},{1})" -f $r.Left, $r.Top)
[WP5]::SetCursorPos(($r.Left + 280), ($r.Top + 19)) | Out-Null
Start-Sleep -Milliseconds 350
[WP5]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 200
[WP5]::SetCursorPos(($r.Left + 400), ($r.Top + 99)) | Out-Null
Start-Sleep -Milliseconds 250
[WP5]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 700
$r2 = New-Object WP5+RECT
[WP5]::GetWindowRect($hw, [ref]$r2) | Out-Null
Write-Output ("DRAG dx={0} dy={1}" -f ($r2.Left-$r.Left), ($r2.Top-$r.Top))
Shot 'p4_6_after_drag.png'

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output 'ALL_DONE_PROBE4'
