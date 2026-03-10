param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

$msbuild = (Get-Command MSBuild.exe -ErrorAction Stop).Source
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
