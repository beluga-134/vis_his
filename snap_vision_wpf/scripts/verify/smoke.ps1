# WPF 版冒烟 + 内存验证（对应 Flutter 版 bisect 系列脚本）
# Stage A: --bubble1 单气泡冒烟（截图窗口）
# Stage B: 默认启动（全量恢复会话 + MD 渲染 + DB 自动挂载）——Flutter 版就是这里爆到 3.5GB
# 用法: powershell -File scripts\verify\smoke.ps1
$ErrorActionPreference = 'Stop'
$Proj = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\'))
$Bin  = [IO.Path]::GetFullPath((Join-Path $Proj '..\snap_vision\bin'))
$Exe  = Join-Path $Bin 'snap-vision.exe'
$Out  = Join-Path $Proj 'scripts\verify'
if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out | Out-Null }

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ProbeW {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
"@
[ProbeW]::SetProcessDPIAware() | Out-Null

function Save-Chat([string]$Name) {
    $proc = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
    $rect = New-Object ProbeW+RECT
    [ProbeW]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap(($rect.Right-$rect.Left), ($rect.Bottom-$rect.Top))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Kill-App {
    Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}

function WS($p) { [math]::Round($p.WorkingSet64 / 1MB) }

# ---- Stage A: 单气泡冒烟 ----
Kill-App
Start-Process $Exe -ArgumentList '--bubble1'
Start-Sleep -Seconds 6
$p = Get-Process snap-vision -ErrorAction Stop
Save-Chat 'wpf_a_bubble1.png'
Write-Output ("A bubble1 WS={0}MB Priv={1}MB" -f (WS $p), [math]::Round($p.PrivateMemorySize64/1MB))
Start-Sleep -Seconds 8
$p.Refresh()
Write-Output ("A bubble1 T+14s WS={0}MB" -f (WS $p))
Kill-App

# ---- Stage B: 全量恢复（MD167 场景，Flutter 版 1GB→3.5GB 爆点）----
Start-Process $Exe
Start-Sleep -Seconds 15
$p = Get-Process snap-vision -ErrorAction Stop
Save-Chat 'wpf_b_restore_full.png'
Write-Output ("B restore WS={0}MB Priv={1}MB" -f (WS $p), [math]::Round($p.PrivateMemorySize64/1MB))
Start-Sleep -Seconds 25
$p.Refresh()
Write-Output ("B restore T+40s WS={0}MB" -f (WS $p))
Start-Sleep -Seconds 20
$p.Refresh()
Write-Output ("B restore T+60s WS={0}MB" -f (WS $p))
Save-Chat 'wpf_b_restore_late.png'

# ---- 异常日志检查 ----
$log = Join-Path $Bin 'his_vis.log'
$tail = if (Test-Path $log) { Get-Content $log -Encoding UTF8 -Tail 40 } else { @() }
$errs = $tail | Where-Object { $_ -match 'FAILED|failed|error|FLUTTER|CRASH|Exception' } | Select-Object -First 5
if ($errs) { Write-Output '=== recent anomalies ==='; $errs | ForEach-Object { Write-Output $_ } }

Kill-App
Write-Output 'ALL_DONE_WPF_SMOKE'
