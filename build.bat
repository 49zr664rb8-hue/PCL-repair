@echo off
rem ===========================================================================
rem  PCL one-click build   (keep this file ASCII-only and CRLF)
rem
rem  Layouts supported:
rem    A) repo root      <root>\Plain Craft Launcher 2.sln
rem    B) split layout   <root>\PCL3-src\Plain Craft Launcher 2.sln
rem
rem  Output: <src>\Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe
rem ===========================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "SRC="
if exist "%~dp0Plain Craft Launcher 2.sln" set "SRC=%~dp0"
if not defined SRC if exist "%~dp0..\PCL3-src\Plain Craft Launcher 2.sln" set "SRC=%~dp0..\PCL3-src\"

if not defined SRC goto :nosln

set "SLN=!SRC!Plain Craft Launcher 2.sln"
echo ============================================================
echo  PCL build
echo ============================================================
echo  solution : !SLN!

echo.
echo [1/3] Locating build tools ...
set "VSW=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSB="
if exist "%VSW%" (
    for /f "usebackq tokens=*" %%i in (`"%VSW%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do set "MSB=%%i"
)
if not defined MSB set "MSB=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not exist "!MSB!" set "MSB="
if not defined MSB goto :nomsb
echo  compiler : !MSB!

echo.
echo [2/3] Building (Release / Any CPU) ...
echo ------------------------------------------------------------
"!MSB!" "!SLN!" /restore /p:Configuration=Release /p:Platform="Any CPU" /m /v:m /nologo
if errorlevel 1 goto :fail
echo ------------------------------------------------------------

echo.
echo [3/3] Checking output ...
set "EXE=!SRC!Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe"
if not exist "!EXE!" goto :noexe
for %%A in ("!EXE!") do set "SIZE=%%~zA"

echo.
echo ============================================================
echo  BUILD OK
echo ============================================================
echo  exe  : !EXE!
echo  size : !SIZE! bytes
echo.
echo Next steps:
echo   1. copy PCL3.config.ini.example next to the exe, rename to PCL3.config.ini
echo   2. double-click the exe
echo.
pause
exit /b 0

:nosln
echo [ERROR] Cannot find "Plain Craft Launcher 2.sln".
echo         Run this script from the repository root.
goto :fail

:nomsb
echo [ERROR] Build tools not found.
echo.
echo         Install "Visual Studio 2022 Build Tools" with the
echo         ".NET desktop build tools" workload, then run this again.
echo         Download: https://aka.ms/vs/17/release/vs_buildtools.exe
echo.
echo         NOTE: if the installer fails with exit code 5002, it is because
echo         both HTTPS_PROXY and https_proxy are set. Run it with the
echo         lowercase variants removed.
goto :fail

:noexe
echo [ERROR] Build reported success but the exe is missing.
echo         !EXE!
goto :fail

:fail
echo.
echo ------------------------------------------------------------
echo  BUILD FAILED - copy the text above and send it back
echo ------------------------------------------------------------
echo.
pause
exit /b 1
