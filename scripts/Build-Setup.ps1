[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.8.3',
    [string]$DotnetRoot = '',
    [string]$InnoCompiler = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($DotnetRoot) { $dotnet = Join-Path $DotnetRoot 'dotnet.exe' }
elseif (Get-Command dotnet -ErrorAction SilentlyContinue) { $dotnet = (Get-Command dotnet).Source }
elseif ($env:DOTNET_ROOT) { $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
else { throw 'Install .NET 8 SDK or pass -DotnetRoot.' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw "dotnet not found: $dotnet" }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
if (-not $InnoCompiler) { $InnoCompiler = & (Join-Path $PSScriptRoot 'Get-InnoSetup.ps1') }
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Inno Setup compiler not found.' }

$release = Join-Path $projectRoot 'artifacts\release'
$buildRoot = Join-Path $projectRoot ('.work\setup-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $buildRoot 'payload'
$artwork = Join-Path $buildRoot 'artwork'
New-Item -ItemType Directory -Force -Path $release, $payload | Out-Null
function Assert-Success([string]$operation) {
    if ($LASTEXITCODE -ne 0) { throw "$operation failed with exit code $LASTEXITCODE." }
}
Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        & $dotnet test Yita.sln --configuration Release --logger 'console;verbosity=minimal'
        Assert-Success 'Tests'
    }
    & $dotnet publish src\Yita.App\Yita.App.csproj --configuration Release --runtime win-x64 `
        --self-contained true --output $payload /p:PublishSingleFile=false /p:PublishTrimmed=false `
        /p:DebugType=None /p:DebugSymbols=false "/p:Version=$Version" "/p:AssemblyVersion=$Version.0" `
        "/p:FileVersion=$Version.0" "/p:InformationalVersion=$Version"
    Assert-Success 'Self-contained publish'
    foreach ($name in @('Yita.exe','Yita.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll',
        'LICENSE','LICENSES\InstantTranslate-MIT.txt','NOTICE.md','Assets\Fonts\LICENSE-SourceSans.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $name))) { throw "Missing payload file: $name" }
    }
    $runtime = Get-Content -Raw (Join-Path $payload 'Yita.runtimeconfig.json') | ConvertFrom-Json
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) {
        throw 'The payload depends on a globally installed runtime.'
    }
    $assets = Get-Content -Raw 'src\Yita.App\obj\project.assets.json' | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $packageId = ($framework.name + '.Runtime.win-x64').ToLowerInvariant()
        $packageFolder = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
            Join-Path $_ ($packageId + '\' + $framework.version)
        } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $packageFolder) { throw "Runtime package not found: $packageId" }
        $license = Get-ChildItem -LiteralPath $packageFolder -File | Where-Object { $_.Name -match '^LICENSE(\.TXT)?$' } | Select-Object -First 1
        if (-not $license) { throw "Runtime license missing: $packageId" }
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $payload ("LICENSE-" + $framework.name + '.txt'))
        $notices = Join-Path $packageFolder 'THIRD-PARTY-NOTICES.TXT'
        if (Test-Path -LiteralPath $notices) {
            Copy-Item -LiteralPath $notices -Destination (Join-Path $payload ("THIRD-PARTY-NOTICES-" + $framework.name + '.txt'))
        }
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'QUICK_START.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $InnoCompiler) 'License.txt') `
        -Destination (Join-Path $payload 'LICENSE-InnoSetup.txt')
    & (Join-Path $PSScriptRoot 'New-InstallerArtwork.ps1') -OutputDirectory $artwork
    & $InnoCompiler '/Qp' "/DAppVersion=$Version" "/DPayloadDir=$payload" "/DArtworkDir=$artwork" `
        "/DOutputDir=$release" (Join-Path $projectRoot 'packaging\windows\Yita.iss')
    Assert-Success 'Setup compilation'
    $setup = Join-Path $release "Yita-Setup-$Version-win-x64.exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw 'Setup output missing.' }
    $hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath "$setup.sha256" -Encoding ASCII
    $manifest = Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object {
        [PSCustomObject]@{
            Path = $_.FullName.Substring($payload.Length + 1)
            SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release "Yita-$Version-payload.json") -Encoding UTF8
    [PSCustomObject]@{ Setup = $setup; SHA256 = $hash; SizeMiB = [Math]::Round((Get-Item $setup).Length / 1MB, 1); Payload = $payload }
} finally { Pop-Location }
