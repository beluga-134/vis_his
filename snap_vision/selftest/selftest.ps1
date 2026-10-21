# Snap Vision 自测：启动 exe → 截屏 → 模拟 Alt+右键 → Esc → 清理
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeInput {
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
}
"@

function Shot($path) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose()
  $bmp.Dispose()
}

$root = 'E:\qq_codex\vis_his\snap_vision\selftest'
New-Item -ItemType Directory -Force -Path $root | Out-Null

Write-Output '=== launching snap-vision.exe ==='
Start-Process -FilePath 'E:\qq_codex\vis_his\snap_vision\bin\snap-vision.exe'
Start-Sleep -Seconds 7

$proc = Get-Process snap-vision -ErrorAction SilentlyContinue
if ($proc) { Write-Output ("ALIVE pid=" + ($proc.Id -join ',')) } else { Write-Output 'DEAD process not found' }
Shot "$root\01-chat.png"
Write-Output 'shot 01-chat.png'

Start-Sleep -Milliseconds 500
Write-Output '=== simulating Alt+RightClick ==='
[NativeInput]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 150
[NativeInput]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 80
[NativeInput]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 150
[NativeInput]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Seconds 3
Shot "$root\02-overlay.png"
Write-Output 'shot 02-overlay.png'

Write-Output '=== sending Esc ==='
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Seconds 2
Shot "$root\03-after-esc.png"
Write-Output 'shot 03-after-esc.png'

$proc = Get-Process snap-vision -ErrorAction SilentlyContinue
if ($proc) { $proc | Stop-Process -Force; Write-Output 'CLEANUP killed snap-vision' } else { Write-Output 'CLEANUP already exited' }
Write-Output 'SELFTEST_DONE'
