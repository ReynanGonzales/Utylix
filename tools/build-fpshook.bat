@echo off
rem Builds Utylix's frame-limiter hook (native\UtylixFpsHook.c) for 64-bit and 32-bit programs into app\hook\.
rem Needs the Visual Studio C++ build tools (set VSDIR below if they are somewhere else). The two DLLs are kept in the repository,
rem so build.bat does not need the C++ tools: run this only when the source changes.
setlocal
if not defined VSDIR set VSDIR=D:\VSBuildTools
set SRC=%~dp0..\native\UtylixFpsHook.c
if not defined OUT set OUT=%~dp0..\app\hook
rem (for tests: set DEFS=/DUTYLIX_DEBUG and OUT=<some folder> first: the DLL then writes limit-^<pid^>.log next to itself)
if not exist "%OUT%" mkdir "%OUT%"
set TMPD=%TEMP%\UtylixFpsHook
if exist "%TMPD%" rmdir /s /q "%TMPD%"
mkdir "%TMPD%"

call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul
if errorlevel 1 (echo Could not start the 64-bit C++ tools & exit /b 1)
pushd "%TMPD%"
cl /nologo /O2 /GS- /Zl /W3 %DEFS% /LD "%SRC%" /Fe:UtylixFpsHook64.dll /link /NODEFAULTLIB /ENTRY:DllMain /DLL /MACHINE:X64 /SUBSYSTEM:WINDOWS /NOIMPLIB
if errorlevel 1 (popd & echo 64-bit build failed & exit /b 1)
copy /y UtylixFpsHook64.dll "%OUT%\" >nul
popd

call "%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul
if errorlevel 1 (echo Could not start the 32-bit C++ tools & exit /b 1)
pushd "%TMPD%"
cl /nologo /O2 /GS- /Zl /W3 %DEFS% /LD "%SRC%" /Fe:UtylixFpsHook32.dll /link /NODEFAULTLIB /ENTRY:DllMain /DLL /MACHINE:X86 /SUBSYSTEM:WINDOWS /NOIMPLIB
if errorlevel 1 (popd & echo 32-bit build failed & exit /b 1)
copy /y UtylixFpsHook32.dll "%OUT%\" >nul
popd
echo Done: %OUT%
