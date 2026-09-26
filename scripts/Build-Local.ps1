param(
    [string]$DotnetRoot = '',
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($DotnetRoot)) {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed) {
        $DotnetRoot = Split-Path -Parent $installed.Source
    } elseif ($env:DOTNET_ROOT -and (Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
        $DotnetRoot = $env:DOTNET_ROOT
    } elseif (Test-Path -LiteralPath 'F:\DevTools\dotnet\dotnet.exe') {
        # Compatibility with the original development machine's portable SDK.
        $DotnetRoot = 'F:\DevTools\dotnet'
    } else {
        throw 'Install the .NET 8 SDK or supply -DotnetRoot with its directory.'
    }
}
$dotnetPath = Join-Path $DotnetRoot 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "dotnet.exe was not found in $DotnetRoot."
}
$env:DOTNET_ROOT = $DotnetRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.work\dotnet-user'
# Respect caller-provided NuGet paths; otherwise use the SDK's normal defaults.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$outputDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot 'artifacts\Yita-ui'
} elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
}

Push-Location $projectRoot
try {
    & $dotnetPath publish 'src\Yita.App\Yita.App.csproj' --configuration Release --no-self-contained --output $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Yita publish failed.' }
    foreach ($name in @('LICENSE','NOTICE.md','QUICK_START.txt')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $outputDirectory -Force
    }
    Write-Output "Yita is ready at $outputDirectory. Start-Yita.cmd uses the default artifacts\Yita-ui directory."
} finally { Pop-Location }
