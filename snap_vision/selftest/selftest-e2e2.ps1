# E2E v2：Alt+右键按下 → 拖动 → 右键松开 = 完成；随后等待 GLM 回复
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

Write-Output '=== Alt+RBDown, RELATIVE drag (+70,+45)x12, RBUp ==='
[NativeInput]::SetCursorPos(500, 500) | Out-Null
Start-Sleep -Milliseconds 200
[NativeInput]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)   # Alt down
Start-Sleep -Milliseconds 120
[NativeInput]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)  # RB down
Start-Sleep -Milliseconds 600

# 相对移动（物理像素 mickeys，不受 DPI 虚拟化影响），总位移约 (840, 540)
for ($i = 1; $i -le 12; $i++) {
  [NativeInput]::mouse_event(0x0001, 70, 45, 0, [UIntPtr]::Zero)  # MOVE relative
  Start-Sleep -Milliseconds 40
}
Start-Sleep -Milliseconds 300
[NativeInput]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)  # RB up
Start-Sleep -Milliseconds 150
[NativeInput]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)     # Alt up

Write-Output '=== waiting 6s (chat bubble + busy) ==='
Start-Sleep -Seconds 6
Shot "$root\e2e2-01-sent.png"
Write-Output 'shot e2e2-01-sent.png'

Write-Output '=== waiting 35s for GLM reply ==='
Start-Sleep -Seconds 35
Shot "$root\e2e2-02-reply.png"
Write-Output 'shot e2e2-02-reply.png'

$proc = Get-Process snap-vision -ErrorAction SilentlyContinue
if ($proc) { $proc | Stop-Process -Force; Write-Output 'CLEANUP killed' } else { Write-Output 'CLEANUP already exited' }
Write-Output 'E2E2_DONE'
