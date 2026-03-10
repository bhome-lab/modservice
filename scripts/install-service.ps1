param(
    [string]$ServiceName = 'ModService',
    [string]$BinaryPath = 'artifacts/publish/ModService.Host/ModService.Host.exe'
)

if (-not ([bool]([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
    throw 'Administrator privileges are required to install the service.'
}

$resolvedBinary = Resolve-Path $BinaryPath
sc.exe create $ServiceName binPath= "`"$resolvedBinary`"" start= auto
if ($LASTEXITCODE -ne 0) {
    throw 'Service creation failed.'
}
