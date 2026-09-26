@echo off
setlocal
if not defined DOTNET_ROOT if exist "F:\DevTools\dotnet\dotnet.exe" set "DOTNET_ROOT=F:\DevTools\dotnet"
if not exist "%~dp0artifacts\Yita-ui\Yita.exe" (
    echo Please run scripts\Build-Local.ps1 first.
    pause
    exit /b 1
)
start "" /d "%~dp0artifacts\Yita-ui" "%~dp0artifacts\Yita-ui\Yita.exe"
