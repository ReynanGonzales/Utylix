@echo off
rem Builds Utylix (needs the .NET 10 SDK):
rem   app\publish\         the program folder: Utylix.exe + its files, .NET included (nothing has to be installed)
rem   Utylix-Setup.exe     ONE file that carries that folder: it installs Utylix, and Utylix's updater runs it to update itself
rem   Utylix.exe           the same program as one single file: Utylix 1.4.0 and older look for this name when updating,
rem                        and it is handy to carry around
rem Publish into folders under app and copy out: publishing straight into the parent folder makes the SDK exclude the source files.
cd /d "%~dp0app"
if exist "%~dp0app\publish" rmdir /s /q "%~dp0app\publish"
dotnet publish -c Release -r win-x64 --self-contained true -o "%~dp0app\publish"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\pack-setup.ps1"
if errorlevel 1 (echo Could not make Utylix-Setup.exe - is it open? Close it and run build.bat again. & pause & exit /b 1)

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "%~dp0app\publish-single"
if errorlevel 1 (echo Build of the single file failed. & pause & exit /b 1)
rem a running Utylix.exe is locked by Windows and cannot be replaced; the new build stays in app\publish-single then
copy /y "%~dp0app\publish-single\Utylix.exe" "%~dp0Utylix.exe" >nul
if errorlevel 1 (
  echo.
  echo Could not replace %~dp0Utylix.exe - Utylix is probably still running from this folder.
  echo Exit it from the tray icon ^(right-click, Exit^) and run build.bat again.
  echo The new build is kept in %~dp0app\publish-single\Utylix.exe
  pause & exit /b 1
)
rmdir /s /q "%~dp0app\publish-single"
echo.
echo Done: the installer %~dp0Utylix-Setup.exe, the single file %~dp0Utylix.exe, the program folder %~dp0app\publish
pause
