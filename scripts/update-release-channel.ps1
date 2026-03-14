param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,
    [Parameter(Mandatory = $true)]
    [string]$SourceTag,
    [Parameter(Mandatory = $true)]
    [string]$Target,
    [string]$ChannelTag = 'latest',
    [string]$Title = '',
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}

if ([string]::IsNullOrWhiteSpace($Title)) {
    $Title = $ChannelTag
}

if ([string]::IsNullOrWhiteSpace($Notes)) {
    $Notes = "Moving release channel for $SourceTag."
}

$downloadRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("modservice-release-channel-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null

try {
    & gh release view $SourceTag --repo $Repo --json tagName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Source release '$SourceTag' was not found in '$Repo'."
    }

    $channelExists = $false
    try {
        & gh release view $ChannelTag --repo $Repo --json tagName 2>$null | Out-Null
        $channelExists = $LASTEXITCODE -eq 0
    }
    catch {
        $channelExists = $false
    }

    if ($channelExists) {
        & gh release delete $ChannelTag --repo $Repo --yes --cleanup-tag
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to delete existing channel release '$ChannelTag'."
        }
    }

    $createArguments = @(
        'release', 'create', $ChannelTag,
        '--repo', $Repo,
        '--target', $Target,
        '--title', $Title,
        '--notes', $Notes,
        '--latest'
    )

    & gh @createArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to create channel release '$ChannelTag'."
    }

    & gh release download $SourceTag --repo $Repo --dir $downloadRoot --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to download assets from source release '$SourceTag'."
    }

    $assetPaths = Get-ChildItem $downloadRoot -File | Sort-Object Name | ForEach-Object { $_.FullName }
    if ($assetPaths.Count -eq 0) {
        throw "Source release '$SourceTag' does not contain any assets."
    }

    & gh release upload $ChannelTag @assetPaths --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to upload assets to channel release '$ChannelTag'."
    }

    & gh release edit $ChannelTag --repo $Repo --draft=false --latest --title $Title --notes $Notes
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish channel release '$ChannelTag'."
    }
}
finally {
    if (Test-Path $downloadRoot) {
        Remove-Item $downloadRoot -Recurse -Force
    }
}
