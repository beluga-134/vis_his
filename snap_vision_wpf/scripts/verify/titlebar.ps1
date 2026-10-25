# 标题栏按钮一体化验证（CaptionHeight=0 + DragMove 修复后的回归测试）
# 覆盖：⚙设置面板开 / 面板内点不关 / 面板外点关 / 🗄数据库面板 / 拖拽移动窗口
#       / 🧹清空对话 / —隐藏+Alt+右键自动弹出（顺带本地OCR）/ ✕退出
# 前提：显示缩放 100%（与 e2e.ps1 一致，物理像素直用）
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

$Before = if (Test-Path $Log) { @(Get-Content -Encoding UTF8 $Log).Count } else { 0 }

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

# 按钮中心 x：右缘 -157(⚙) -123(🗄) -89(🧹) -55(—) -21(✕)，y=顶+19
$rc = Get-AppRect
$btnY = $rc.T + 19
$chatX = [int](($rc.L + $rc.R) / 2); $chatY = [int](($rc.T + $rc.B) / 2)

Click ($rc.R - 157) $btnY          # ⚙ 设置面板
Save-Chat 'tb_1_settings_open.png'
Click ($rc.R - 170) ($rc.T + 80)   # 点面板内部（IsInOverlay 修复：不应关闭）
Save-Chat 'tb_2_panel_stays.png'
Click ($rc.L + 120) ($rc.T + 150)  # 点面板左侧聊天区：面板应收起
Save-Chat 'tb_3_panel_closed.png'
Click ($rc.R - 123) $btnY          # 🗄 数据库面板
Save-Chat 'tb_4_dbpanel_open.png'
Click ($rc.L + 120) ($rc.T + 150)
Start-Sleep -Milliseconds 300

# 拖拽移动窗口（DragMove 路径）
$r1 = Get-AppRect
[TB]::SetCursorPos(($r1.L + 280), ($r1.T + 19)) | Out-Null
Start-Sleep -Milliseconds 260
[TB]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 160
[TB]::SetCursorPos(($r1.L + 400), ($r1.T + 99)) | Out-Null
Start-Sleep -Milliseconds 220
[TB]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
$r2 = Get-AppRect
Write-Output ("DRAG dx={0} dy={1}" -f ($r2.L-$r1.L), ($r2.T-$r1.T))
$rc = $r2

# 🧹 清空对话
Click ($rc.R - 89) ($rc.T + 19)
Save-Chat 'tb_5_cleared.png'

# — 隐藏窗口
Click ($rc.R - 55) ($rc.T + 19)
Start-Sleep -Milliseconds 700
$p = Get-AppProc
Write-Output ("HIDDEN handle=0x{0:X} alive={1}" -f $p.MainWindowHandle.ToInt64(), (-not $p.HasExited))

# Alt+右键拖选文本夹具 → 窗口自动弹出 + 清空后 auto 引擎走本地 OCR
$Form = New-Object System.Windows.Forms.Form
$Form.Text = 'Snap Vision synthetic fixture'
$Form.FormBorderStyle = 'None'
$Form.StartPosition = 'Manual'
$Form.Location = New-Object System.Drawing.Point(100, 100)
$Form.ClientSize = New-Object System.Drawing.Size(900, 400)
$Form.TopMost = $true
$Label = New-Object System.Windows.Forms.Label
$Label.Text = "Patient List`n+----+--------+----------+`n| ID | Name   | Dept     |`n| 1  | Zhang  | Cardiology |`n| 2  | Li     | Neurology  |`n| 3  | Wang   | Surgery    |"
$Label.Font = New-Object System.Drawing.Font('Consolas', 16)
$Label.Location = New-Object System.Drawing.Point(20, 20)
$Label.AutoSize = $true
$Form.Controls.Add($Label)
$Form.Add_Shown({ $Form.Activate() })
$Form.Show() | Out-Null
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 500

try {
    [TB]::SetCursorPos(200, 150) | Out-Null
    Start-Sleep -Milliseconds 300
    [TB]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)      # Alt down
    Start-Sleep -Milliseconds 150
    [TB]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero) # RB down
    Start-Sleep -Milliseconds 150
    [TB]::SetCursorPos(600, 350) | Out-Null
    Start-Sleep -Milliseconds 200
    [TB]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero) # RB up
    Start-Sleep -Milliseconds 120
    [TB]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)      # Alt up
} finally {
    Start-Sleep -Seconds 14   # 冻屏裁剪 + OCR + 气泡渲染
    $Form.Close(); $Form.Dispose()
}
$p = Get-AppProc
Write-Output ("RESHOWN handle=0x{0:X}" -f $p.MainWindowHandle.ToInt64())
Save-Chat 'tb_6_capture_ocr.png'

# ✕ 退出
$rc = Get-AppRect
Click ($rc.R - 21) ($rc.T + 19)
Start-Sleep -Seconds 2
$p = Get-Process snap-vision -ErrorAction SilentlyContinue
Write-Output ("CLOSED alive={0}" -f [bool]$p)
if ($p) { $p | Stop-Process -Force }

$After = @(Get-Content -Encoding UTF8 $Log)
Write-Output '=== new log lines ==='
$After | Select-Object -Skip $Before | ForEach-Object { Write-Output $_ }
Write-Output 'ALL_DONE_TITLEBAR'
