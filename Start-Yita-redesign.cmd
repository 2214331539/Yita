@echo off
setlocal
if not defined DOTNET_ROOT if exist "F:\DevTools\dotnet\dotnet.exe" set "DOTNET_ROOT=F:\DevTools\dotnet"
if not exist "%~dp0artifacts\Yita-ui-redesign\Yita.exe" (
    echo Please run scripts\Build-Local.ps1 -OutputDirectory artifacts\Yita-ui-redesign first.
    pause
    exit /b 1
)
start "" /d "%~dp0artifacts\Yita-ui-redesign" "%~dp0artifacts\Yita-ui-redesign\Yita.exe"
