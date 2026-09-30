@echo off
rem Removes Utylix from this PC (the installed copy): its right-click menus, file types, Start with Windows, shortcuts and its
rem entry in Windows' list of apps. It asks before deleting Utylix's own settings. Same as Settings > Apps > Utylix > Uninstall.
setlocal
set "CMD="
for /f "tokens=2,*" %%a in ('reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Utylix" /v UninstallString 2^>nul ^| find "UninstallString"') do set "CMD=%%b"
if not defined CMD for /f "tokens=2,*" %%a in ('reg query "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\Utylix" /v UninstallString 2^>nul ^| find "UninstallString"') do set "CMD=%%b"
if not defined CMD (
    echo Utylix is not installed with the installer on this PC, so there is nothing to remove.
    pause
    exit /b 1
)
start "" %CMD%
