@echo off
rem Right-click this file > "Run as administrator".
rem 1. puts the standard Program Files permissions back on Utylix's install folder (an earlier build locked it in a way that left the files
rem    unreadable, so Utylix would not start and could not be uninstalled)
rem 2. installs the new Utylix over it (Utylix-Setup.exe from the folder above this one), already with administrator rights
net session >nul 2>&1
if errorlevel 1 (
  echo Please right-click this file and choose "Run as administrator".
  pause
  exit /b 1
)
echo Putting the permissions of C:\Program Files\Utylix back...
icacls "C:\Program Files\Utylix" /reset /T /C /Q
echo.
set SETUP=%~dp0..\Utylix-Setup.exe
if exist "%SETUP%" (
  echo Installing the new Utylix, one moment...
  "%SETUP%" --setup-update --dir "C:\Program Files\Utylix"
) else (
  echo Utylix-Setup.exe was not found next to the tools folder. Run it yourself now.
)
echo.
echo Done. Open Utylix from the Start menu or the desktop shortcut.
pause
