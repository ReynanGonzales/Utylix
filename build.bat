@echo off
rem Builds Utylix.exe (one file, needs nothing installed - the .NET runtime is inside) into this folder.
rem Needs the .NET 10 SDK to build. Publish into app\publish and copy out: publishing straight into the
rem parent folder makes the SDK exclude the source files and produces a broken exe.
cd /d "%~dp0app"
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0app\publish"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
rem a running Utylix.exe is locked by Windows and cannot be replaced; the new build stays in app\publish then
copy /y "%~dp0app\publish\Utylix.exe" "%~dp0Utylix.exe" >nul
if errorlevel 1 (
  echo.
  echo Could not replace %~dp0Utylix.exe - Utylix is probably still running from this folder.
  echo Exit it from the tray icon ^(right-click, Exit^) and run build.bat again.
  echo The new build is kept in %~dp0app\publish\Utylix.exe
  pause & exit /b 1
)
rem the same file under another name is the installer: started as Utylix-Setup.exe it offers to install Utylix
copy /y "%~dp0app\publish\Utylix.exe" "%~dp0Utylix-Setup.exe" >nul
if errorlevel 1 (echo Could not replace %~dp0Utylix-Setup.exe - close it and run build.bat again. & pause & exit /b 1)
rmdir /s /q "%~dp0app\publish"
echo.
echo Done: %~dp0Utylix.exe  and the installer %~dp0Utylix-Setup.exe
pause
