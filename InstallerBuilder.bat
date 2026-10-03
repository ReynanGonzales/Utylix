@echo off
rem Builds the Utylix INSTALLER: installer\Utylix-Setup.exe
rem
rem It is one file: a small starter with the whole Utylix program folder inside (.NET included, nothing needs to be installed on the
rem PC you give it to). Double-click it there: a setup wizard explains Utylix, asks where to put it (just for you, or all users),
rem which right-click menus to turn on and which extra parts to download, and adds Utylix to Settings > Apps.
rem Needs the .NET 10 SDK on THIS PC to build. (build.bat makes the same installer, plus the single-file Utylix.exe.)
setlocal
cd /d "%~dp0app"
echo Building the installer, this takes a minute...
if exist "%~dp0app\publish" rmdir /s /q "%~dp0app\publish"
dotnet publish -c Release -r win-x64 --self-contained true -o "%~dp0app\publish"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
if not exist "%~dp0installer" mkdir "%~dp0installer"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\pack-setup.ps1" -Out "%~dp0installer\Utylix-Setup.exe"
if errorlevel 1 (echo Could not make the installer - is it open? & pause & exit /b 1)
echo.
echo Done: %~dp0installer\Utylix-Setup.exe
echo Give this one file to anyone, or attach it to a GitHub release (together with build.bat's Utylix.exe).
echo.
choice /c YN /m "Run the installer now"
if errorlevel 2 exit /b 0
start "" "%~dp0installer\Utylix-Setup.exe"
