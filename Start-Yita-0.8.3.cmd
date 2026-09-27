@echo off
setlocal
if not defined DOTNET_ROOT if exist "F:\DevTools\dotnet\dotnet.exe" set "DOTNET_ROOT=F:\DevTools\dotnet"
if not exist "%~dp0artifacts\Yita-0.8.3\Yita.exe" (
    echo Build artifacts\Yita-0.8.3 before starting this version.
    pause
    exit /b 1
)
start "" /d "%~dp0artifacts\Yita-0.8.3" "%~dp0artifacts\Yita-0.8.3\Yita.exe"
