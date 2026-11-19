#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'SchoolTraining'),
    [switch]$RemoveProgramFiles
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'deployment-common.ps1')
$serviceName = 'SchoolTraining'
$productKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SchoolTraining'
$InstallDirectory = Get-CheckedDirectory $InstallDirectory
$registration = Get-ItemProperty -LiteralPath $productKey -ErrorAction SilentlyContinue
if (-not $registration -or -not $InstallDirectory.Equals($registration.InstallLocation.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Installation directory does not match a registered SchoolTraining product.' }
$dataPath = Get-CheckedDirectory $registration.DataDirectory
if ($dataPath.Equals($InstallDirectory,[StringComparison]::OrdinalIgnoreCase) -or $dataPath.StartsWith($InstallDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Data cannot be inside the program directory.' }
$manifest = Get-VerifiedInstallManifest $InstallDirectory
if (-not $PSCmdlet.ShouldProcess($InstallDirectory,'Uninstall SchoolTraining service; remove only unchanged installed files; preserve data and unknown files')) { return }
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; (Get-Service -Name $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) }
    & "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Failed to remove service registration.' }
}
Get-NetFirewallRule -Name 'SchoolTraining-HTTP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
if ($RemoveProgramFiles) {
    $removed=Remove-InstalledPayload $InstallDirectory
    if($removed.directoryRetained) { Write-Output 'Unknown or modified files remain in the program directory and were preserved.' }
}
Remove-Item -LiteralPath $productKey -Force
Write-Output 'Service removed. Database, encryption keys, configuration, backups and logs were preserved in the data directory.'
