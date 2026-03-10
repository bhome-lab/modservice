param(
    [string]$ServiceName = 'ModService'
)

if (-not ([bool]([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))) {
    throw 'Administrator privileges are required to uninstall the service.'
}

sc.exe stop $ServiceName | Out-Null
sc.exe delete $ServiceName
if ($LASTEXITCODE -ne 0) {
    throw 'Service deletion failed.'
}
