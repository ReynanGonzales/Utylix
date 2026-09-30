@echo off
rem Builds the Utylix INSTALLER: installer\Utylix-Setup.exe
rem
rem It is one file (the .NET runtime is inside, nothing needs to be installed on the PC you give it to). Double-click it there:
rem a setup wizard explains Utylix, asks where to put it (just for you, or all users), which right-click menus to turn on and
rem which extra parts to download, and adds Utylix to Settings > Apps. Needs the .NET 10 SDK on THIS PC to build.
rem
rem (Utylix.exe alone, from build.bat, is the program; the installer is the same program started under the name Utylix-Setup.exe.)
setlocal
cd /d "%~dp0app"
echo Building the installer, this takes a minute...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0app\publish"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
if not exist "%~dp0installer" mkdir "%~dp0installer"
copy /y "%~dp0app\publish\Utylix.exe" "%~dp0installer\Utylix-Setup.exe" >nul
rmdir /s /q "%~dp0app\publish"
echo.
echo Done: %~dp0installer\Utylix-Setup.exe
for %%F in ("%~dp0installer\Utylix-Setup.exe") do echo Size: %%~zF bytes
echo Give this one file to anyone, or attach it to a GitHub release.
echo.
choice /c YN /m "Run the installer now"
if errorlevel 2 exit /b 0
start "" "%~dp0installer\Utylix-Setup.exe"
