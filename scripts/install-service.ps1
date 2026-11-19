#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$SourceDirectory = (Join-Path $PSScriptRoot '..\app'),
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'SchoolTraining'),
    [string]$DataDirectory = (Join-Path $env:ProgramData 'SchoolTraining'),
    [string]$ListenAddress = '127.0.0.1',
    [ValidateRange(1024,65535)][int]$Port = 5080,
    [switch]$AllowLan,
    [string]$ServerConfigurationFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'deployment-common.ps1')
$serviceName = 'SchoolTraining'
$firewallName = 'SchoolTraining-HTTP'
$productKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SchoolTraining'

function Invoke-ServiceControl([string[]]$Arguments) {
    $result = & "$env:SystemRoot\System32\sc.exe" @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Service operation failed: $result" }
}
function Set-ServiceBinary([string]$ImagePath,[bool]$Create) {
    if ($Create) {
        $result = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{ Name=$serviceName; DisplayName='School Training Platform'; PathName=$ImagePath; ServiceType=[byte]16; ErrorControl=[byte]1; StartMode='Automatic'; StartName='NT AUTHORITY\LocalService' }
    } else {
        $serviceObject = Get-CimInstance -ClassName Win32_Service -Filter "Name='SchoolTraining'"
        $result = Invoke-CimMethod -InputObject $serviceObject -MethodName Change -Arguments @{ PathName=$ImagePath; StartMode='Automatic'; StartName='NT AUTHORITY\LocalService' }
    }
    if ($result.ReturnValue -ne 0) { throw "Service binary configuration failed: Win32 code $($result.ReturnValue)" }
}
$SourceDirectory = Get-CheckedDirectory $SourceDirectory
$InstallDirectory = Get-CheckedDirectory $InstallDirectory
$DataDirectory = Get-CheckedDirectory $DataDirectory
Assert-ProtectedParentChain $InstallDirectory
Assert-ProtectedParentChain $DataDirectory
$priorManifest = Get-VerifiedInstallManifest $InstallDirectory -AllowEmpty
Assert-OwnedDataDirectory $DataDirectory -AllowEmpty
if ($DataDirectory.StartsWith($InstallDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or $InstallDirectory.StartsWith($DataDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or $DataDirectory.Equals($InstallDirectory, [StringComparison]::OrdinalIgnoreCase)) { throw 'Program and data directories must be separate and cannot contain one another.' }
$sourceExe = Join-Path $SourceDirectory 'School.Training.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) { throw "Published program missing: $sourceExe" }
$sourceFiles = @(Get-SafeFiles $SourceDirectory)
$plannedFiles = New-Object Collections.Generic.List[object]
foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($SourceDirectory.TrimEnd('\').Length + 1)
    $null = Get-SafeProductChild $InstallDirectory $relative
    $plannedFiles.Add([ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
}
foreach ($name in @('deployment-common.ps1','install-service.ps1','uninstall-service.ps1','restore-backup.ps1')) {
    $scriptSource = Get-SafeProductChild $PSScriptRoot $name
    $null = Get-SafeProductChild $InstallDirectory ('scripts\' + $name)
    $plannedFiles.Add([ordered]@{path=('scripts\' + $name);sha256=(Get-FileHash -LiteralPath $scriptSource -Algorithm SHA256).Hash.ToLowerInvariant()})
}
Assert-NoPayloadCollisions $InstallDirectory $plannedFiles $priorManifest
$priorInstallationFile = Join-Path $DataDirectory 'installation.json'
if (Test-Path -LiteralPath $priorInstallationFile) {
    $prior = Get-Content -LiteralPath $priorInstallationFile -Raw | ConvertFrom-Json
    if ($prior.product -eq 'SchoolTraining') {
        if (-not $PSBoundParameters.ContainsKey('ListenAddress')) { $ListenAddress = $prior.listenAddress }
        if (-not $PSBoundParameters.ContainsKey('Port')) { $Port = [int]$prior.port }
        if (-not $PSBoundParameters.ContainsKey('AllowLan') -and $prior.PSObject.Properties['allowLan']) { $AllowLan = [bool]$prior.allowLan }
    }
}
$bindIp = $null
if (-not [Net.IPAddress]::TryParse($ListenAddress, [ref]$bindIp) -or $bindIp.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'ListenAddress must be an IPv4 address.' }
if (-not [Net.IPAddress]::IsLoopback($bindIp) -and -not $AllowLan) { throw 'Non-loopback binding requires -AllowLan.' }
$scheme = 'http'
$serverConfig = $null
$certificateSource = $null
$serverConfigPath = Join-Path $DataDirectory 'server.json'
$configurationSource = $serverConfigPath
if ($ServerConfigurationFile) { $configurationSource = [IO.Path]::GetFullPath($ServerConfigurationFile) }
if ($AllowLan) {
    if (-not (Test-Path -LiteralPath $configurationSource -PathType Leaf)) { throw 'LAN access requires a server.json with an HTTPS endpoint and certificate. Supply -ServerConfigurationFile.' }
    $serverConfig = Get-Content -LiteralPath $configurationSource -Raw | ConvertFrom-Json
    if(-not $serverConfig.PSObject.Properties['AllowedHosts'] -or $serverConfig.AllowedHosts -isnot [string]) { throw 'LAN server.json requires an explicit AllowedHosts string matching the school certificate hostname.' }
    $endpointProperties = @($serverConfig.Kestrel.Endpoints.PSObject.Properties)
    if ($endpointProperties.Count -ne 1) { throw 'LAN setup requires exactly one HTTPS endpoint in server.json.' }
    $endpoint = $endpointProperties[0].Value
    $uri = [Uri]$endpoint.Url
    if ($uri.Scheme -ne 'https' -or $uri.Port -ne $Port -or $uri.Host -ne $ListenAddress) { throw 'The HTTPS endpoint address and port must match ListenAddress and Port.' }
    $certificateSource = [IO.Path]::GetFullPath($endpoint.Certificate.Path)
    if (-not (Test-Path -LiteralPath $certificateSource -PathType Leaf)) { throw 'Configured HTTPS certificate file does not exist.' }
    $certificatePassword = ''
    if ($endpoint.Certificate.PSObject.Properties['Password']) { $certificatePassword = [string]$endpoint.Certificate.Password }
    $certificate = $null
    try {
        $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($certificateSource, $certificatePassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        if (-not $certificate.HasPrivateKey -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow -or $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow) { throw 'Certificate must have a private key and be currently valid.' }
        $certificateDnsName=$certificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::DnsName,$false)
    } catch { throw 'Unable to load a currently valid HTTPS certificate with private key. Check server.json and the PFX file.' }
    finally { if ($certificate) { $certificate.Dispose() } }
    $serverConfig.AllowedHosts=Get-ValidatedLanHosts $serverConfig.AllowedHosts $certificateDnsName
    $scheme = 'https'
}
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$oldImagePath = $null
$oldEnvironment = $null
$upgradeBackup = $null
$backupComplete = $false
$serviceStopped = $false
$configurationWritten = $false
$oldServerConfigExists = Test-Path -LiteralPath $serverConfigPath
$exePath = Join-Path $InstallDirectory 'School.Training.exe'
if ($existing) {
    $serviceConfig = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $oldImagePath = $serviceConfig.ImagePath
    $oldEnvironment = $serviceConfig.PSObject.Properties['Environment']
    if ($oldEnvironment) { $oldEnvironment = $oldEnvironment.Value }
    if ($oldImagePath -notlike "*$exePath*") { throw 'Existing service belongs to a different installation path. Uninstall or use that path explicitly.' }
}
New-Item -ItemType Directory -Path $InstallDirectory, $DataDirectory -Force | Out-Null
[ordered]@{product='SchoolTraining';schemaVersion=1} | ConvertTo-Json | Set-Content -LiteralPath (Get-SafeProductChild $DataDirectory '.schooltraining-data.json') -Encoding UTF8
Set-DeploymentPermissions $InstallDirectory 'Program' $plannedFiles
Set-DeploymentPermissions $DataDirectory 'Data'
try {
    if ($existing) {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        $serviceStopped = $true
    }
    if ($existing -and -not $SourceDirectory.Equals($InstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        $upgradeBackup = Join-Path $DataDirectory ('upgrade-backups\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
        New-Item -ItemType Directory -Path $upgradeBackup -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $upgradeBackup 'program') -Force | Out-Null
        foreach ($entry in @($priorManifest.files)) {
            $oldFile=Get-SafeProductChild $InstallDirectory $entry.path
            if (Test-Path -LiteralPath $oldFile -PathType Leaf) { $backupFile=Get-SafeProductChild $upgradeBackup ('program\' + $entry.path); New-Item -ItemType Directory -Path (Split-Path -Parent $backupFile) -Force | Out-Null; Copy-Item -LiteralPath $oldFile -Destination $backupFile -Force }
        }
        Copy-Item -LiteralPath (Get-SafeProductChild $InstallDirectory '.schooltraining-install.json') -Destination (Join-Path $upgradeBackup 'program\.schooltraining-install.json') -Force
        if (Test-Path -LiteralPath $serverConfigPath) { Copy-Item -LiteralPath $serverConfigPath -Destination (Join-Path $upgradeBackup 'server.json') -Force }
        if (Test-Path -LiteralPath (Join-Path $DataDirectory 'certificates')) { Copy-SafeTree (Get-SafeProductChild $DataDirectory 'certificates') (Join-Path $upgradeBackup 'certificates') }
        foreach ($name in @('school.db','school.db-wal','school.db-shm','installation.json')) { $item=Join-Path $DataDirectory $name; if (Test-Path -LiteralPath $item) { Copy-Item -LiteralPath $item -Destination (Join-Path $upgradeBackup $name) -Force } }
        if (Test-Path -LiteralPath (Join-Path $DataDirectory 'keys')) { Copy-SafeTree (Get-SafeProductChild $DataDirectory 'keys') (Join-Path $upgradeBackup 'data-keys') }
        $backupComplete = $true
    }
    if ($priorManifest) {
        $nextPaths=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($entry in $plannedFiles) { $null=$nextPaths.Add($entry.path) }
        foreach($entry in @($priorManifest.files)) {
            if ($nextPaths.Contains($entry.path)) { continue }
            $obsolete=Get-SafeProductChild $InstallDirectory $entry.path
            if ((Test-Path -LiteralPath $obsolete -PathType Leaf) -and (Get-FileHash -LiteralPath $obsolete -Algorithm SHA256).Hash.Equals($entry.sha256,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $obsolete -Force }
        }
    }
    # Record only the immutable release payload hashes. Existing modified configuration
    # files will not match these hashes and are preserved by uninstall.
    [ordered]@{product='SchoolTraining';schemaVersion=1;state='installing';files=$plannedFiles.ToArray()} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Get-SafeProductChild $InstallDirectory '.schooltraining-install.json') -Encoding UTF8
    if ($serverConfig) {
        $certificateDirectory = Join-Path $DataDirectory 'certificates'
        New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null
        $installedCertificate = Join-Path $certificateDirectory 'server.pfx'
        if (-not $certificateSource.Equals($installedCertificate,[StringComparison]::OrdinalIgnoreCase)) { Copy-Item -LiteralPath $certificateSource -Destination $installedCertificate -Force }
        $endpoint.Certificate.Path = $installedCertificate
        $serverConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $serverConfigPath -Encoding UTF8
        $configurationWritten = $true
    }
    if (-not $SourceDirectory.Equals($InstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        foreach ($file in $sourceFiles) {
            $relative = $file.FullName.Substring($SourceDirectory.TrimEnd('\').Length + 1)
            $destination = Get-SafeProductChild $InstallDirectory $relative
            if ($relative -eq 'appsettings.Production.json' -and (Test-Path -LiteralPath $destination)) { continue }
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }
    $installedScripts = Join-Path $InstallDirectory 'scripts'
    New-Item -ItemType Directory -Path $installedScripts -Force | Out-Null
    foreach ($name in @('deployment-common.ps1','install-service.ps1','uninstall-service.ps1','restore-backup.ps1')) {
        $sourceScript = Join-Path $PSScriptRoot $name
        $destinationScript = Join-Path $installedScripts $name
        if (-not $sourceScript.Equals($destinationScript, [StringComparison]::OrdinalIgnoreCase)) { Copy-Item -LiteralPath $sourceScript -Destination $destinationScript -Force }
    }
    # Copying from administrator-only staging must not leave service binaries
    # unreadable by LocalService. Apply the complete program policy after copy.
    Set-DeploymentPermissions $InstallDirectory 'Program' $plannedFiles
    Set-DeploymentPermissions $DataDirectory 'Data'
    Set-ServiceBinary ('"' + $exePath + '"') (-not [bool]$existing)
    $environment = @("SCHOOL_DATA_DIR=$DataDirectory", "ASPNETCORE_URLS=${scheme}://${ListenAddress}:$Port", 'ASPNETCORE_ENVIRONMENT=Production')
    New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name 'Environment' -PropertyType MultiString -Value $environment -Force | Out-Null
    New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name 'DelayedAutostart' -PropertyType DWord -Value 1 -Force | Out-Null
    Invoke-ServiceControl @('description', $serviceName, 'School training management platform. ProgramData contains persistent database and encryption keys.')
    Invoke-ServiceControl @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
    Invoke-ServiceControl @('failureflag', $serviceName, '1')
    Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($AllowLan) { New-NetFirewallRule -Name $firewallName -DisplayName 'School Training Platform (Private/Domain)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $exePath -Profile Private,Domain | Out-Null }
    $installation = [ordered]@{product='SchoolTraining'; installDirectory=$InstallDirectory; dataDirectory=$DataDirectory; port=$Port; listenAddress=$ListenAddress; allowLan=[bool]$AllowLan; scheme=$scheme; installedAtUtc=[DateTime]::UtcNow.ToString('o')}
    $installation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $DataDirectory 'installation.json') -Encoding UTF8
    New-Item -Path $productKey -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name DisplayName -Value 'School Training Platform' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name DisplayVersion -Value ([Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion) -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name Publisher -Value 'SchoolTraining' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name InstallLocation -Value $InstallDirectory -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name DataDirectory -Value $DataDirectory -PropertyType String -Force | Out-Null
    $uninstallCommand = '"' + $env:SystemRoot + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installedScripts 'uninstall-service.ps1') + '" -InstallDirectory "' + $InstallDirectory + '" -RemoveProgramFiles'
    New-ItemProperty -LiteralPath $productKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Start-Sleep -Seconds 2
    if ((Get-Service -Name $serviceName).Status -ne 'Running') { throw 'Service exited during startup.' }
    Wait-ProductHealth $DataDirectory
    [ordered]@{product='SchoolTraining';schemaVersion=1;state='installed';files=$plannedFiles.ToArray();installedAtUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Get-SafeProductChild $InstallDirectory '.schooltraining-install.json') -Encoding UTF8
    Set-DeploymentPermissions $InstallDirectory 'Program' $plannedFiles
    Write-Output "Installed. Open ${scheme}://127.0.0.1:$Port on this server to initialize the administrator (use the certificate's trusted hostname for HTTPS)."
    Write-Output "Persistent data: $DataDirectory"
} catch {
    $installFailure = $_
    if ($existing -and $upgradeBackup -and $backupComplete) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        Copy-SafeTree (Join-Path $upgradeBackup 'program') $InstallDirectory
        if ($oldImagePath) { Set-ServiceBinary $oldImagePath $false }
        if ($oldEnvironment) { Set-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name Environment -Value $oldEnvironment }
        if (Test-Path -LiteralPath (Join-Path $upgradeBackup 'server.json')) { Copy-Item -LiteralPath (Join-Path $upgradeBackup 'server.json') -Destination $serverConfigPath -Force }
        elseif ($configurationWritten -and -not $oldServerConfigExists -and (Test-Path -LiteralPath $serverConfigPath)) { Move-Item -LiteralPath $serverConfigPath -Destination (Join-Path $upgradeBackup 'failed-server.json') -Force }
        if (Test-Path -LiteralPath (Join-Path $upgradeBackup 'certificates')) { Copy-SafeTree (Join-Path $upgradeBackup 'certificates') (Get-SafeProductChild $DataDirectory 'certificates') }
        foreach ($name in @('school.db-wal','school.db-shm')) { $current=Join-Path $DataDirectory $name; if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination (Join-Path $upgradeBackup ('failed-' + $name)) -Force } }
        foreach ($name in @('school.db','school.db-wal','school.db-shm','installation.json')) { $old=Join-Path $upgradeBackup $name; if (Test-Path -LiteralPath $old) { Copy-Item -LiteralPath $old -Destination (Join-Path $DataDirectory $name) -Force } }
        $currentKeys = [IO.Path]::GetFullPath((Join-Path $DataDirectory 'keys'))
        $failedKeys = [IO.Path]::GetFullPath((Join-Path $upgradeBackup 'failed-data-keys'))
        if (-not $currentKeys.StartsWith($DataDirectory + '\',[StringComparison]::OrdinalIgnoreCase) -or -not $failedKeys.StartsWith($DataDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe rollback key path.' }
        if (Test-Path -LiteralPath (Join-Path $upgradeBackup 'data-keys')) {
            if (Test-Path -LiteralPath $currentKeys) { Move-Item -LiteralPath $currentKeys -Destination $failedKeys }
            Copy-SafeTree (Join-Path $upgradeBackup 'data-keys') $currentKeys
        }
        Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        if ((Test-Path -LiteralPath $priorInstallationFile)) {
            $restoredInstallation = Get-Content -LiteralPath $priorInstallationFile -Raw | ConvertFrom-Json
            if ($restoredInstallation.PSObject.Properties['allowLan'] -and $restoredInstallation.allowLan) { New-NetFirewallRule -Name $firewallName -DisplayName 'School Training Platform (Private/Domain)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort ([int]$restoredInstallation.port) -Program $exePath -Profile Private,Domain | Out-Null }
        }
        if (Test-Path -LiteralPath $productKey) { Set-ItemProperty -LiteralPath $productKey -Name DisplayVersion -Value ([Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion) }
        Set-DeploymentPermissions $InstallDirectory 'Program' @($priorManifest.files)
        Set-DeploymentPermissions $DataDirectory 'Data'
        Start-Service -Name $serviceName -ErrorAction SilentlyContinue
    }
    elseif ($existing -and $serviceStopped) { Start-Service -Name $serviceName -ErrorAction SilentlyContinue }
    throw $installFailure
}
