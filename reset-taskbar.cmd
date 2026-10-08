@echo off
rem Reset the taskbar to its original state (crop region + content area + tray position).
rem Works whether Dockify.exe sits next to this file or in a dist\ subfolder.

set EXE=%~dp0Dockify.exe
if not exist "%EXE%" set EXE=%~dp0dist\Dockify.exe

if not exist "%EXE%" (
  echo [ERROR] Dockify.exe not found next to this script or in dist\.
  echo         Put this file in the same folder as Dockify.exe.
  pause
  exit /b 1
)

echo Resetting the taskbar ...
"%EXE%" --reset
if errorlevel 1 (
  echo [ERROR] Reset failed. Try running this script as administrator.
  pause
  exit /b 1
)
echo Done. The taskbar is back to its original state.
timeout /t 2 >nul