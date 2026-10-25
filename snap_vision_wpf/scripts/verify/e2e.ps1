# E2E 验证：合成夹具窗 + 模拟 Alt+右键拖选（钩子→捕获→本地 OCR→气泡）
# 复用 Flutter 版 sim_local.ps1 的交互合成方式，目标换成 WPF 版
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
public class ProbeE {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
}
"@
[ProbeE]::SetProcessDPIAware() | Out-Null

$Before = if (Test-Path $Log) { @(Get-Content -Encoding UTF8 $Log).Count } else { 0 }

function Save-Chat([string]$Name) {
    $proc = Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1
    Add-Type -AssemblyName System.Drawing
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ProbeE2 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
"@
    $rect = New-Object ProbeE2+RECT
    [ProbeE2]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) { Write-Output "SAVE_SKIP invalid rect $w x $h"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

# 合成夹具窗（患者列表，含可 OCR 文本）
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

Start-Process (Join-Path $Bin 'snap-vision.exe')
Start-Sleep -Seconds 6

# Alt+右键拖选 (200,150) → (600,350)
[ProbeE]::SetCursorPos(200, 150) | Out-Null
Start-Sleep -Milliseconds 300
[ProbeE]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)      # Alt down
Start-Sleep -Milliseconds 150
[ProbeE]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero) # RBUTTON down
Start-Sleep -Milliseconds 120
[ProbeE]::SetCursorPos(600, 350) | Out-Null
Start-Sleep -Milliseconds 180
[ProbeE]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero) # RBUTTON up
Start-Sleep -Milliseconds 100
[ProbeE]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)      # Alt up

Start-Sleep -Seconds 20   # 捕获 + PNG + OCR + 气泡
Save-Chat 'wpf_e2e_after_shot.png'
$p = Get-Process snap-vision -ErrorAction Stop
Write-Output ("E2E WS={0}MB" -f [math]::Round($p.WorkingSet64/1MB))

$After = @(Get-Content -Encoding UTF8 $Log)
Write-Output '=== new log lines ==='
$After | Select-Object -Skip $Before | Select-Object -First 25 | ForEach-Object { Write-Output $_ }
Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Output 'ALL_DONE_E2E'
