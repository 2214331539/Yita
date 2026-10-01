@echo off
setlocal
if not defined DOTNET_ROOT if exist "F:\DevTools\dotnet\dotnet.exe" set "DOTNET_ROOT=F:\DevTools\dotnet"
set "YITA_DESKTOP_DIR=%~dp0src\Yita.Desktop\bin\Release\net8.0"
if not exist "%YITA_DESKTOP_DIR%\Yita.Desktop.exe" (
    echo Build the preview first: dotnet build Yita.CrossPlatform.sln -c Release
    pause
    exit /b 1
)
start "" /d "%YITA_DESKTOP_DIR%" "%YITA_DESKTOP_DIR%\Yita.Desktop.exe"
