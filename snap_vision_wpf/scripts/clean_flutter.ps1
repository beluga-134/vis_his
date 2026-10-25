# M5 清理：删除 Flutter 版源码 + bin 目录 Flutter 遗留
# 前置：WPF 版全量验证通过后执行（deploy.ps1 -CleanFlutter 已清 bin 遗留）
$ErrorActionPreference = 'Stop'
$root = 'E:\qq_codex\vis_his'
$flutterSrc = Join-Path $root 'snap_vision_flutter'

if (-not (Test-Path (Join-Path $root 'snap_vision_wpf\src\SnapVision\SnapVision.csproj'))) {
  throw 'WPF 工程不存在，中止清理'
}
if (-not (Test-Path (Join-Path $root 'snap_vision\bin\snap-vision.exe'))) {
  throw 'bin\snap-vision.exe 不存在，中止清理'
}

if (Test-Path $flutterSrc) {
  Remove-Item $flutterSrc -Recurse -Force
  Write-Output "REMOVED $flutterSrc"
}
# 诊断期间的临时探针工程
foreach ($probe in @('mem_probe_fa', 'mem_probe_tmp')) {
  $p = Join-Path $root $probe
  if (Test-Path $p) {
    Remove-Item $p -Recurse -Force
    Write-Output "REMOVED $p"
  }
}
Write-Output 'ALL_DONE_CLEAN_FLUTTER'
