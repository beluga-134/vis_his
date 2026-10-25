@echo off
rem 编译 capture.dll（x64）：需 VS Build Tools（cl.exe + vcvars64）。
rem 产物输出到 ..\..\artifacts\native\capture.dll
setlocal
set "VS=E:\qq_codex\qq_codex\.tooling\windows\vs-build-tools\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VS%" (
  echo [ERROR] vcvars64 not found: %VS%
  exit /b 1
)
call "%VS%" >nul || exit /b 1
set "OUT=%~dp0..\..\artifacts\native"
if not exist "%OUT%" mkdir "%OUT%"
cd /d "%~dp0"
cl /nologo /std:c++17 /O2 /utf-8 /W3 /EHsc /LD capture_module.cpp sv_exports.cpp ^
   /Fe:"%OUT%\capture.dll" ^
   /link /DLL /MACHINE:X64 user32.lib gdi32.lib dwmapi.lib shcore.lib
if errorlevel 1 (
  echo [ERROR] capture.dll build failed
  exit /b 1
)
del /q capture_module.obj sv_exports.obj capture_module.exp capture_module.lib 2>nul
echo BUILD_OK %OUT%\capture.dll
