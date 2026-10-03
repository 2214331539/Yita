[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('osx-arm64', 'win-x64')][string]$RuntimeIdentifier = 'osx-arm64',
    [string]$AssetsFile = '',
    [string]$DependencyFileName = 'Yita.Desktop.deps.json'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (!$AssetsFile) { $AssetsFile = Join-Path $repoRoot 'src/Yita.Desktop/obj/project.assets.json' }
$assets = Get-Content $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$deps = Get-Content (Join-Path $PublishDirectory $DependencyFileName) -Raw | ConvertFrom-Json -AsHashtable
$null = New-Item $OutputDirectory -ItemType Directory -Force
Copy-Item (Join-Path $repoRoot 'LICENSE') $OutputDirectory
Copy-Item (Join-Path $repoRoot 'NOTICE.md') $OutputDirectory
Copy-Item (Join-Path $repoRoot 'LICENSES') $OutputDirectory -Recurse -Force
Copy-Item (Join-Path $repoRoot 'src/Yita.App/Assets/Fonts/LICENSE-SourceSans.md') $OutputDirectory

# Keep the full restored dependency set, including build-only and other-platform notices.
$packages = @{}
foreach ($entry in $assets.libraries.GetEnumerator()) {
    if ($entry.Value.type -eq 'package') { $packages[$entry.Key] = $entry.Value.path }
}
$runtimeEntries = @($deps.libraries.Keys | Where-Object {
    $_ -match ('^(runtimepack\.)?Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.' + [regex]::Escape($RuntimeIdentifier) + '/')
})
if ($runtimeEntries.Count -lt 1) { throw 'No matching self-contained runtime pack in the published dependency manifest.' }
foreach ($key in $runtimeEntries) { $packages[$key] = ($key -replace '^runtimepack\.', '').ToLowerInvariant() }
$inventory = @()
foreach ($key in ($packages.Keys | Sort-Object)) {
    $packageRoot = $null
    foreach ($folder in $assets.packageFolders.Keys) {
        $candidate = Join-Path $folder $packages[$key]
        if (Test-Path $candidate -PathType Container) { $packageRoot = $candidate; break }
    }
    if (!$packageRoot) { throw "Restored package is missing: $key" }
    $destination = Join-Path $OutputDirectory ('Packages/' + $key)
    $null = New-Item $destination -ItemType Directory -Force
    $nuspec = @(Get-ChildItem $packageRoot -Filter '*.nuspec' -File)
    if ($nuspec.Count -ne 1) { throw "Package metadata is missing: $key" }
    Copy-Item $nuspec[0].FullName $destination
    [xml]$metadata = Get-Content $nuspec[0].FullName -Raw
    $files = @(Get-ChildItem $packageRoot -Recurse -File | Where-Object {
        $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD.PARTY.NOTICES)([.-]|$)' -and $_.Length -gt 0
    })
    $licensePaths = @()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($packageRoot, $file.FullName)
        $target = Join-Path $destination $relative
        $null = New-Item (Split-Path $target -Parent) -ItemType Directory -Force
        Copy-Item $file.FullName $target
        $licensePaths += $relative
    }
    if ($files.Count -eq 0) {
        $fallback = switch -Wildcard ($key) {
            'Avalonia*/*' { 'Avalonia-MIT.txt'; break }
            'MicroCom.Runtime/*' { 'MicroCom-MIT.txt'; break }
            'Tmds.DBus.Protocol/*' { 'Tmds-DBus-MIT.txt'; break }
            default { throw "No complete license text is available for $key" }
        }
        Copy-Item (Join-Path $repoRoot ('LICENSES/' + $fallback)) $destination
        $licensePaths += $fallback
    }
    $inventory += [ordered]@{
        package = $key
        license = [string]$metadata.package.metadata.license.InnerText
        copyright = [string]$metadata.package.metadata.copyright
        source = [string]$metadata.package.metadata.repository.url
        notices = $licensePaths
    }
}
$inventory | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'dependency-inventory.json') -Encoding utf8NoBOM
Write-Output "Collected complete notices for $($inventory.Count) restored packages, including $($runtimeEntries -join ', ')."
