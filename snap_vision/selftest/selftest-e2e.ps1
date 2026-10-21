# Snap Vision 端到端自测：启动 → Alt+右键 → 真实拖框 → 等待 GLM 回复 → 截图清理
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativeInput {
  [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
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

Write-Output '=== launching ==='
Start-Process -FilePath 'E:\qq_codex\vis_his\snap_vision\bin\snap-vision.exe'
Start-Sleep -Seconds 7

Write-Output '=== Alt+RightClick to enter capture ==='
[NativeInput]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 150
[NativeInput]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 80
[NativeInput]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 150
[NativeInput]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Seconds 2

Write-Output '=== dragging selection (400,400) -> (1000,700) ==='
[NativeInput]::SetCursorPos(400, 400) | Out-Null
Start-Sleep -Milliseconds 250
[NativeInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 120
[NativeInput]::SetCursorPos(1000, 700) | Out-Null
Start-Sleep -Milliseconds 250
[NativeInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 5
Shot "$root\e2e-01-sent.png"
Write-Output 'shot e2e-01-sent.png (chat should show bubble + busy)'

Write-Output '=== waiting for GLM reply ==='
Start-Sleep -Seconds 35
Shot "$root\e2e-02-reply.png"
Write-Output 'shot e2e-02-reply.png'

$proc = Get-Process snap-vision -ErrorAction SilentlyContinue
if ($proc) { $proc | Stop-Process -Force; Write-Output 'CLEANUP killed' } else { Write-Output 'CLEANUP already exited' }
Write-Output 'E2E_DONE'
