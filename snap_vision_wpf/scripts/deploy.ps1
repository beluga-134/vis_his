# 部署：artifacts\publish\snap-vision.exe → snap_vision\bin\（与旧版同名替换）
# 参数 -CleanFlutter：删除 Flutter 遗留（flutter_windows.dll / data\ / 插件 dll）——M5 用
param([switch]$CleanFlutter)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$pub = Join-Path $root 'artifacts\publish'
$bin = 'E:\qq_codex\vis_his\snap_vision\bin'

if (-not (Test-Path (Join-Path $pub 'snap-vision.exe'))) {
  throw "publish 产物不存在：$pub\snap-vision.exe（先跑 scripts\build.cmd）"
}

# 旧进程必须先退（exe 被占用无法覆盖）
$old = Get-Process -Name 'snap-vision' -ErrorAction SilentlyContinue
if ($old) {
  Write-Output "kill old snap-vision pid=$($old.Id -join ',')"
  $old | Stop-Process -Force
  Start-Sleep -Milliseconds 800
}

Copy-Item (Join-Path $pub 'snap-vision.exe') (Join-Path $bin 'snap-vision.exe') -Force
Copy-Item (Join-Path $pub 'capture.dll') (Join-Path $bin 'capture.dll') -Force
Write-Output "DEPLOYED $bin\snap-vision.exe"

# vis_his 根目录若放有运行副本（sidecars 为 junction），同步更新
$rootExe = 'E:\qq_codex\vis_his\snap-vision.exe'
if (Test-Path $rootExe) {
  Copy-Item (Join-Path $pub 'snap-vision.exe') $rootExe -Force
  Copy-Item (Join-Path $pub 'capture.dll') 'E:\qq_codex\vis_his\capture.dll' -Force
  Write-Output "SYNCED $rootExe"
  # 根副本 sidecars 是实体目录（用户要求不用 junction）：增量同步，只拷有变化的文件
  $rootSidecars = 'E:\qq_codex\vis_his\sidecars'
  if (Test-Path $rootSidecars) {
    robocopy (Join-Path $bin 'sidecars') $rootSidecars /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -le 7) { Write-Output "SYNCED $rootSidecars" } else { Write-Output "WARN sidecars sync rc=$LASTEXITCODE" }
  }
  # 会话不分叉：bin/his 与根 his 的 jsonl 互为硬链接（只补缺失的，已存在不动）
  $rootHis = 'E:\qq_codex\vis_his\his'
  $binHis = Join-Path $bin 'his'
  foreach ($pair in @(@($binHis, $rootHis), @($rootHis, $binHis))) {
    $srcDir = $pair[0]; $dstDir = $pair[1]
    if (-not (Test-Path $srcDir)) { continue }
    Get-ChildItem $srcDir -Filter 'his_vis-*.jsonl' -ErrorAction SilentlyContinue | ForEach-Object {
      $dst = Join-Path $dstDir $_.Name
      if (-not (Test-Path $dst)) {
        New-Item -ItemType HardLink -Path $dst -Target $_.FullName -ErrorAction SilentlyContinue | Out-Null
      }
    }
  }
  Write-Output "SYNCED his hardlinks"
}

if ($CleanFlutter) {
  $leftovers = @('flutter_windows.dll', 'data') | ForEach-Object { Join-Path $bin $_ }
  $leftovers += Get-ChildItem $bin -Filter '*_plugin*.dll' -ErrorAction SilentlyContinue | ForEach-Object FullName
  foreach ($f in $leftovers) {
    if (Test-Path $f) {
      Remove-Item $f -Recurse -Force
      Write-Output "REMOVED $f"
    }
  }
  Write-Output 'ALL_DONE_DEPLOY_CLEAN'
} else {
  Write-Output 'ALL_DONE_DEPLOY'
}
