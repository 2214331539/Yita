[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+-preview\.[1-9]\d*$')][string]$Version = '0.9.0-preview.1',
    [string]$DotnetRoot = '',
    [string]$InnoCompiler = '',
    [string]$OutputDirectory = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = if ($DotnetRoot) { Join-Path $DotnetRoot 'dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
if (!$InnoCompiler) { $InnoCompiler = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts/desktop-release' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item $output -ItemType Directory -Force
$work = Join-Path $repoRoot ('.work/desktop-setup-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'Yita'
$worker = Join-Path $payload 'Native/WindowsUIA'
$artwork = Join-Path $work 'artwork'
$null = New-Item $payload, $worker -ItemType Directory -Force
$baseVersion, $previewNumber = $Version -split '-preview\.'
$numericVersion = "$baseVersion.$previewNumber"
$setup = Join-Path $output "Yita-Setup-$Version-win-x64.exe"
if (Test-Path $setup) { throw 'Refusing to overwrite an existing Setup. Choose another output directory.' }
function Assert-Success([string]$operation) {
    if ($LASTEXITCODE -ne 0) { throw "$operation failed with exit code $LASTEXITCODE." }
}
Push-Location $repoRoot
try {
    if (!$SkipTests) {
        & $dotnet test Yita.CrossPlatform.sln -c Release --logger 'console;verbosity=minimal'
        Assert-Success 'Cross-platform tests'
    }
    $properties = @('-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false',
        "-p:Version=$Version", "-p:AssemblyVersion=$baseVersion.0", "-p:FileVersion=$numericVersion")
    & $dotnet publish src/Yita.Desktop/Yita.Desktop.csproj -c Release -r win-x64 --self-contained true -o $payload @properties
    Assert-Success 'Desktop publish'
    & $dotnet publish src/Yita.Native.Windows.UIA.Worker/Yita.Native.Windows.UIA.Worker.csproj `
        -c Release -r win-x64 --self-contained true -o $worker @properties
    Assert-Success 'UIA worker publish'
    & (Join-Path $PSScriptRoot 'Collect-DesktopLicenses.ps1') -PublishDirectory $payload `
        -OutputDirectory (Join-Path $payload 'Licenses') -RuntimeIdentifier win-x64
    & (Join-Path $PSScriptRoot 'Collect-DesktopLicenses.ps1') -PublishDirectory $worker `
        -OutputDirectory (Join-Path $payload 'Licenses/WindowsUIA') -RuntimeIdentifier win-x64 `
        -AssetsFile (Join-Path $repoRoot 'src/Yita.Native.Windows.UIA.Worker/obj/project.assets.json') -DependencyFileName 'Yita.UIA.Worker.deps.json'
    Copy-Item (Join-Path (Split-Path $InnoCompiler -Parent) 'License.txt') (Join-Path $payload 'Licenses/LICENSE-InnoSetup.txt')
    Copy-Item (Join-Path $repoRoot 'docs/WINDOWS_PREVIEW_TESTING.md') (Join-Path $payload 'WINDOWS_TESTING.md')
    $runtime = Get-Content (Join-Path $payload 'Yita.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
    $manifest = [ordered]@{
        version = $Version; commit = (& git rev-parse HEAD); worktreeDirty = [bool](& git status --porcelain)
        rid = 'win-x64'; minimumWindows = '10.0.17763'; sdk = (& $dotnet --version)
        runtime = $runtime.runtimeOptions.includedFrameworks; signing = 'unsigned; no Authenticode certificate'
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $payload 'build-manifest.json') -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'Verify-Windows-Package.ps1') -PayloadDirectory $payload
    & (Join-Path $PSScriptRoot 'New-InstallerArtwork.ps1') -OutputDirectory $artwork
    & $InnoCompiler '/Qp' "/DAppVersion=$Version" "/DAppNumericVersion=$numericVersion" "/DPayloadDir=$payload" `
        "/DArtworkDir=$artwork" "/DOutputDir=$output" (Join-Path $repoRoot 'packaging/windows/Yita.Desktop.iss')
    Assert-Success 'Avalonia Setup compilation'
    if (!(Test-Path $setup)) { throw 'Setup output missing.' }
    $zip = Join-Path $output "Yita-$Version-win-x64.zip"
    Compress-Archive -LiteralPath $payload -DestinationPath $zip
    Copy-Item (Join-Path $payload 'build-manifest.json') (Join-Path $output "Yita-$Version-win-x64.build.json")
    Copy-Item (Join-Path $payload 'WINDOWS_TESTING.md') $output
    $files = Get-ChildItem $payload -Recurse -File | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($payload, $_.FullName); sha256 = (Get-FileHash $_.FullName).Hash.ToLowerInvariant() }
    }
    $files | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $output "Yita-$Version-win-x64.payload.json") -Encoding utf8NoBOM
    Get-ChildItem $output -File | Where-Object { $_.Name -ne 'SHA256SUMS-win-x64.txt' } | Sort-Object Name | ForEach-Object {
        (Get-FileHash $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name
    } | Set-Content (Join-Path $output 'SHA256SUMS-win-x64.txt') -Encoding ascii
    [pscustomobject]@{ setup = $setup; payload = $payload; version = $Version }
} finally { Pop-Location }
