[CmdletBinding()]
param([Parameter(Mandatory)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$workerRoot = Join-Path $payload 'Native/WindowsUIA'
foreach ($root in @($payload, $workerRoot)) {
    foreach ($name in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll')) {
        if (!(Test-Path (Join-Path $root $name) -PathType Leaf)) { throw "Missing self-contained runtime: $root/$name" }
    }
}
foreach ($entry in @('Licenses/LICENSE', 'Licenses/NOTICE.md', 'Licenses/LICENSES/InstantTranslate-MIT.txt',
    'Licenses/LICENSE-SourceSans.md', 'Licenses/dependency-inventory.json', 'Licenses/WindowsUIA/dependency-inventory.json',
    'Native/WindowsUIA/PresentationFramework.dll', 'Assets/Yita.ico')) {
    if (!(Test-Path (Join-Path $payload $entry) -PathType Leaf)) { throw "Missing payload file: $entry" }
}
foreach ($relative in @('Yita.Desktop.runtimeconfig.json', 'Native/WindowsUIA/Yita.UIA.Worker.runtimeconfig.json')) {
    $runtime = Get-Content (Join-Path $payload $relative) -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or !$runtime.runtimeOptions.includedFrameworks) {
        throw "Runtime is not self-contained: $relative"
    }
}
foreach ($relative in @('Yita.Desktop.exe', 'coreclr.dll', 'Native/WindowsUIA/Yita.UIA.Worker.exe', 'Native/WindowsUIA/coreclr.dll')) {
    $stream = [IO.File]::OpenRead((Join-Path $payload $relative))
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c
        $header = $reader.ReadInt32()
        $stream.Position = $header
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw "Expected x64 PE binary: $relative" }
    } finally { $reader.Dispose(); $stream.Dispose() }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('yita-package-check-' + [guid]::NewGuid().ToString('N'))
$null = New-Item $temporary -ItemType Directory
$variables = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP', 'YITA_UIA_WORKER_PATH')
$previous = @{}
foreach ($variable in $variables) { $previous[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
try {
    $env:DOTNET_ROOT = Join-Path $temporary 'missing-runtime'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    Remove-Item Env:YITA_UIA_WORKER_PATH -ErrorAction SilentlyContinue
    $stdout = Join-Path $temporary 'stdout.json'
    $stderr = Join-Path $temporary 'stderr.txt'
    $process = Start-Process -FilePath (Join-Path $payload 'Yita.Desktop.exe') -ArgumentList '--package-check' `
        -WorkingDirectory $payload -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (!$process.WaitForExit(30000)) { $process.Kill($true); throw 'Package check timed out.' }
    if ($process.ExitCode -ne 0) { throw ('Package check failed: ' + (Get-Content $stderr -Raw)) }
    $result = Get-Content $stdout -Raw | ConvertFrom-Json
    if ($result.status -ne 'passed' -or $result.architecture -ne 'x64') { throw 'Invalid package check result.' }
    $result | ConvertTo-Json -Compress
} finally {
    foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, $previous[$variable], 'Process') }
}
Write-Output 'Windows package verified. No product settings, clipboard, selection, startup or API changes.'
