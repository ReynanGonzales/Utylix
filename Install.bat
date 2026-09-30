@echo off
rem Starts the Utylix installer: a wizard that explains the app, asks where to put it, which right-click menus to turn on,
rem and which extra parts (yt-dlp, ffmpeg, the player engine, the AI model) to download. Choose "all users" on its second page
rem to install for everyone on this PC (Windows asks for administrator permission).
cd /d "%~dp0"
if exist "Utylix-Setup.exe" (
    start "" "Utylix-Setup.exe"
) else if exist "Utylix.exe" (
    start "" "Utylix.exe" --setup
) else (
    echo Utylix.exe was not found next to this file. Run build.bat first.
    pause
)
