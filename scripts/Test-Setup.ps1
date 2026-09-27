[CmdletBinding()]
param([string]$Version = '0.8.4')

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$setup = Join-Path $projectRoot "artifacts\release\Yita-Setup-$Version-win-x64.exe"
$manifestPath = Join-Path $projectRoot "artifacts\release\Yita-$Version-payload.json"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{2F7C12F3-CF37-4B0B-B76C-73B7443709EE}_is1'
if (Test-Path $uninstallKey) { throw 'Yita is already installed. Run this test on a separate Windows account or clean VM.' }
$testRoot = Join-Path $projectRoot ('.work\install-test-' + [Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testRoot 'Yita Test'
New-Item -ItemType Directory -Path $testRoot | Out-Null
$settings = Join-Path $env:LOCALAPPDATA 'Yita\settings.json'
$originalSettingsHash = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings).Hash } else { '' }

function Invoke-SetupProcess([string]$path, [string[]]$arguments) {
    $process = Start-Process -FilePath $path -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { throw "Setup process still running; inspect log before continuing: $path" }
    if ($process.ExitCode -ne 0) { throw "Setup process failed ($($process.ExitCode)): $path" }
}

$arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS',
    '/NOICONS','/MERGETASKS="!desktopicon"','/LANG=english',('/DIR="' + $installDirectory + '"'),('/LOG="' + (Join-Path $testRoot 'install.log') + '"'))
Invoke-SetupProcess $setup $arguments
if (-not (Test-Path $uninstallKey)) { throw 'Uninstall registration was not created.' }
$registration = Get-ItemProperty $uninstallKey
if ($registration.DisplayVersion -ne $Version -or $registration.InstallLocation.TrimEnd('\') -ne $installDirectory) {
    throw 'Installed product registration does not match the requested version/path.'
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
foreach ($file in $manifest) {
    $installedFile = Join-Path $installDirectory $file.Path
    if (-not (Test-Path -LiteralPath $installedFile) -or (Get-FileHash -LiteralPath $installedFile).Hash -ne $file.SHA256) {
        throw "Installed payload mismatch: $($file.Path)"
    }
}
$sentinel = Join-Path $installDirectory 'user-created-file.txt'
Set-Content -LiteralPath $sentinel -Value 'User-created content must survive reinstall and uninstall.'
Invoke-SetupProcess $setup $arguments
if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Reinstall removed a user-created file.' }
$uninstaller = Join-Path $installDirectory 'unins000.exe'
Invoke-SetupProcess $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"'))
if (Test-Path $uninstallKey) { throw 'Uninstall registration remained after uninstall.' }
if (Test-Path -LiteralPath (Join-Path $installDirectory 'Yita.exe')) { throw 'Uninstall left the application executable behind.' }
if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed a user-created file.' }
$settingsHash = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings).Hash } else { '' }
if ($settingsHash -ne $originalSettingsHash) { throw 'Installer changed existing user settings.' }
Write-Output "Install, payload hashes, reinstall and uninstall passed. User settings preserved. Logs: $testRoot"
