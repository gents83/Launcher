@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0output" (
    rmdir /s /q "%~dp0output"
    if exist "%~dp0output" (
        echo ERROR: Could not completely remove the output folder.
        exit /b 1
    )
)

set "VERSION_OVERRIDE="
for %%A in (%*) do if /i "%%~A"=="-Version" set "VERSION_OVERRIDE=1"
if defined VERSION_OVERRIDE goto invoke_release

if not exist "%~dp0version.txt" (
    echo ERROR: version.txt is missing. Create it with one SemVer version, for example 1.0.0.
    exit /b 2
)
set "VERSION_FROM_FILE="
for /f "usebackq delims=" %%V in ("%~dp0version.txt") do if not defined VERSION_FROM_FILE set "VERSION_FROM_FILE=%%V"
if not defined VERSION_FROM_FILE (
    echo ERROR: version.txt is empty. Add one SemVer version, for example 1.0.0.
    exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-release.ps1" -Version "%VERSION_FROM_FILE%" %*
exit /b %ERRORLEVEL%

:invoke_release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-release.ps1" %*
exit /b %ERRORLEVEL%
