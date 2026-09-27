[CmdletBinding()]
param([string]$OutputDirectory = '')

# Read-only collection. Never reads Credential Manager, API keys, document
# content or full settings. Does not change firewall, registry or app state.
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [Environment]::GetFolderPath('Desktop')
}
$name = 'Yita-Diagnostics-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$work = Join-Path ([IO.Path]::GetTempPath()) $name
New-Item -ItemType Directory -Force -Path $OutputDirectory, $work | Out-Null
$report = [ordered]@{
    CreatedUtc = [DateTime]::UtcNow.ToString('o')
    WindowsVersion = [Environment]::OSVersion.Version.ToString()
    Is64BitOS = [Environment]::Is64BitOperatingSystem
    InstalledVersion = $null
    InstalledExeVersion = $null
    InstalledExeSHA256 = $null
    HealthLogPresent = $false
    Events = @()
    CollectionNotes = @()
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{2F7C12F3-CF37-4B0B-B76C-73B7443709EE}_is1'
try {
    if (Test-Path $uninstallKey) {
        $registration = Get-ItemProperty $uninstallKey
        $report.InstalledVersion = $registration.DisplayVersion
        $exe = Join-Path $registration.InstallLocation 'Yita.exe'
        if (Test-Path -LiteralPath $exe) {
            $report.InstalledExeVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion.Trim()
            $report.InstalledExeSHA256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        }
    }
} catch { $report.CollectionNotes += 'Could not read installation metadata.' }

$dataDirectory = Join-Path $env:LOCALAPPDATA 'Yita'
foreach ($fileName in @('runtime-health.log', 'runtime-health.log.previous')) {
    $path = Join-Path $dataDirectory $fileName
    if (Test-Path -LiteralPath $path) {
        try {
            # Reconstruct only known safe fields, even if a log was modified.
            $safeLines = @(Get-Content -LiteralPath $path -Tail 150 | ForEach-Object {
                $line = $_
                if ($line -match '^\d{4}-\d{2}-\d{2}T[0-9:.]+[+Z0-9:-]* ') {
                    $time = $line.Split(' ')[0]
                    $fields = [regex]::Matches($line,
                        '(?:^| )(?:(?:event|exception|inner|version|at)=[A-Za-z0-9_.+<>`:-]{1,240}|(?:pid|session|code|hresult)=-?[0-9]+)(?= |$)') |
                        ForEach-Object { $_.Value.Trim() }
                    if ($fields.Count -gt 0) { $time + ' ' + ($fields -join ' ') }
                }
            })
            $safeLines | Set-Content -LiteralPath (Join-Path $work $fileName) -Encoding UTF8
            $report.HealthLogPresent = $true
        } catch { $report.CollectionNotes += 'Could not read a health log.' }
    }
}

try {
    $events = @(Get-WinEvent -FilterHashtable @{
        LogName='Application'; Id=1000,1026; StartTime=(Get-Date).AddDays(-3)
    } -MaxEvents 1000 -ErrorAction Stop)
    foreach ($event in $events) {
        $xml = [xml]$event.ToXml()
        $data = @($xml.Event.EventData.Data)
        if ((($data | ForEach-Object { $_.InnerText }) -join "`n") -notmatch '(?i)\bYita\.(exe|dll)\b') { continue }
        $entry = [ordered]@{ TimeUtc=$event.TimeCreated.ToUniversalTime().ToString('o'); Id=$event.Id }
        if ($event.Id -eq 1000) {
            foreach ($field in @('AppName','AppVersion','ModuleName','ModuleVersion','ExceptionCode','FaultingOffset')) {
                $value = ($data | Where-Object { $_.Name -eq $field } | Select-Object -First 1).InnerText
                if ($value -match '^[A-Za-z0-9_.-]{1,160}$') { $entry[$field] = $value }
            }
        } else {
            # Do not export exception messages, arguments or filesystem paths.
            $body = ($data | ForEach-Object { $_.InnerText }) -join "`n"
            $entry.ExceptionTypes = @([regex]::Matches($body,
                '\b(?:System|Microsoft|Yita)\.[A-Za-z0-9_.+`]{1,160}Exception\b') |
                ForEach-Object { $_.Value } | Select-Object -Unique -First 12)
            $entry.Methods = @([regex]::Matches($body,
                '(?m)^\s*(?:at|在)\s+((?:Yita|System|Microsoft|MS\.Internal)\.[A-Za-z0-9_.+<>`]{1,200})\(') |
                ForEach-Object { $_.Groups[1].Value } | Select-Object -First 24)
        }
        $report.Events += [PSCustomObject]$entry
        if ($report.Events.Count -ge 20) { break }
    }
} catch {
    $report.CollectionNotes += 'Event log query returned no matching events or was unavailable; this does not rule out a crash.'
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $work 'report.json') -Encoding UTF8
$archive = Join-Path $OutputDirectory ($name + '.zip')
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $archive
Write-Output "Diagnostic archive: $archive"
Write-Output 'Review the archive before sharing. No upload was performed.'
