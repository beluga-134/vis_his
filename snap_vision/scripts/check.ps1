# Snap Vision 编译检查：复用 qq_codex 的便携工具链（cargo/rustup/MSVC）
[CmdletBinding()]
param()

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

# 导入 MSVC 编译环境（cl.exe / link.exe / Windows SDK），写法与 qq_codex env.ps1 一致
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
& $CargoCommand check --manifest-path (Join-Path $ProjectRoot 'src-tauri\Cargo.toml')
exit $LASTEXITCODE
