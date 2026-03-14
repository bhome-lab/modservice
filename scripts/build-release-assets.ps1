param(
    [string]$OutputRoot = 'artifacts/release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$PackageVersion = '1.0.0',
    [string]$Channel = 'win'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedOutputRoot = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
} else {
    Join-Path $repoRoot $OutputRoot
}

$workRoot = Join-Path $resolvedOutputRoot 'work'
$assetsRoot = Join-Path $resolvedOutputRoot 'assets'

if (Test-Path $workRoot) {
    Remove-Item $workRoot -Recurse -Force
}

if (Test-Path $assetsRoot) {
    Remove-Item $assetsRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
New-Item -ItemType Directory -Path $assetsRoot -Force | Out-Null

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Description,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action
    )

    Write-Host "==> $Description"
    & $Action
}

function Invoke-DotNetPublish {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,
        [Parameter(Mandatory = $true)]
        [string]$PublishDirectory,
        [Parameter(Mandatory = $true)]
        [bool]$SingleFile
    )

    $publishSingleFile = if ($SingleFile) { 'true' } else { 'false' }
    $compression = if ($SingleFile) { 'true' } else { 'false' }

    dotnet publish $ProjectPath `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=$publishSingleFile `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:EnableCompressionInSingleFile=$compression `
        -o $PublishDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath"
    }
}

function Copy-StreamContent {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Source,
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Destination
    )

    $buffer = New-Object byte[] (1024 * 1024)
    while (($bytesRead = $Source.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $Destination.Write($buffer, 0, $bytesRead)
    }
}

function New-BundledSetupAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LauncherPath,
        [Parameter(Mandatory = $true)]
        [string]$InnerSetupPath,
        [Parameter(Mandatory = $true)]
        [string]$OutputPath
    )

    $magicBytes = [System.Text.Encoding]::ASCII.GetBytes('MSSTP001')
    $payloadLengthBytes = [System.BitConverter]::GetBytes([long](Get-Item $InnerSetupPath).Length)

    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    $outputStream = [System.IO.File]::Create($OutputPath)
    try {
        $launcherStream = [System.IO.File]::OpenRead($LauncherPath)
        try {
            Copy-StreamContent -Source $launcherStream -Destination $outputStream
        }
        finally {
            $launcherStream.Dispose()
        }

        $innerSetupStream = [System.IO.File]::OpenRead($InnerSetupPath)
        try {
            Copy-StreamContent -Source $innerSetupStream -Destination $outputStream
        }
        finally {
            $innerSetupStream.Dispose()
        }

        $outputStream.Write($payloadLengthBytes, 0, $payloadLengthBytes.Length)
        $outputStream.Write($magicBytes, 0, $magicBytes.Length)
    }
    finally {
        $outputStream.Dispose()
    }
}

Invoke-Step -Description 'Restore local tools' -Action {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet tool restore failed.'
    }
}

Invoke-Step -Description 'Build native Debug assets for tests' -Action {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts/build-native.ps1') -Configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw 'Native Debug build failed.'
    }
}

Invoke-Step -Description 'Run automated tests' -Action {
    dotnet test (Join-Path $repoRoot 'ModService.slnx')
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet test failed.'
    }
}

Invoke-Step -Description 'Build native Release assets' -Action {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts/build-native.ps1') -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw 'Native Release build failed.'
    }
}

$standaloneManagedAssets = @(
    @{ Name = 'ModService.Host'; AssetName = 'ModService'; Project = 'src/ModService.Host/ModService.Host.csproj' },
    @{ Name = 'ModService.Tool'; Project = 'src/ModService.Tool/ModService.Tool.csproj' },
    @{ Name = 'ModService.TestTarget'; Project = 'src/ModService.TestTarget/ModService.TestTarget.csproj' }
)

foreach ($managedAsset in $standaloneManagedAssets) {
    $publishDirectory = Join-Path $workRoot ($managedAsset.Name + '.Standalone')
    Invoke-Step -Description "Publish $($managedAsset.Name) as self-contained single-file" -Action {
        Invoke-DotNetPublish -ProjectPath (Join-Path $repoRoot $managedAsset.Project) -PublishDirectory $publishDirectory -SingleFile $true
    }

    $expectedExePath = Join-Path $publishDirectory ($managedAsset.Name + '.exe')
    if (-not (Test-Path $expectedExePath)) {
        throw "Expected single-file executable was not produced: $expectedExePath"
    }

    $assetName = if ($managedAsset.ContainsKey('AssetName')) {
        $managedAsset.AssetName
    } else {
        $managedAsset.Name
    }

    Copy-Item $expectedExePath (Join-Path $assetsRoot ($assetName + '-' + $RuntimeIdentifier + '.exe')) -Force
}

$packagePublishDirectory = Join-Path $workRoot 'ModService.Host.Package'
Invoke-Step -Description 'Publish ModService.Host for Velopack packaging' -Action {
    Invoke-DotNetPublish -ProjectPath (Join-Path $repoRoot 'src/ModService.Host/ModService.Host.csproj') -PublishDirectory $packagePublishDirectory -SingleFile $false
}

$packageOutputDirectory = Join-Path $workRoot 'velopack'
Invoke-Step -Description 'Create Velopack release assets' -Action {
    dotnet vpk pack `
        --packId ModService `
        --packVersion $PackageVersion `
        --packDir $packagePublishDirectory `
        --mainExe ModService.Host.exe `
        --packTitle ModService `
        --packAuthors ModService `
        --outputDir $packageOutputDirectory `
        --runtime $RuntimeIdentifier `
        --channel $Channel

    if ($LASTEXITCODE -ne 0) {
        throw 'Velopack packaging failed.'
    }
}

$setupLauncherDirectory = Join-Path $workRoot 'ModService.SetupLauncher'
Invoke-Step -Description 'Publish ModService.SetupLauncher as self-contained single-file' -Action {
    Invoke-DotNetPublish -ProjectPath (Join-Path $repoRoot 'src/ModService.SetupLauncher/ModService.SetupLauncher.csproj') -PublishDirectory $setupLauncherDirectory -SingleFile $true
}

$setupAssetName = 'ModService-' + $Channel + '-Setup.exe'
$innerSetupPath = Join-Path $packageOutputDirectory $setupAssetName
if (-not (Test-Path $innerSetupPath)) {
    throw "Expected Velopack setup package was not produced: $innerSetupPath"
}

$setupLauncherExePath = Join-Path $setupLauncherDirectory 'ModService.SetupLauncher.exe'
if (-not (Test-Path $setupLauncherExePath)) {
    throw "Expected setup launcher executable was not produced: $setupLauncherExePath"
}

Invoke-Step -Description 'Bundle elevated ModService setup launcher' -Action {
    New-BundledSetupAsset `
        -LauncherPath $setupLauncherExePath `
        -InnerSetupPath $innerSetupPath `
        -OutputPath (Join-Path $assetsRoot $setupAssetName)
}

Get-ChildItem $packageOutputDirectory -File | Where-Object { $_.Name -ne $setupAssetName } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $assetsRoot $_.Name) -Force
}

$nativeAssets = @(
    (Join-Path $repoRoot 'artifacts/native/NativeExecutor/x64/Release/NativeExecutor.dll'),
    (Join-Path $repoRoot 'artifacts/native/SampleModule/x64/Release/SampleModule.dll')
)

foreach ($nativeAssetPath in $nativeAssets) {
    if (-not (Test-Path $nativeAssetPath)) {
        throw "Missing native release asset: $nativeAssetPath"
    }

    Copy-Item $nativeAssetPath $assetsRoot -Force
}

$configPath = Join-Path $repoRoot 'src/ModService.Host/modservice.json'
Copy-Item $configPath (Join-Path $assetsRoot 'modservice.sample.json') -Force

$checksumLines = Get-ChildItem $assetsRoot -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
        "{0} *{1}" -f $hash, $_.Name
    }

Set-Content -Path (Join-Path $assetsRoot 'SHA256SUMS.txt') -Value $checksumLines

Write-Host '==> Release assets'
Get-ChildItem $assetsRoot -File | Sort-Object Name | Select-Object Name, Length, LastWriteTimeUtc
