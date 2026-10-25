@echo off
rem Build release: self-contained single-file exe (portable, copy-and-run)
rem Output: snap_vision_wpf\artifacts\publish\snap-vision.exe
setlocal
set "DOTNET=%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
set "ROOT=%~dp0.."
if not exist "%ROOT%\artifacts\native\capture.dll" (
  echo [WARN] capture.dll missing, run native\capture\build.cmd first
)
cd /d "%ROOT%\src\SnapVision"
"%DOTNET%" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%ROOT%\artifacts\publish"
if errorlevel 1 (
  echo [ERROR] publish failed
  exit /b 1
)
copy /y "%ROOT%\artifacts\native\capture.dll" "%ROOT%\artifacts\publish\capture.dll" >nul
echo BUILD_OK %ROOT%\artifacts\publish\snap-vision.exe
