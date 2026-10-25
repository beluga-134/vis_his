# 探测：按钮坐标处命中的到底是哪个窗口？（排查标题栏点击无效）
$ErrorActionPreference = 'Stop'
$Bin = 'E:\qq_codex\vis_his\snap_vision\bin'
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WP {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(int x, int y);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
}
"@
[WP]::SetProcessDPIAware() | Out-Null

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Start-Process (Join-Path $Bin 'snap-vision.exe')
Start-Sleep -Seconds 6

$proc = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
Write-Output ("procs={0} picked pid={1} hwnd=0x{2:X}" -f @(Get-Process snap-vision).Count, $proc.Id, $proc.MainWindowHandle)
$hwnd = $proc.MainWindowHandle
$r = New-Object WP+RECT
[WP]::GetWindowRect($hwnd, [ref]$r) | Out-Null
Write-Output ("app rect=({0},{1})-({2},{3})" -f $r.Left, $r.Top, $r.Right, $r.Bottom)
$exstyle = [WP]::GetWindowLong($hwnd, -20)
Write-Output ("app ExStyle=0x{0:X} (0x8=TOPMOST, 0x80000=LAYERED, 0x20=TRANSPARENT)" -f $exstyle)

function Probe([string]$tag, [int]$x, [int]$y) {
    $h = [WP]::WindowFromPoint($x, $y)
    $t = New-Object System.Text.StringBuilder 256
    [WP]::GetWindowText($h, $t, 256) | Out-Null
    $c = New-Object System.Text.StringBuilder 256
    [WP]::GetClassName($h, $c, 256) | Out-Null
    $pid2 = 0
    [WP]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    $pname = try { (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName } catch { '?' }
    $same = if ($h -eq $hwnd) { 'APP' } else { 'OTHER' }
    Write-Output ("{0} ({1},{2}) -> {3} hwnd=0x{4:X} class={5} title={6} proc={7}(pid {8})" -f $tag, $x, $y, $same, $h, $c, $t, $pname, $pid2)
}

$midY = $r.Top + 19
Probe 'btn_gear  ' ($r.Right - 159) $midY
Probe 'btn_db    ' ($r.Right - 125) $midY
Probe 'btn_clear ' ($r.Right - 91)  $midY
Probe 'btn_hide  ' ($r.Right - 57)  $midY
Probe 'btn_close ' ($r.Right - 23)  $midY
Probe 'title_mid ' ([int](($r.Left+$r.Right)/2)) $midY
Probe 'chat_mid  ' ([int](($r.Left+$r.Right)/2)) ([int](($r.Top+$r.Bottom)/2))
Probe 'composer  ' ([int](($r.Left+$r.Right)/2)) ($r.Bottom - 40)

# 枚举所有可见 TOPMOST 窗口
Write-Output '=== topmost visible windows ==='
[WP]::EnumWindows({ param($h, $l)
    if ([WP]::IsWindowVisible($h)) {
        $ex = [WP]::GetWindowLong($h, -20)
        if ($ex -band 0x8) {
            $t = New-Object System.Text.StringBuilder 256
            [WP]::GetWindowText($h, $t, 256) | Out-Null
            $c = New-Object System.Text.StringBuilder 256
            [WP]::GetClassName($h, $c, 256) | Out-Null
            $pid3 = 0
            [WP]::GetWindowThreadProcessId($h, [ref]$pid3) | Out-Null
            $pname = try { (Get-Process -Id $pid3 -ErrorAction Stop).ProcessName } catch { '?' }
            $rr = New-Object WP+RECT
            [WP]::GetWindowRect($h, [ref]$rr) | Out-Null
            Write-Output ("  hwnd=0x{0:X} class={1} title={2} proc={3} rect=({4},{5})-({6},{7})" -f $h, $c, $t, $pname, $rr.Left, $rr.Top, $rr.Right, $rr.Bottom)
        }
    }
    return $true
}, [IntPtr]::Zero) | Out-Null

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output 'ALL_DONE_PROBE'
