param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Output = 'artifacts/publish/ModService.Host'
)

dotnet publish "src/ModService.Host/ModService.Host.csproj" -c $Configuration -o $Output
if ($LASTEXITCODE -ne 0) {
    throw "Service publish failed."
}
