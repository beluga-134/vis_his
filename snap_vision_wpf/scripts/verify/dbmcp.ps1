# 数据库 MCP 问答链路验证（WPF 版，engine=db 强制走 DbChatFlowAsync）
# 流程：ui.json 引擎改 db → 启动 → 窗口拖到右下角避让夹具 → 合成 complex_table.png 顶置
#       → Alt+右键拖选 → 观察 120s（截图 3/12/35s + 终态）→ 恢复 ui.json
# 判定：db_chat: OCR done → query starting 间隔 ≤250ms；query completed；无 query failed
$ErrorActionPreference = 'Stop'
$Proj = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\'))
$Bin  = [IO.Path]::GetFullPath((Join-Path $Proj '..\snap_vision\bin'))
$Out  = Join-Path $Proj 'scripts\verify'
$Log  = Join-Path $Bin 'his_vis.log'
$Ui   = Join-Path $Bin 'snap_vision.ui.json'
$Bak  = Join-Path $Out 'ui.json.bak-dbmcp'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class DB {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int x, int y, uint data, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
"@
[DB]::SetProcessDPIAware() | Out-Null

# --- ui.json 引擎切 db（备份原文件，结束时恢复；正则替换，不依赖 System.Text.Json） ---
Copy-Item $Ui $Bak -Force
$uiRaw = [IO.File]::ReadAllText($Ui)
$origEngine = if ($uiRaw -match '"engine"\s*:\s*"([^"]+)"') { $Matches[1] } else { '?' }
$uiRaw = $uiRaw -replace '"engine"\s*:\s*"[^"]+"', '"engine": "db"'
[IO.File]::WriteAllText($Ui, $uiRaw)
Write-Output "ENGINE set db (was $origEngine)"

$Before = if (Test-Path $Log) { @(Get-Content -Encoding UTF8 $Log).Count } else { 0 }

function Get-AppProc { Get-Process snap-vision -ErrorAction Stop | Select-Object -First 1 }
function Save-Chat([string]$Name) {
    $proc = Get-Process snap-vision -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc -or $proc.MainWindowHandle -eq 0) { Write-Output "SAVE_SKIP ($Name)"; return }
    $r = New-Object DB+RECT
    [DB]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right-$r.Left; $h = $r.Bottom-$r.Top
    if ($w -le 0 -or $h -le 0) { Write-Output "SAVE_SKIP rect ($Name)"; return }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "SHOT $Name"
}

try {
    Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600

    # --- 合成夹具：复杂表 complex_table.png（先建不显示，拖完窗口再置顶显示） ---
    $Fixture = 'E:\qq_codex\vis_his\snap_vision\selftest\db_mcp\complex_table.png'
    if (-not (Test-Path $Fixture)) { throw "fixture missing: $Fixture" }
    $Form = New-Object System.Windows.Forms.Form
    $Form.Text = 'Snap Vision synthetic fixture'
    $Form.FormBorderStyle = 'None'
    $Form.StartPosition = 'Manual'
    $Form.Location = New-Object System.Drawing.Point(100, 100)
    $Form.ClientSize = New-Object System.Drawing.Size(1200, 600)
    $Form.TopMost = $true
    $Picture = New-Object System.Windows.Forms.PictureBox
    $Picture.Dock = 'Fill'
    $Picture.SizeMode = 'Zoom'
    $Picture.BackColor = 'White'
    $Picture.Image = [System.Drawing.Image]::FromFile($Fixture)
    $Form.Controls.Add($Picture)

    Start-Process (Join-Path $Bin 'snap-vision.exe')
    Start-Sleep -Seconds 6

    # --- 窗口拖到右下角，避免盖住夹具（顺带再验一次 DragMove） ---
    $proc = Get-AppProc
    $r = New-Object DB+RECT
    [DB]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
    $w = $r.Right-$r.Left; $h = $r.Bottom-$r.Top
    [DB]::SetCursorPos(($r.Left + 280), ($r.Top + 19)) | Out-Null
    Start-Sleep -Milliseconds 260
    [DB]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 160
    [DB]::SetCursorPos((1920 - $w - 10), (1080 - $h - 10)) | Out-Null
    Start-Sleep -Milliseconds 250
    [DB]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
    $r2 = New-Object DB+RECT
    [DB]::GetWindowRect((Get-AppProc).MainWindowHandle, [ref]$r2) | Out-Null
    Write-Output ("WINDOW_MOVED to ({0},{1})" -f $r2.Left, $r2.Top)

    $Form.Show() | Out-Null
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 500

    # --- Alt+右键拖选表格区域 (105,105)→(1295,695)，扫动轨迹 ---
    try {
        [DB]::SetCursorPos(105, 105) | Out-Null
        Start-Sleep -Milliseconds 300
        [DB]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 150
        [DB]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 200
        for ($i = 1; $i -le 20; $i++) {
            [DB]::SetCursorPos((105 + [int](1190*$i/20)), (105 + [int](590*$i/20))) | Out-Null
            Start-Sleep -Milliseconds 100
        }
    } finally {
        [DB]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
        [DB]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        $Form.Close()
        $Picture.Image.Dispose()
        $Form.Dispose()
    }

    # --- 零输入观察：按日志推进，最长 120s；3/12/35s 定时截图 ---
    $Clock = [Diagnostics.Stopwatch]::StartNew()
    Write-Output 'INPUT_RELEASED'
    $Captured = @{}
    while ($Clock.Elapsed.TotalSeconds -lt 120) {
        foreach ($Seconds in @(3, 12, 35)) {
            if ($Clock.Elapsed.TotalSeconds -ge $Seconds -and -not $Captured.ContainsKey($Seconds)) {
                Save-Chat ("dbmcp-{0}s.png" -f $Seconds)
                $Captured[$Seconds] = $true
            }
        }
        $Observed = @(Get-Content -Encoding UTF8 $Log | Select-Object -Skip $Before)
        if (@($Observed | Where-Object { $_ -match 'db_chat: query completed|db_chat: query failed:' }).Count) {
            Start-Sleep -Milliseconds 1500
            break
        }
        Start-Sleep -Milliseconds 500
    }
    Save-Chat 'dbmcp-final.png'

    $New = @(Get-Content -Encoding UTF8 $Log | Select-Object -Skip $Before)
    $New | Set-Content -Encoding UTF8 (Join-Path $Out 'dbmcp-run.log')
    $Ocr   = @($New | Where-Object { $_ -match 'db_chat: OCR done' })
    $Query = @($New | Where-Object { $_ -match 'db_chat: query starting' })
    $Done  = @($New | Where-Object { $_ -match 'db_chat: query completed' })
    $Fail  = @($New | Where-Object { $_ -match 'db_chat: query failed:' })
    if (-not $Ocr.Count -or -not $Query.Count) { throw 'FAIL missing OCR/query pipeline log' }
    $Gap = [long]($Query[-1].Split(' ')[0]) - [long]($Ocr[-1].Split(' ')[0])
    Write-Output ("OCR_TO_QUERY_MS={0} QUERY_COMPLETED={1}" -f $Gap, ($Done.Count -gt 0))
    if ($Fail.Count) { Write-Output ("QUERY_FAILED: {0}" -f $Fail[-1]) }
    Write-Output '=== new log lines ==='
    $New | Select-Object -First 40 | ForEach-Object { Write-Output $_ }
} finally {
    Get-Process snap-vision -ErrorAction SilentlyContinue | Stop-Process -Force
    Copy-Item $Bak $Ui -Force
    Write-Output "ENGINE restored ($origEngine)"
}
Write-Output 'ALL_DONE_DBMCP'
