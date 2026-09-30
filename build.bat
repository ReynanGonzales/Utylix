@echo off
rem Builds Utylix.exe (one file, needs nothing installed - the .NET runtime is inside) into this folder.
rem Needs the .NET 10 SDK to build. Publish into app\publish and copy out: publishing straight into the
rem parent folder makes the SDK exclude the source files and produces a broken exe.
cd /d "%~dp0app"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0app\publish"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
copy /y "%~dp0app\publish\Utylix.exe" "%~dp0Utylix.exe" >nul
rem the same file under another name is the installer: started as Utylix-Setup.exe it offers to install Utylix
copy /y "%~dp0app\publish\Utylix.exe" "%~dp0Utylix-Setup.exe" >nul
rmdir /s /q "%~dp0app\publish"
echo.
echo Done: %~dp0Utylix.exe  and the installer %~dp0Utylix-Setup.exe
pause
