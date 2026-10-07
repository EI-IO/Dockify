@echo off
rem Put your taskbar back to normal (undo cropping / dock).
"%~dp0dist\Dockify.exe" --reset
echo.
echo Done. If the taskbar still looks off, restart Windows Explorer:
echo   Ctrl+Shift+Esc -^> find "Windows Explorer" -^> Restart
pause
