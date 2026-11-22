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
function Copy-ProgramFile([string]$Source,[string]$Relative) {
    $destination = Get-SafeProductChild $InstallDirectory $Relative
    $parent = Split-Path -Parent $destination
    for ($directory = $parent; $directory.StartsWith($InstallDirectory + '\',[StringComparison]::OrdinalIgnoreCase); $directory = Split-Path -Parent $directory) {
        if (Test-Path -LiteralPath $directory) { break }
        $null = $createdProgramDirectories.Add($directory)
    }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if ($originalProgramFiles.Contains($destination)) {
        Copy-Item -LiteralPath $Source -Destination $destination -Force
    } else {
        [IO.File]::Copy($Source,$destination,$false)
        $createdProgramFiles[$destination] = $plannedHashes[$Relative]
    }
}
function Remove-NewProgramFiles {
    foreach ($file in $createdProgramFiles.Keys) {
        $null = Assert-NoReparseInPath $file
        if ((Test-Path -LiteralPath $file -PathType Leaf) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -eq $createdProgramFiles[$file]) {
            Remove-Item -LiteralPath $file -Force
        }
    }
    foreach ($directory in @($createdProgramDirectories | Sort-Object Length -Descending)) {
        $null = Assert-NoReparseInPath $directory
        if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) {
            Remove-Item -LiteralPath $directory -Force
        }
    }
}
function Restore-DeploymentFile([string]$Relative,[bool]$ArchiveNew) {
    $saved = Get-SafeProductChild $upgradeBackup $Relative
    $current = Get-SafeProductChild $DataDirectory $Relative
    $hasBackup = Test-Path -LiteralPath $saved -PathType Leaf
    if (($hasBackup -or $ArchiveNew) -and (Test-Path -LiteralPath $current -PathType Leaf)) {
        $failed = Get-SafeProductChild $upgradeBackup ('failed-' + $Relative.Replace('\','-'))
        Move-Item -LiteralPath $current -Destination $failed
    }
    if ($hasBackup) { Copy-Item -LiteralPath $saved -Destination $current }
}
function Start-CheckedService([string]$ExpectedVersion) {
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    Wait-ProductHealth $DataDirectory $ExpectedVersion
    if ((Get-Service -Name $serviceName).Status -ne 'Running') { throw 'Service exited during startup.' }
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
$originalProgramFiles = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$createdProgramDirectories = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$createdProgramFiles = @{}
$plannedHashes = @{}
foreach ($entry in $plannedFiles) {
    $plannedHashes[$entry.path] = $entry.sha256
    $target = Get-SafeProductChild $InstallDirectory $entry.path
    if (Test-Path -LiteralPath $target -PathType Leaf) { $null = $originalProgramFiles.Add($target) }
}
$installManifestPath = Get-SafeProductChild $InstallDirectory '.schooltraining-install.json'
$installDirectoryExisted = Test-Path -LiteralPath $InstallDirectory -PathType Container
$dataDirectoryHasFiles = (Test-Path -LiteralPath $DataDirectory -PathType Container) -and @(Get-ChildItem -LiteralPath $DataDirectory -Force).Count -gt 0
$priorInstallationFile = Get-SafeProductChild $DataDirectory 'installation.json'
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
$serverConfigPath = Get-SafeProductChild $DataDirectory 'server.json'
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
$previousFirewall = @(Get-NetFirewallRule -Name $firewallName -ErrorAction SilentlyContinue)
$previouslyRunning = $existing -and $existing.Status -ne 'Stopped'
$serviceCreated = $false
$registrationCreated = $false
$firewallCreated = $null
$firewallChanged = $false
$installManifestHash = $null
$installationHash = $null
$startupAttempted = $false
$oldImagePath = $null
$oldEnvironment = $null
$upgradeBackup = $null
$backupComplete = $false
$serviceStopped = $false
$configurationWritten = $false
$certificateWritten = $false
$installationWritten = $false
$exePath = Join-Path $InstallDirectory 'School.Training.exe'
if ($existing) {
    if (-not $priorManifest) { throw 'Existing service has no valid program installation manifest.' }
    $serviceConfig = Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $oldImagePath = $serviceConfig.ImagePath
    $oldEnvironment = $serviceConfig.PSObject.Properties['Environment']
    if ($oldEnvironment) { $oldEnvironment = $oldEnvironment.Value }
    if ($oldImagePath -notlike "*$exePath*") { throw 'Existing service belongs to a different installation path. Uninstall or use that path explicitly.' }
} elseif ((Test-Path -LiteralPath $productKey) -or $previousFirewall.Count -gt 0) {
    throw 'An existing product registration or firewall rule has no matching installed service. Review it before retrying installation.'
}
try {
    New-Item -ItemType Directory -Path $InstallDirectory, $DataDirectory -Force | Out-Null
    Set-DeploymentPermissions $InstallDirectory 'Program' $plannedFiles
    Set-DeploymentPermissions $DataDirectory 'Data'
    $dataMarker = Get-SafeProductChild $DataDirectory '.schooltraining-data.json'
    if (-not (Test-Path -LiteralPath $dataMarker)) {
        [ordered]@{product='SchoolTraining';schemaVersion=1} | ConvertTo-Json | Set-Content -LiteralPath $dataMarker -Encoding UTF8
    }
    if ($previouslyRunning) {
        Stop-Service -Name $serviceName -Force
        $serviceStopped = $true
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    if ($priorManifest -or $dataDirectoryHasFiles -or $serverConfig) {
        $backupName = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N')
        $upgradeBackup = Get-SafeProductChild $DataDirectory ('upgrade-backups\' + $backupName)
        New-Item -ItemType Directory -Path $upgradeBackup -Force | Out-Null
        if ($priorManifest) {
            New-Item -ItemType Directory -Path (Join-Path $upgradeBackup 'program') -Force | Out-Null
            foreach ($entry in @($priorManifest.files)) {
                $oldFile = Get-SafeProductChild $InstallDirectory $entry.path
                if (Test-Path -LiteralPath $oldFile -PathType Leaf) {
                    $backupFile = Get-SafeProductChild $upgradeBackup ('program\' + $entry.path)
                    New-Item -ItemType Directory -Path (Split-Path -Parent $backupFile) -Force | Out-Null
                    Copy-Item -LiteralPath $oldFile -Destination $backupFile
                }
            }
            Copy-Item -LiteralPath $installManifestPath -Destination (Join-Path $upgradeBackup 'program\.schooltraining-install.json')
        }
        foreach ($name in @('school.db','school.db-wal','school.db-shm','installation.json','server.json','certificates\server.pfx')) {
            $item = Get-SafeProductChild $DataDirectory $name
            if (Test-Path -LiteralPath $item -PathType Leaf) {
                $backupFile = Get-SafeProductChild $upgradeBackup $name
                New-Item -ItemType Directory -Path (Split-Path -Parent $backupFile) -Force | Out-Null
                Copy-Item -LiteralPath $item -Destination $backupFile
            }
        }
        $keys = Get-SafeProductChild $DataDirectory 'keys'
        if (Test-Path -LiteralPath $keys) { Copy-SafeTree $keys (Join-Path $upgradeBackup 'data-keys') }
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
    [ordered]@{product='SchoolTraining';schemaVersion=1;state='installing';files=$plannedFiles.ToArray()} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $installManifestPath -Encoding UTF8
    $installManifestHash = (Get-FileHash -LiteralPath $installManifestPath -Algorithm SHA256).Hash
    if ($serverConfig) {
        $installedCertificate = Get-SafeProductChild $DataDirectory 'certificates\server.pfx'
        if (-not $certificateSource.Equals($installedCertificate,[StringComparison]::OrdinalIgnoreCase)) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $installedCertificate) -Force | Out-Null
            $certificateWritten = $true
            Copy-Item -LiteralPath $certificateSource -Destination $installedCertificate -Force
        }
        $endpoint.Certificate.Path = $installedCertificate
        $configurationWritten = $true
        $serverConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $serverConfigPath -Encoding UTF8
    }
    if (-not $SourceDirectory.Equals($InstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        foreach ($file in $sourceFiles) {
            $relative = $file.FullName.Substring($SourceDirectory.TrimEnd('\').Length + 1)
            $destination = Get-SafeProductChild $InstallDirectory $relative
            if ($relative -eq 'appsettings.Production.json' -and (Test-Path -LiteralPath $destination)) { continue }
            Copy-ProgramFile $file.FullName $relative
        }
    }
    $installedScripts = Join-Path $InstallDirectory 'scripts'
    foreach ($name in @('deployment-common.ps1','install-service.ps1','uninstall-service.ps1','restore-backup.ps1')) {
        $sourceScript = Join-Path $PSScriptRoot $name
        $destinationScript = Join-Path $installedScripts $name
        if (-not $sourceScript.Equals($destinationScript, [StringComparison]::OrdinalIgnoreCase)) { Copy-ProgramFile $sourceScript ('scripts\' + $name) }
    }
    # Copying from administrator-only staging must not leave service binaries
    # unreadable by LocalService. Apply the complete program policy after copy.
    Set-DeploymentPermissions $InstallDirectory 'Program' $plannedFiles
    Set-DeploymentPermissions $DataDirectory 'Data'
    Set-ServiceBinary ('"' + $exePath + '"') (-not [bool]$existing)
    $serviceCreated = -not $existing
    $environment = @("SCHOOL_DATA_DIR=$DataDirectory", "ASPNETCORE_URLS=${scheme}://${ListenAddress}:$Port", 'ASPNETCORE_ENVIRONMENT=Production')
    New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name 'Environment' -PropertyType MultiString -Value $environment -Force | Out-Null
    New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name 'DelayedAutostart' -PropertyType DWord -Value 1 -Force | Out-Null
    Invoke-ServiceControl @('description', $serviceName, 'School training management platform. ProgramData contains persistent database and encryption keys.')
    Invoke-ServiceControl @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
    Invoke-ServiceControl @('failureflag', $serviceName, '1')
    if ($previousFirewall.Count -gt 0) {
        $firewallChanged = $true
        $previousFirewall | Remove-NetFirewallRule
    }
    if ($AllowLan) { $firewallCreated = New-NetFirewallRule -Name $firewallName -DisplayName 'School Training Platform (Private/Domain)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $exePath -Profile Private,Domain }
    $installation = [ordered]@{product='SchoolTraining'; installDirectory=$InstallDirectory; dataDirectory=$DataDirectory; port=$Port; listenAddress=$ListenAddress; allowLan=[bool]$AllowLan; scheme=$scheme; installedAtUtc=[DateTime]::UtcNow.ToString('o')}
    $installationWritten = $true
    $installation | ConvertTo-Json | Set-Content -LiteralPath $priorInstallationFile -Encoding UTF8
    $installationHash = (Get-FileHash -LiteralPath $priorInstallationFile -Algorithm SHA256).Hash
    if (-not $existing -or -not (Test-Path -LiteralPath $productKey)) {
        New-Item -Path $productKey | Out-Null
        $registrationCreated = $true
    }
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion
    New-ItemProperty -LiteralPath $productKey -Name DisplayName -Value 'School Training Platform' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name DisplayVersion -Value $productVersion -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name Publisher -Value 'SchoolTraining' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name InstallLocation -Value $InstallDirectory -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name DataDirectory -Value $DataDirectory -PropertyType String -Force | Out-Null
    $uninstallCommand = '"' + $env:SystemRoot + '\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installedScripts 'uninstall-service.ps1') + '" -InstallDirectory "' + $InstallDirectory + '" -RemoveProgramFiles'
    New-ItemProperty -LiteralPath $productKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $productKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
    $startupAttempted = $true
    Start-CheckedService ($productVersion -split '\+',2)[0]
    [ordered]@{product='SchoolTraining';schemaVersion=1;state='installed';files=$plannedFiles.ToArray();installedAtUtc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $installManifestPath -Encoding UTF8
    $installManifestHash = (Get-FileHash -LiteralPath $installManifestPath -Algorithm SHA256).Hash
    Set-DeploymentPermissions $InstallDirectory 'Program' $plannedFiles
    Write-Output "Installed. Open ${scheme}://127.0.0.1:$Port on this server to initialize the administrator (use the certificate's trusted hostname for HTTPS)."
    Write-Output "Persistent data: $DataDirectory"
} catch {
    $installFailure = $_
    try {
        if ($existing -or $serviceCreated) {
            $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if (($startupAttempted -or $serviceCreated) -and $currentService -and $currentService.Status -ne 'Stopped') {
                Stop-Service -Name $serviceName -Force
                (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
            }
            if ($serviceCreated -and $currentService) { Invoke-ServiceControl @('delete', $serviceName) }
        }
        if ($firewallCreated) { $firewallCreated | Remove-NetFirewallRule }
        if ($registrationCreated) { Remove-Item -LiteralPath $productKey -Force }
        Remove-NewProgramFiles
        if ($backupComplete) {
            if ($priorManifest) { Copy-SafeTree (Join-Path $upgradeBackup 'program') $InstallDirectory }
            if ($configurationWritten) { Restore-DeploymentFile 'server.json' $true }
            if ($certificateWritten) { Restore-DeploymentFile 'certificates\server.pfx' $true }
            if ($installationWritten) { Restore-DeploymentFile 'installation.json' $true }
            if ($startupAttempted) {
                $previousDatabaseExists = Test-Path -LiteralPath (Join-Path $upgradeBackup 'school.db') -PathType Leaf
                foreach ($name in @('school.db','school.db-wal','school.db-shm')) { Restore-DeploymentFile $name $previousDatabaseExists }
                $savedKeys = Get-SafeProductChild $upgradeBackup 'data-keys'
                if (Test-Path -LiteralPath $savedKeys) {
                    $currentKeys = Get-SafeProductChild $DataDirectory 'keys'
                    if (Test-Path -LiteralPath $currentKeys) { Move-Item -LiteralPath $currentKeys -Destination (Get-SafeProductChild $upgradeBackup 'failed-data-keys') }
                    Copy-SafeTree $savedKeys $currentKeys
                }
            }
        }
        elseif (-not $existing -and $installationHash -and (Test-Path -LiteralPath $priorInstallationFile -PathType Leaf) -and (Get-FileHash -LiteralPath $priorInstallationFile -Algorithm SHA256).Hash -eq $installationHash) {
            # No data existed before this attempt. Keep the database and logs from startup.
            Remove-Item -LiteralPath $priorInstallationFile -Force
        }
        if (-not $priorManifest -and $installManifestHash -and (Test-Path -LiteralPath $installManifestPath) -and (Get-FileHash -LiteralPath $installManifestPath -Algorithm SHA256).Hash -eq $installManifestHash) {
            $remaining = @(Get-ChildItem -LiteralPath $InstallDirectory -Force | Where-Object FullName -ne $installManifestPath)
            if ($remaining.Count -eq 0) { Remove-Item -LiteralPath $installManifestPath -Force }
        }
        if (-not $installDirectoryExisted -and (Test-Path -LiteralPath $InstallDirectory -PathType Container) -and @(Get-ChildItem -LiteralPath $InstallDirectory -Force).Count -eq 0) {
            Remove-Item -LiteralPath $InstallDirectory -Force
        }
        if ($priorManifest -and $backupComplete) {
            Set-DeploymentPermissions $InstallDirectory 'Program' @($priorManifest.files)
            Set-DeploymentPermissions $DataDirectory 'Data'
        }
        $restoredVersion = if ($existing) { [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).ProductVersion }
        if ($existing -and $backupComplete) {
            Set-ServiceBinary $oldImagePath $false
            $serviceKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
            if ($oldEnvironment) { Set-ItemProperty -LiteralPath $serviceKey -Name Environment -Value $oldEnvironment }
            else { Remove-ItemProperty -LiteralPath $serviceKey -Name Environment -ErrorAction SilentlyContinue }
            if ($firewallChanged -and (Test-Path -LiteralPath $priorInstallationFile)) {
                $restoredInstallation = Get-Content -LiteralPath $priorInstallationFile -Raw | ConvertFrom-Json
                if ($restoredInstallation.PSObject.Properties['allowLan'] -and $restoredInstallation.allowLan) { New-NetFirewallRule -Name $firewallName -DisplayName 'School Training Platform (Private/Domain)' -Direction Inbound -Action Allow -Protocol TCP -LocalPort ([int]$restoredInstallation.port) -Program $exePath -Profile Private,Domain | Out-Null }
            }
            if (Test-Path -LiteralPath $productKey) { Set-ItemProperty -LiteralPath $productKey -Name DisplayVersion -Value $restoredVersion }
        }
        if ($previouslyRunning -and $serviceStopped) {
            Start-CheckedService ($restoredVersion -split '\+',2)[0]
            Write-Output 'Previous service restarted and passed its live status check.'
        }
    } catch {
        throw [InvalidOperationException]::new("Installation failed: $($installFailure.Exception.Message) Rollback could not be verified: $($_.Exception.Message)", $installFailure.Exception)
    }
    throw $installFailure
}
