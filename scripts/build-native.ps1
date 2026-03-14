param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

function Resolve-MSBuildPath {
    $command = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswherePath) {
        $installPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installPath)) {
            $candidate = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    throw 'MSBuild.exe could not be located. Install Visual Studio Build Tools or run microsoft/setup-msbuild before invoking this script.'
}

$msbuild = Resolve-MSBuildPath
$projects = @(
    'native/NativeExecutor/NativeExecutor.vcxproj',
    'native/SampleModule/SampleModule.vcxproj',
    'native/TestApp/TestApp.vcxproj'
)

foreach ($project in $projects) {
    & $msbuild $project /m /nologo /p:Configuration=$Configuration /p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) {
        throw "Native build failed for $project"
    }
}
