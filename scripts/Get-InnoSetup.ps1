[CmdletBinding()]
param([string]$ToolsDirectory = '')

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($ToolsDirectory)) {
    $ToolsDirectory = Join-Path $projectRoot '.work\tools\innosetup-6.7.3'
}
$compiler = Join-Path $ToolsDirectory 'tools\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    New-Item -ItemType Directory -Force -Path $ToolsDirectory | Out-Null
    $archive = Join-Path $ToolsDirectory 'compiler.zip'
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg' -OutFile $archive
    $expected = 'F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4'
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
        throw 'Inno Setup package checksum mismatch.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $ToolsDirectory -Force
}
foreach ($binary in @('ISCC.exe', 'ISCmplr.dll')) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $ToolsDirectory "tools\$binary")
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Pyrsys B.V.') {
        throw "Inno Setup publisher verification failed for $binary."
    }
}
# Downloads and extracts a portable compiler. No installer execution, PATH or registry changes.
Write-Output $compiler
