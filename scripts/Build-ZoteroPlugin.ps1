[CmdletBinding()]
param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceDirectory = Join-Path $projectRoot 'integrations\zotero\yita-selection'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot 'artifacts\integrations'
}

$resolvedSource = (Resolve-Path -LiteralPath $sourceDirectory).Path
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

foreach ($requiredFile in @('manifest.json', 'bootstrap.js', 'README.md')) {
    $requiredPath = Join-Path $resolvedSource $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Missing Zotero plugin file: $requiredFile"
    }
}

$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $resolvedSource 'manifest.json') |
    ConvertFrom-Json
if ($manifest.applications.zotero.id -ne 'yita-selection@yita.local') {
    throw 'Unexpected Zotero plugin id.'
}

$temporaryZip = Join-Path $resolvedOutput ('.zotero-' + [Guid]::NewGuid().ToString('N') + '.zip')
$xpiPath = Join-Path $resolvedOutput 'Yita-Zotero-Selection.xpi'
try {
    $packagePaths = @(
        (Join-Path $resolvedSource '*'),
        (Join-Path $projectRoot 'LICENSE'),
        (Join-Path $projectRoot 'NOTICE.md')
    )
    Compress-Archive -Path $packagePaths -DestinationPath $temporaryZip -CompressionLevel Optimal
    Move-Item -LiteralPath $temporaryZip -Destination $xpiPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryZip) {
        Remove-Item -LiteralPath $temporaryZip -Force
    }
}

Write-Output $xpiPath
