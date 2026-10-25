# 探测2：formal.exe 窗口布局 + WebView2 窗口属性 + 全屏截图，确认真实遮挡
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WP2 {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
}
"@
[WP2]::SetProcessDPIAware() | Out-Null

function Info([IntPtr]$h, [string]$tag) {
    $r = New-Object WP2+RECT
    [WP2]::GetWindowRect($h, [ref]$r) | Out-Null
    $t = New-Object System.Text.StringBuilder 256
    [WP2]::GetWindowText($h, $t, 256) | Out-Null
    $c = New-Object System.Text.StringBuilder 256
    [WP2]::GetClassName($h, $c, 256) | Out-Null
    $pid2 = 0
    [WP2]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    $pname = try { (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName } catch { '?' }
    $ex = [WP2]::GetWindowLong($h, -20)
    $vis = [WP2]::IsWindowVisible($h)
    $min = [WP2]::IsIconic($h)
    Write-Output ("{0}: hwnd=0x{1:X} class={2} title={3} proc={4} rect=({5},{6})-({7},{8}) ExStyle=0x{9:X} visible={10} minimized={11}" -f $tag, $h, $c, $t, $pname, $r.Left, $r.Top, $r.Right, $r.Bottom, $ex, $vis, $min)
}

# formal.exe 主窗口
$formal = Get-Process formal -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($formal) { Info $formal.MainWindowHandle 'formal_main' } else { Write-Output 'formal_main: 无主窗口（可能全部隐藏/托盘）' }
Write-Output ("formal 进程数: {0}" -f @(Get-Process formal -ErrorAction SilentlyContinue).Count)

# WebView2 僵尸窗口 0x67280（上轮探测值，重启后可能变化——按类名+进程重新找）
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WP3 {
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
"@
$found = New-Object System.Collections.ArrayList
[WP3]::EnumWindows({ param($h, $l)
    $c = New-Object System.Text.StringBuilder 256
    [WP3]::GetClassName($h, $c, 256) | Out-Null
    if ($c.ToString() -eq 'Chrome_RenderWidgetHostHWND') {
        $null = $found.Add($h)
    }
    return $true
}, [IntPtr]::Zero) | Out-Null
Write-Output ("Chrome_RenderWidgetHostHWND 顶层窗口数: {0}" -f $found.Count)
foreach ($h in $found) {
    Info $h 'webview_render'
    $root = [WP2]::GetAncestor($h, 2)
    Info $root '  webview_root'
}

# z-order：从我们的应用窗口向上数 8 层（GW_HWNDPREV=获得 z 序中上方窗口）
Write-Output '=== 启动 snap-vision 并取 z 序 ==='
Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process 'E:\qq_codex\vis_his\snap_vision\bin\snap-vision.exe'
Start-Sleep -Seconds 6
$proc = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
$hw = $proc.MainWindowHandle
Info $hw 'our_app'
$cur = $hw
for ($i = 1; $i -le 8; $i++) {
    $cur = [WP2]::GetWindow($cur, 3) # GW_HWNDPREV
    if ($cur -eq [IntPtr]::Zero) { break }
    Info $cur ("z_above_$i")
}

# 全屏截图
$bmp = New-Object System.Drawing.Bitmap(1920, 1080)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save('E:\qq_codex\vis_his\snap_vision_wpf\scripts\verify\probe_fullscreen.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output 'FULLSCREEN_SAVED'

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output 'ALL_DONE_PROBE2'
