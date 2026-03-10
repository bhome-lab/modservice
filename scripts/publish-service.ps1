param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Output = 'artifacts/publish/ModService',
    [string]$RuntimeIdentifier = 'win-x64'
)

dotnet publish "src/ModService.Host/ModService.Host.csproj" `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    throw "Tray publish failed."
}
