@echo off
rem Right-click this file > "Run as administrator".
rem Puts the standard Program Files permissions back on Utylix's install folder (every file and folder inherits them again).
rem (An earlier build locked the folder in a way that left the files unreadable, so Utylix would not start.)
net session >nul 2>&1
if errorlevel 1 (
  echo Please right-click this file and choose "Run as administrator".
  pause
  exit /b 1
)
icacls "C:\Program Files\Utylix" /reset /T /C /Q
echo.
echo Done. Open Utylix from the Start menu or the desktop shortcut.
pause
