param(
    [string]$OutputRoot = 'artifacts/release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$InstallerVersion = '1.0.0'
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
        [string]$PublishDirectory
    )

    dotnet publish $ProjectPath `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:EnableCompressionInSingleFile=true `
        -o $PublishDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath"
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

$managedAssets = @(
    @{ Name = 'ModService.Host'; AssetName = 'ModService'; Project = 'src/ModService.Host/ModService.Host.csproj' },
    @{ Name = 'ModService.Tool'; Project = 'src/ModService.Tool/ModService.Tool.csproj' },
    @{ Name = 'ModService.TestTarget'; Project = 'src/ModService.TestTarget/ModService.TestTarget.csproj' }
)

foreach ($managedAsset in $managedAssets) {
    $publishDirectory = Join-Path $workRoot $managedAsset.Name
    Invoke-Step -Description "Publish $($managedAsset.Name) as framework-dependent single-file" -Action {
        Invoke-DotNetPublish -ProjectPath (Join-Path $repoRoot $managedAsset.Project) -PublishDirectory $publishDirectory
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

$installerOutputDirectory = Join-Path $workRoot 'installer'
$hostPublishDirectory = Join-Path $workRoot 'ModService.Host'
$configSourcePath = Join-Path $repoRoot 'src/ModService.Host/modservice.json'
Invoke-Step -Description 'Build MSI installer' -Action {
    dotnet build (Join-Path $repoRoot 'installer/ModService.Setup/ModService.Setup.wixproj') `
        -c Release `
        -o $installerOutputDirectory `
        -p:PublishDir=$hostPublishDirectory `
        -p:ConfigSource=$configSourcePath `
        -p:ProductVersion=$InstallerVersion `
        -p:OutputName='ModService-win-x64'

    if ($LASTEXITCODE -ne 0) {
        throw 'MSI build failed.'
    }
}

$installerPath = Join-Path $installerOutputDirectory 'ModService-win-x64.msi'
if (-not (Test-Path $installerPath)) {
    throw "Expected MSI was not produced: $installerPath"
}

Copy-Item $installerPath (Join-Path $assetsRoot 'ModService-win-x64.msi') -Force

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

Copy-Item (Join-Path $repoRoot 'src/ModService.Host/modservice.json') (Join-Path $assetsRoot 'modservice.sample.json') -Force

$checksumLines = Get-ChildItem $assetsRoot -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
        "{0} *{1}" -f $hash, $_.Name
    }

Set-Content -Path (Join-Path $assetsRoot 'SHA256SUMS.txt') -Value $checksumLines

Write-Host '==> Release assets'
Get-ChildItem $assetsRoot -File | Sort-Object Name | Select-Object Name, Length, LastWriteTimeUtc
