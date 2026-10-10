[CmdletBinding()]
param(
    [string]$Version = '0.9.0-preview.1',
    [string]$ReleaseDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$ReleaseDirectory) { $ReleaseDirectory = Join-Path $repoRoot 'artifacts/desktop-release' }
$setup = Join-Path $ReleaseDirectory "Yita-Setup-$Version-win-x64.exe"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4F136905-2FF1-40BA-BFDE-D64F5AE0EF35}_is1'
if (Test-Path $uninstallKey) { throw 'Avalonia Yita is already installed. Run on another account or a clean runner.' }
$testRoot = Join-Path $repoRoot ('.work/desktop-install-test-' + [guid]::NewGuid().ToString('N'))
$install = Join-Path $testRoot 'Yita Install Test'
$null = New-Item $testRoot -ItemType Directory
$settings = Join-Path $env:LOCALAPPDATA 'Yita/settings.json'
$original = if (Test-Path $settings) { (Get-FileHash $settings).Hash } else { '' }
function Run-Installer([string]$file, [string[]]$arguments) {
    $process = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(120000)) { throw "Installer timed out; inspect logs in $testRoot" }
    if ($process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
}
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCLOSEAPPLICATIONS', '/NOICONS',
    '/MERGETASKS="!desktopicon"', '/LANG=english', ('/DIR="' + $install + '"'), ('/LOG="' + $testRoot + '\install.log"'))
Run-Installer $setup $arguments
$registration = Get-ItemProperty $uninstallKey
if ($registration.DisplayVersion -ne $Version -or $registration.InstallLocation.TrimEnd('\') -ne $install) {
    throw 'Installer registration mismatch.'
}
$manifest = Get-Content (Join-Path $ReleaseDirectory "Yita-$Version-win-x64.payload.json") -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $file = Join-Path $install $entry.path
    if (!(Test-Path $file) -or (Get-FileHash $file).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Installed file mismatch: $($entry.path)" }
}
& (Join-Path $PSScriptRoot 'Verify-Windows-Package.ps1') -PayloadDirectory $install
$sentinel = Join-Path $install 'user-created-file.txt'
Set-Content $sentinel 'This file must survive reinstall and uninstall.'
Run-Installer $setup $arguments
if (!(Test-Path $sentinel)) { throw 'Reinstall removed a user-created file.' }
Run-Installer (Join-Path $install 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + $testRoot + '\uninstall.log"'))
if ((Test-Path $uninstallKey) -or (Test-Path (Join-Path $install 'Yita.Desktop.exe'))) { throw 'Uninstall did not remove the program/registration.' }
if (!(Test-Path $sentinel)) { throw 'Uninstall removed a user-created file.' }
$after = if (Test-Path $settings) { (Get-FileHash $settings).Hash } else { '' }
if ($after -ne $original) { throw 'Installer changed user settings.' }
Write-Output "Avalonia Setup install, payload, runtime/helper startup, reinstall and uninstall passed. Logs: $testRoot"
