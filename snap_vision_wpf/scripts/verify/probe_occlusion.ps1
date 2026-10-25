# 探测5：遮挡渲染节流特性（点击⚙后：无输入等待 / 鼠标悬停移动 / 窗外点击）
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class WP6 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
}
"@
[WP6]::SetProcessDPIAware() | Out-Null
$Out = 'E:\qq_codex\vis_his\snap_vision_wpf\scripts\verify'

function Click($x, $y) {
    [WP6]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 350
    [WP6]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 120
    [WP6]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
}
function MoveTo($x, $y) {
    [WP6]::SetCursorPos([int]$x, [int]$y) | Out-Null
    Start-Sleep -Milliseconds 120
}
function Shot([string]$name) {
    $p = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
    if ($p.MainWindowHandle -eq 0) { Write-Output "SHOT $name SKIP_HIDDEN"; return }
    $r = New-Object WP6+RECT
    [WP6]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
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
$r = New-Object WP6+RECT
[WP6]::GetWindowRect($hw, [ref]$r) | Out-Null
Write-Output ("app rect=({0},{1})-({2},{3})" -f $r.Left, $r.Top, $r.Right, $r.Bottom)
$btnY = $r.Top + 19
$midX = [int](($r.Left+$r.Right)/2)

MoveTo ($r.Left - 100) 900   # 鼠标先离开应用窗口
Shot 'p5_0_base.png'

# 1) 点 ⚙ 后无输入等 0.7s
Click ($r.Right - 159) $btnY
MoveTo ($r.Left - 100) 900   # 点完立刻把鼠标移出窗外
Start-Sleep -Milliseconds 700
Shot 'p5_1_after700ms.png'

# 2) 继续无输入再等 4s
Start-Sleep -Seconds 4
Shot 'p5_2_after5s_noinput.png'

# 3) 仅在窗口上移动鼠标（无点击），500ms
MoveTo ($r.Left + 200) ($r.Top + 100)
MoveTo ($r.Left + 300) ($r.Top + 150)
MoveTo ($r.Left + 400) ($r.Top + 200)
MoveTo ($r.Left - 100) 900
Start-Sleep -Milliseconds 400
Shot 'p5_3_after_mousemove.png'

# 4) 点击窗外区域（Retros Messenger 上），1s 后看面板视觉是否还挂着/关闭
Click 100 900
Start-Sleep -Milliseconds 1000
Shot 'p5_4_after_outside_click.png'

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output 'ALL_DONE_PROBE5'
