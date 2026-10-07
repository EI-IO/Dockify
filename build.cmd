@echo off
rem 编译 Dockify（双击即可）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
