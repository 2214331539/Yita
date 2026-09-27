@echo off
setlocal
powershell.exe -NoProfile -File "%~dp0Collect-Yita-Diagnostics.ps1"
pause
