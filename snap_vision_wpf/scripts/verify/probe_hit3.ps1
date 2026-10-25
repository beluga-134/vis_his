# 探测3：决定性点击实验（✕ 退出 / 挪位后 ⚙ 面板）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WP4 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
}
"@
[WP4]::SetProcessDPIAware() | Out-Null

function Click($x, $y) {
    [WP4]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 300
    [WP4]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    [WP4]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 600
}

# ===== 实验 A：新鲜启动（无任何拖选/覆盖层），点 ✕ =====
Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process 'E:\qq_codex\vis_his\snap_vision\bin\snap-vision.exe'
Start-Sleep -Seconds 6
$p = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
$hw = $p.MainWindowHandle
$r = New-Object WP4+RECT
[WP4]::GetWindowRect($hw, [ref]$r) | Out-Null
Write-Output ("A: app hwnd=0x{0:X} rect=({1},{2})-({3},{4})" -f $hw, $r.Left, $r.Top, $r.Right, $r.Bottom)
$hit = [WP4]::WindowFromPoint(($r.Right - 23), ($r.Top + 19))
Write-Output ("A: WindowFromPoint at close btn -> 0x{0:X} (app=0x{1:X}) {2}" -f $hit, $hw, $(if ($hit -eq $hw) {'MATCH'} else {'OTHER'}))
Click ($r.Right - 23) ($r.Top + 19)
Start-Sleep -Seconds 2
$p2 = Get-Process snap-vision -ErrorAction SilentlyContinue
Write-Output ("A: after close-click alive={0}" -f [bool]$p2)

# ===== 实验 B（仅当 A 失败）：挪窗口到 (0,0)，点 ⚙ 看面板像素 =====
if ($p2) {
    [WP4]::SetWindowPos($hw, [IntPtr]::Zero, 20, 20, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010) | Out-Null # SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE
    Start-Sleep -Milliseconds 400
    [WP4]::GetWindowRect($hw, [ref]$r) | Out-Null
    Write-Output ("B: moved rect=({0},{1})-({2},{3})" -f $r.Left, $r.Top, $r.Right, $r.Bottom)
    # 截图基线（面板关）
    $bmp0 = New-Object System.Drawing.Bitmap(($r.Right-$r.Left), ($r.Bottom-$r.Top))
    $g = [System.Drawing.Graphics]::FromImage($bmp0)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp0.Size)
    $bmp0.Save('E:\qq_codex\vis_his\snap_vision_wpf\scripts\verify\probe3_before.png', [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp0.Dispose()
    $hit = [WP4]::WindowFromPoint(($r.Right - 159), ($r.Top + 19))
    Write-Output ("B: WindowFromPoint at gear btn -> 0x{0:X} (app=0x{1:X}) {2}" -f $hit, $hw, $(if ($hit -eq $hw) {'MATCH'} else {'OTHER'}))
    Click ($r.Right - 159) ($r.Top + 19)
    # 截图（面板应开）
    $bmp1 = New-Object System.Drawing.Bitmap(($r.Right-$r.Left), ($r.Bottom-$r.Top))
    $g = [System.Drawing.Graphics]::FromImage($bmp1)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp1.Size)
    $bmp1.Save('E:\qq_codex\vis_his\snap_vision_wpf\scripts\verify\probe3_after.png', [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp1.Dispose()
    Write-Output 'B: shots saved (probe3_before/after.png)'
    # 顺便试 ✕（挪位后）
    Click ($r.Right - 23) ($r.Top + 19)
    Start-Sleep -Seconds 2
    $p3 = Get-Process snap-vision -ErrorAction SilentlyContinue
    Write-Output ("B: after close-click alive={0}" -f [bool]$p3)
    if ($p3) { $p3 | Stop-Process -Force }
}
Write-Output 'ALL_DONE_PROBE3'
