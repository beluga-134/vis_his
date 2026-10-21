# Snap Vision 构建 debug exe：与 check.ps1 相同的工具链环境，改为 cargo build
[CmdletBinding()]
param([switch]$DebugBuild)

$ErrorActionPreference = 'Stop'

$ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$QqCodexTooling = 'E:\qq_codex\qq_codex\.tooling\windows'

$CargoHome = Join-Path $QqCodexTooling 'cargo'
$RustupHome = Join-Path $QqCodexTooling 'rustup'
$CargoCommand = Join-Path (Join-Path $CargoHome 'bin') 'cargo.exe'
$VsDevCmdPath = Join-Path $QqCodexTooling 'vs-build-tools\Common7\Tools\VsDevCmd.bat'

foreach ($Required in @($CargoCommand, $VsDevCmdPath)) {
    if (-not (Test-Path -LiteralPath $Required -PathType Leaf)) {
        throw "依赖 qq_codex 工具链缺失：$Required"
    }
}

$env:RUSTUP_HOME = $RustupHome
$env:CARGO_HOME = $CargoHome
$env:Path = (Join-Path $CargoHome 'bin') + ';' + $env:Path

$Command = 'call "{0}" -no_logo -arch=x64 -host_arch=x64 >nul && set' -f $VsDevCmdPath
$EnvironmentLines = @(& $env:ComSpec /d /s /c $Command)
if ($LASTEXITCODE -ne 0) {
    throw "Visual Studio developer environment failed to load from: $VsDevCmdPath"
}
foreach ($Line in $EnvironmentLines) {
    $Separator = $Line.IndexOf('=')
    if ($Separator -le 0) {
        continue
    }
    $Name = $Line.Substring(0, $Separator)
    if ($Name.StartsWith('=')) {
        continue
    }
    $Value = $Line.Substring($Separator + 1)
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

Set-Location $ProjectRoot
$Profile = if ($DebugBuild) { 'debug' } else { 'release' }
$ExtraArgs = @()
if (-not $DebugBuild) {
    # release 构建启用 custom-protocol：内嵌前端资源，双击即可运行
    $ExtraArgs += '--features'
    $ExtraArgs += 'custom-protocol'
}
& $CargoCommand build --manifest-path (Join-Path $ProjectRoot 'src-tauri\Cargo.toml') --profile $Profile @ExtraArgs
$ExitCode = $LASTEXITCODE
if ($ExitCode -eq 0) {
    Write-Host ('EXE=' + (Join-Path $ProjectRoot ("src-tauri\target\$Profile\snap-vision.exe")))
}
exit $ExitCode
