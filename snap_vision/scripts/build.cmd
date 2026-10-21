@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "SNAP_VISION_EXIT=%ERRORLEVEL%"
endlocal & exit /b %SNAP_VISION_EXIT%
