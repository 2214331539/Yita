param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.8.3',

    [string]$CertificateThumbprint = '',

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$CertificateStoreLocation = 'CurrentUser',

    [string]$TimestampUrl = 'http://timestamp.digicert.com',

    [string]$SignToolPath = ''
)

$ErrorActionPreference = 'Stop'

function Assert-LastExitCode {
    param([string]$Operation)

    if ($LASTEXITCODE -ne 0) {
        throw "$Operation failed with exit code $LASTEXITCODE."
    }
}

function Resolve-SignTool {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolvedPath = (Resolve-Path -LiteralPath $ExplicitPath).Path
        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
            throw "SignTool was not found at '$resolvedPath'."
        }

        return $resolvedPath
    }

    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $sdkBin = Join-Path $programFilesX86 'Windows Kits\10\bin'
        if (Test-Path -LiteralPath $sdkBin -PathType Container) {
            $candidate = Get-ChildItem -LiteralPath $sdkBin -Directory |
                Sort-Object Name -Descending |
                ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
                Select-Object -First 1
            if ($null -ne $candidate) {
                return $candidate
            }
        }
    }

    throw 'SignTool was not found. Install the Windows SDK or pass -SignToolPath.'
}

function Invoke-CodeSigning {
    param(
        [string]$ExecutablePath,
        [string]$Thumbprint,
        [string]$StoreLocation,
        [string]$TimestampServer,
        [string]$ToolPath
    )

    $normalizedThumbprint = $Thumbprint -replace '\s', ''
    if ($normalizedThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'CertificateThumbprint must be a 40-character SHA-1 certificate thumbprint.'
    }

    $certificatePath = "Cert:\$StoreLocation\My\$normalizedThumbprint"
    if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
        throw "The signing certificate was not found at '$certificatePath'."
    }

    $certificate = Get-Item -LiteralPath $certificatePath
    if (-not $certificate.HasPrivateKey) {
        throw 'The signing certificate does not have an accessible private key.'
    }
    if ($certificate.NotAfter -le (Get-Date)) {
        throw 'The signing certificate has expired.'
    }

    $resolvedSignTool = Resolve-SignTool -ExplicitPath $ToolPath
    $signArguments = @(
        'sign',
        '/sha1', $normalizedThumbprint,
        '/s', 'My',
        '/fd', 'SHA256'
    )
    if ($StoreLocation -eq 'LocalMachine') {
        $signArguments += '/sm'
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampServer)) {
        $signArguments += @('/tr', $TimestampServer, '/td', 'SHA256')
    }
    $signArguments += $ExecutablePath

    & $resolvedSignTool @signArguments
    Assert-LastExitCode 'Authenticode signing'
    & $resolvedSignTool verify /pa /all $ExecutablePath
    Assert-LastExitCode 'Authenticode verification'
}

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solutionFile = Join-Path $projectRoot 'Yita.sln'
$projectFile = Join-Path $projectRoot 'src\Yita.App\Yita.App.csproj'
$artifactsRoot = Join-Path $projectRoot 'artifacts\release'
$packageName = "Yita-v$Version-win-x64"
$finalPackageDirectory = Join-Path $artifactsRoot $packageName
$finalZipPath = Join-Path $artifactsRoot "$packageName.zip"
$finalHashPath = "$finalZipPath.sha256"
$stagingRoot = Join-Path $artifactsRoot ('.staging-' + [Guid]::NewGuid().ToString('N'))
$stagingPackageDirectory = Join-Path $stagingRoot $packageName
$stagingZipPath = Join-Path $stagingRoot "$packageName.zip"
$assemblyVersion = "$Version.0"

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

$resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($artifactsRoot)
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
$resolvedFinalPackage = [System.IO.Path]::GetFullPath($finalPackageDirectory)
if (-not $resolvedStagingRoot.StartsWith($resolvedArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase) `
    -or -not $resolvedFinalPackage.StartsWith($resolvedArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release paths resolved outside the release artifacts folder.'
}

try {
    & dotnet restore $solutionFile
    Assert-LastExitCode 'dotnet restore'

    & dotnet restore $projectFile --runtime win-x64
    Assert-LastExitCode 'dotnet restore (win-x64)'

    & dotnet build $solutionFile `
        --configuration Release `
        --no-restore `
        "/p:Version=$Version" `
        "/p:AssemblyVersion=$assemblyVersion" `
        "/p:FileVersion=$assemblyVersion" `
        "/p:InformationalVersion=$Version"
    Assert-LastExitCode 'dotnet build'

    & dotnet test $solutionFile `
        --configuration Release `
        --no-build `
        --no-restore
    Assert-LastExitCode 'dotnet test'

    & dotnet publish $projectFile `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        --output $stagingPackageDirectory `
        "/p:Version=$Version" `
        "/p:AssemblyVersion=$assemblyVersion" `
        "/p:FileVersion=$assemblyVersion" `
        "/p:InformationalVersion=$Version" `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true `
        /p:DebugType=None `
        /p:DebugSymbols=false
    Assert-LastExitCode 'dotnet publish'

    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $stagingPackageDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'QUICK_START.txt') -Destination $stagingPackageDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination $stagingPackageDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $stagingPackageDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'NOTICE.md') -Destination $stagingPackageDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Yita.App\Assets\AppLogo.png') -Destination $stagingPackageDirectory

    # Keep the packaged README's relative preview links usable offline.
    $previewImagesDirectory = Join-Path $stagingPackageDirectory 'docs\images'
    New-Item -ItemType Directory -Force -Path $previewImagesDirectory | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs\images') -Filter '*.png' -File |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $previewImagesDirectory }

    $zoteroIntegrationDirectory = Join-Path $stagingPackageDirectory 'Integrations\Zotero'
    New-Item -ItemType Directory -Force -Path $zoteroIntegrationDirectory | Out-Null
    & (Join-Path $projectRoot 'scripts\Build-ZoteroPlugin.ps1') -OutputDirectory $zoteroIntegrationDirectory
    Assert-LastExitCode 'Zotero plugin packaging'

    $packagedReadme = Join-Path $stagingPackageDirectory 'README.md'
    $readmeText = Get-Content -Raw -Encoding UTF8 -LiteralPath $packagedReadme
    $readmeText = $readmeText.Replace('src/Yita.App/Assets/AppLogo.png', 'AppLogo.png')
    Set-Content -Encoding UTF8 -LiteralPath $packagedReadme -Value $readmeText

    $publishedExecutable = Join-Path $stagingPackageDirectory 'Yita.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable)) {
        throw 'Published executable was not created.'
    }

    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        Invoke-CodeSigning `
            -ExecutablePath $publishedExecutable `
            -Thumbprint $CertificateThumbprint `
            -StoreLocation $CertificateStoreLocation `
            -TimestampServer $TimestampUrl `
            -ToolPath $SignToolPath
        Write-Output "Signature: Authenticode ($CertificateStoreLocation)"
    }
    else {
        Write-Output 'Signature: unsigned (no certificate thumbprint supplied)'
    }

    foreach ($smokeArgument in @('--smoke-test', '--popup-smoke-test', '--settings-lifecycle-test')) {
        $maximumAttempts = if ($smokeArgument -eq '--popup-smoke-test') { 2 } else { 1 }
        $smokeSucceeded = $false
        for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
            $smokeProcess = Start-Process `
                -FilePath $publishedExecutable `
                -ArgumentList $smokeArgument `
                -PassThru `
                -WindowStyle Hidden
            $smokeTimeoutMilliseconds = if ($smokeArgument -eq '--settings-lifecycle-test') { 60000 } else { 15000 }
            if (-not $smokeProcess.WaitForExit($smokeTimeoutMilliseconds)) {
                $smokeProcess.Kill($true)
                throw "Published executable $smokeArgument timed out."
            }
            if ($smokeProcess.ExitCode -eq 0) {
                $smokeSucceeded = $true
                break
            }
            if ($attempt -lt $maximumAttempts) {
                Write-Warning "Published executable $smokeArgument failed once; repeating the visual smoke test."
            }
        }
        if (-not $smokeSucceeded) {
            throw "Published executable $smokeArgument failed after $maximumAttempts attempt(s)."
        }
    }

    Compress-Archive `
        -Path (Join-Path $stagingPackageDirectory '*') `
        -DestinationPath $stagingZipPath `
        -CompressionLevel Optimal

    if (-not (Test-Path -LiteralPath $stagingZipPath)) {
        throw 'Release archive was not created.'
    }

    if (Test-Path -LiteralPath $finalPackageDirectory) {
        Remove-Item -LiteralPath $finalPackageDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $finalZipPath) {
        Remove-Item -LiteralPath $finalZipPath -Force
    }
    if (Test-Path -LiteralPath $finalHashPath) {
        Remove-Item -LiteralPath $finalHashPath -Force
    }

    Move-Item -LiteralPath $stagingPackageDirectory -Destination $finalPackageDirectory
    Move-Item -LiteralPath $stagingZipPath -Destination $finalZipPath

    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $finalZipPath
    "$($hash.Hash.ToLowerInvariant()) *$([System.IO.Path]::GetFileName($finalZipPath))" |
        Set-Content -Encoding ASCII -LiteralPath $finalHashPath

    Write-Output "Package: $finalPackageDirectory"
    Write-Output "Archive: $finalZipPath"
    Write-Output "SHA-256: $finalHashPath"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
