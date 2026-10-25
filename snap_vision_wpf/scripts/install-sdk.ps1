# .NET 8 SDK 用户级安装（免管理员）：dotnet-install.ps1 zip 安装到 %LOCALAPPDATA%\Microsoft\dotnet
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$dotnet = Join-Path $dir 'dotnet.exe'

if (Test-Path $dotnet) {
  $sdks = & $dotnet --list-sdks 2>$null
  if ($sdks -match '^8\.') {
    Write-Output "SDK_ALREADY_PRESENT"
    $sdks
    Write-Output 'ALL_DONE_SDK8 0'
    exit 0
  }
}

$installer = Join-Path $env:TEMP 'dotnet-install.ps1'
Invoke-WebRequest -Uri https://dot.net/v1/dotnet-install.ps1 -OutFile $installer
& $installer -Channel 8.0 -InstallDir $dir
if ($LASTEXITCODE -ne 0) { throw "install failed rc=$LASTEXITCODE" }

Write-Output 'INSTALLED:'
& $dotnet --list-sdks
& $dotnet --list-runtimes | Select-String 'WindowsDesktop' | Select-Object -First 2
Write-Output "ALL_DONE_SDK8 0"
