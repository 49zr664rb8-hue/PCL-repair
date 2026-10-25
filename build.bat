@echo off
rem ============================================================================
rem  PCL3 one-click build
rem
rem  Layout this script expects (either one works):
rem      <root>\PCL3\build.bat   +  <root>\PCL3-src\...        (current layout)
rem      <root>\build.bat        +  <root>\PCL3-src\...
rem
rem  Output: <root>\PCL3-src\Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe
rem ============================================================================
setlocal enabledelayedexpansion
chcp 65001 >nul

set "SLN="
set "BASE="

rem ---- locate the solution: supports both layouts ----
rem    A) repo root        <root>\Plain Craft Launcher 2.sln      （开源仓库的布局）
rem    B) split layout     <root>\PCL3-src\Plain Craft Launcher 2.sln
for %%R in ("%~dp0.." "%~dp0" "%~dp0..\..") do (
    if not defined SLN (
        if exist "%%~fR\PCL3-src\Plain Craft Launcher 2.sln" (
            set "BASE=%%~fR"
            set "SRC=%%~fR\PCL3-src"
            set "SLN=%%~fR\PCL3-src\Plain Craft Launcher 2.sln"
        )
    )
    if not defined SLN (
        if exist "%%~fR\Plain Craft Launcher 2.sln" (
            set "BASE=%%~fR"
            set "SRC=%%~fR"
            set "SLN=%%~fR\Plain Craft Launcher 2.sln"
        )
    )
)

echo ============================================================
echo  PCL3 build
echo ============================================================
if not defined SLN (
    echo [ERROR] Cannot find the solution file.
    echo         Looked for "Plain Craft Launcher 2.sln" under:
    echo           %~dp0..
    echo           %~dp0
    echo.
    echo         Run this script from the repository root (next to the .sln).
    goto :fail
)
echo  solution : !SLN!
set "VSW=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "MSB="

echo.
echo [1/3] Locating build tools ...
if exist "%VSW%" (
    for /f "usebackq tokens=*" %%i in (`"%VSW%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe 2^>nul`) do set "MSB=%%i"
)

if not defined MSB (
    rem Fallback: default install location of VS 2022 Build Tools
    set "MSB=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    if not exist "!MSB!" set "MSB="
)

if not defined MSB (
    echo [ERROR] Build tools not found.
    echo.
    echo         Install "Visual Studio Build Tools 2022" with the
    echo         ".NET desktop build tools" workload, then run this again.
    echo         Download: https://aka.ms/vs/17/release/vs_buildtools.exe
    echo.
    echo         NOTE: if the installer fails with exit code 5002, it is because
    echo         both HTTPS_PROXY and https_proxy are set in the environment.
    echo         Run the installer with the lowercase variants removed.
    goto :fail
)
echo  compiler : !MSB!

echo.
echo [2/3] Building (Release / Any CPU) ...
echo ------------------------------------------------------------
"!MSB!" "!SLN!" /restore /p:Configuration=Release /p:Platform="Any CPU" /m /v:m /nologo
if errorlevel 1 goto :fail
echo ------------------------------------------------------------

echo.
echo [3/3] Checking output ...
set "EXE=!SRC!\Plain Craft Launcher 2\bin\Plain Craft Launcher 2.exe"
if not exist "!EXE!" (
    echo [ERROR] Build reported success but the exe is missing:
    echo         !EXE!
    goto :fail
)
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

:fail
echo.
echo ------------------------------------------------------------
echo  BUILD FAILED - copy the text above and send it back
echo ------------------------------------------------------------
echo.
pause
exit /b 1
