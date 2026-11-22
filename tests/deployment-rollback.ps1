[CmdletBinding()]
param([string]$ReportFile = 'docs/verification/deployment-rollback.json')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installer = Join-Path $root 'scripts\install-service.ps1'
$common = Join-Path $root 'scripts\deployment-common.ps1'
$fixtureRoot = Join-Path $root ('.local\deployment-rollback-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
$checks = New-Object 'Collections.Generic.List[object]'
$injection = '$SourceDirectory = Get-CheckedDirectory $SourceDirectory'
$scriptText = [IO.File]::ReadAllText($installer)
if (-not $scriptText.StartsWith('#Requires -RunAsAdministrator') -or -not $scriptText.Contains($injection)) {
    throw 'The isolated installer test cannot find its guard and injection point.'
}
$scriptText = ($scriptText -replace '\A#Requires -RunAsAdministrator\r?\n','').Replace($injection,((@'
. (Join-Path $PSScriptRoot 'test-doubles.ps1')
'@) + [Environment]::NewLine + $injection))
$doubles = @'
function Assert-FixturePath([string]$Path) {
    if (-not [IO.Path]::GetFullPath($Path).StartsWith($fixtureDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Attempt to use the host outside the isolated fixture: $Path"
    }
}
function Assert-ProtectedParentChain([string]$Path) { Assert-FixturePath $Path }
function Set-DeploymentPermissions([string]$Path,[string]$Mode,$PlannedFiles) { Assert-FixturePath $Path }
function Get-Service {
    [CmdletBinding()]
    param([string]$Name)
    if ($Name -ne 'SchoolTraining') { throw 'Unexpected test service.' }
    if ($state.service) { return $state.service }
}
function Set-ServiceBinary([string]$ImagePath,[bool]$Create) {
    Assert-FixturePath $ImagePath.Trim('"')
    if ($Create -and $state.service) { throw 'A pre-existing fixture service would be changed.' }
    if ($Create) { $state.service = New-FakeService 'Stopped' }
    $state.imagePath = $ImagePath
}
function Stop-Service {
    [CmdletBinding()]
    param([string]$Name,[switch]$Force)
    $state.events.Add('stop')
    $state.service.Status = 'Stopped'
}
function Start-Service {
    [CmdletBinding()]
    param([string]$Name)
    $state.starts++
    $state.events.Add('start')
    $state.service.Status = 'Running'
}
function Invoke-ServiceControl([string[]]$Arguments) {
    if ($Arguments[1] -ne 'SchoolTraining') { throw 'Unexpected test service.' }
    if ($Arguments[0] -eq 'delete') { $state.events.Add('delete'); $state.service = $null }
    elseif ($Arguments[0] -eq 'failure' -and $state.failRecoverySetup) { throw 'injected recovery settings failure' }
}
function Get-NetFirewallRule {
    [CmdletBinding()]
    param([string]$Name)
    if ($state.firewall) { return $state.firewall }
}
function New-NetFirewallRule {
    [CmdletBinding()]
    param($Name,$DisplayName,$Direction,$Action,$Protocol,$LocalPort,$Program,$Profile)
    Assert-FixturePath $Program
    if ($state.firewall) { throw 'The test would overwrite an existing firewall rule.' }
    $state.firewall = [pscustomobject]@{ port=$LocalPort; name=$Name }
    return $state.firewall
}
function Remove-NetFirewallRule {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline)]$Rule)
    process {
        if ($Rule) {
            if (-not [Object]::ReferenceEquals($state.firewall,$Rule)) { throw 'The test would remove a different rule.' }
            $state.firewall = $null
        }
    }
}
function Test-Path {
    [CmdletBinding()]
    param([string]$LiteralPath,[string]$PathType)
    if ($LiteralPath.StartsWith('HKLM:')) { return $state.registered }
    Microsoft.PowerShell.Management\Test-Path @PSBoundParameters
}
function New-Item {
    [CmdletBinding()]
    param([string[]]$Path,[string]$ItemType,[switch]$Force)
    if ($Path[0].StartsWith('HKLM:')) {
        if ($state.registered) { throw 'An existing registration would be altered.' }
        $state.registered = $true
        return
    }
    foreach ($target in $Path) { Assert-FixturePath $target }
    Microsoft.PowerShell.Management\New-Item @PSBoundParameters
}
function Get-ItemProperty {
    [CmdletBinding()]
    param([string]$LiteralPath)
    if (-not $LiteralPath.StartsWith('HKLM:')) { throw 'Unexpected registry access in the test.' }
    return [pscustomobject]@{ ImagePath=$state.imagePath; Environment=$state.environment }
}
function New-ItemProperty {
    [CmdletBinding()]
    param($LiteralPath,$Name,$Value,$PropertyType,[switch]$Force)
    if (-not $LiteralPath.StartsWith('HKLM:')) { throw 'Unexpected registry access in the test.' }
    if ($Name -eq 'Environment') { $state.environment = $Value }
    if ($Name -eq 'DisplayVersion') { $state.displayVersion = $Value }
}
function Set-ItemProperty {
    [CmdletBinding()]
    param($LiteralPath,$Name,$Value)
    if (-not $LiteralPath.StartsWith('HKLM:')) { throw 'Unexpected registry access in the test.' }
    if ($Name -eq 'Environment') { $state.environment = $Value }
    if ($Name -eq 'DisplayVersion') { $state.displayVersion = $Value }
}
function Remove-ItemProperty {
    [CmdletBinding()]
    param($LiteralPath,$Name)
    if (-not $LiteralPath.StartsWith('HKLM:')) { throw 'Unexpected registry access in the test.' }
    if ($Name -eq 'Environment') { $state.environment = $null }
}
function Remove-Item {
    [CmdletBinding()]
    param([string]$LiteralPath,[switch]$Force)
    if ($LiteralPath.StartsWith('HKLM:')) {
        if (-not $state.registered) { throw 'The test would remove an unknown registration.' }
        $state.registered = $false
        return
    }
    Assert-FixturePath $LiteralPath
    Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
}
function Wait-ProductHealth([string]$DataDirectory,[string]$ExpectedVersion) {
    Assert-FixturePath $DataDirectory
    $state.events.Add('health')
    $state.versions.Add($ExpectedVersion)
    if ($state.versions.Count -eq 1) {
        Assert-True ($ExpectedVersion -eq $state.nextVersion) 'The new version was not required by the health check.'
        [IO.File]::WriteAllText((Join-Path $DataDirectory 'school.db'),'database written by failed startup')
        [IO.File]::WriteAllText((Join-Path $DataDirectory 'startup.log'),'failure log')
        if ($state.addUnknownProgramFile) { [IO.File]::WriteAllText((Join-Path $installDirectory 'unknown.txt'),'untouched') }
        throw 'injected new version status failure'
    }
    Assert-True ($ExpectedVersion -eq $state.previousVersion) 'The restored version was not required by the health check.'
    Assert-True ([IO.File]::ReadAllText((Join-Path $DataDirectory 'school.db')) -eq 'original database') 'Health probe ran before database was restored.'
    if ($state.failRollbackHealth) { throw 'injected old version status failure' }
}
'@

function Assert-True([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function New-FakeService([string]$Status) {
    return [pscustomobject]@{ Status=$Status } | Add-Member -PassThru ScriptMethod WaitForStatus {
        param($Expected,$Timeout)
        if ($this.Status -ne $Expected) { throw "Fake service not in expected state: $Expected" }
    }
}
function Write-OwnedManifest([string]$Directory) {
    $exe = Join-Path $Directory 'School.Training.exe'
    $manifest = @{ product='SchoolTraining'; schemaVersion=1; state='installed'; files=@(@{ path='School.Training.exe'; sha256=(Get-FileHash -LiteralPath $exe).Hash }) }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Directory '.schooltraining-install.json') -Encoding UTF8
}
function Write-FakeLanConfig([string]$Directory) {
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    try {
        $req = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=training.school.test',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $cert = $req.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddDays(1))
        try {
            $pfx = Join-Path $Directory 'test.pfx'
            [IO.File]::WriteAllBytes($pfx,$cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,'isolated-test'))
        } finally { $cert.Dispose() }
    } finally { $rsa.Dispose() }
    $file = Join-Path $Directory 'lan.json'
    @{ AllowedHosts='training.school.test'; Kestrel=@{ Endpoints=@{ SchoolHttps=@{ Url='https://0.0.0.0:5443'; Certificate=@{ Path=$pfx; Password='isolated-test' } } } } } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $file -Encoding UTF8
    return $file
}
function Test-InstallFailure([string]$Scenario) {
    $fixtureDirectory = Join-Path $fixtureRoot $Scenario
    $scriptDirectory = Join-Path $fixtureDirectory 'scripts'
    $source = Join-Path $fixtureDirectory 'app'
    $installDirectory = Join-Path $fixtureDirectory 'program'
    $dataDirectory = Join-Path $fixtureDirectory 'data'
    New-Item -ItemType Directory -Path $scriptDirectory,$source | Out-Null
    Copy-Item -LiteralPath $common,(Join-Path $root 'scripts\uninstall-service.ps1'),(Join-Path $root 'scripts\restore-backup.ps1') -Destination $scriptDirectory
    $testInstaller = Join-Path $scriptDirectory 'install-service.ps1'
    [IO.File]::WriteAllText($testInstaller,$scriptText)
    [IO.File]::WriteAllText((Join-Path $scriptDirectory 'test-doubles.ps1'),$doubles)
    $nextExe = Join-Path $source 'School.Training.exe'
    Copy-Item -LiteralPath (Join-Path $env:windir 'System32\where.exe') -Destination $nextExe
    $newFile = Join-Path $source 'new-release.dll'
    [IO.File]::WriteAllText($newFile,'new release file')
    $nextVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($nextExe).ProductVersion -split '\+',2)[0]
    $state = @{
        service=$null; firewall=$null; registered=$false; environment=$null; displayVersion=$null; imagePath=$null; starts=0
        nextVersion=$nextVersion; previousVersion=$null; versions=(New-Object 'Collections.Generic.List[string]'); events=(New-Object 'Collections.Generic.List[string]')
        failRecoverySetup=($Scenario -eq 'fresh-early-failure'); failRollbackHealth=($Scenario -eq 'upgrade-unhealthy')
        addUnknownProgramFile=($Scenario -eq 'fresh-unknown-file')
    }
    $priorInstall = Join-Path $dataDirectory 'installation.json'
    $setupParameters = @{SourceDirectory=$source; InstallDirectory=$installDirectory; DataDirectory=$dataDirectory}
    if ($Scenario -in @('fresh-reused-data','upgrade-healthy','upgrade-stopped','upgrade-unhealthy')) {
        New-Item -ItemType Directory -Path $dataDirectory,(Join-Path $dataDirectory 'keys') | Out-Null
        [IO.File]::WriteAllText((Join-Path $dataDirectory '.schooltraining-data.json'),'{"product":"SchoolTraining","schemaVersion":1}')
        [IO.File]::WriteAllText((Join-Path $dataDirectory 'keys\original.xml'),'original key')
        [IO.File]::WriteAllText((Join-Path $dataDirectory 'school.db'),'original database')
        $metadata = @{ product='SchoolTraining'; installDirectory=$installDirectory; dataDirectory=$dataDirectory; port=5080; listenAddress='127.0.0.1'; allowLan=$false }
        [IO.File]::WriteAllText($priorInstall,($metadata | ConvertTo-Json))
        $originalMetadata = [IO.File]::ReadAllText($priorInstall)
    }
    if ($Scenario -eq 'fresh-lan') {
        $setupParameters['AllowLan'] = $true
        $setupParameters['ListenAddress'] = '0.0.0.0'
        $setupParameters['Port'] = 5443
        $setupParameters['ServerConfigurationFile'] = Write-FakeLanConfig $fixtureDirectory
    }
    if ($Scenario.StartsWith('upgrade-')) {
        New-Item -ItemType Directory -Path $installDirectory | Out-Null
        $oldExe = Join-Path $installDirectory 'School.Training.exe'
        Copy-Item -LiteralPath (Join-Path $env:windir 'System32\whoami.exe') -Destination $oldExe
        $oldExeHash = (Get-FileHash -LiteralPath $oldExe).Hash
        $state.previousVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($oldExe).ProductVersion -split '\+',2)[0]
        Write-OwnedManifest $installDirectory
        $state.imagePath = '"' + $oldExe + '"'
        $state.service = New-FakeService $(if ($Scenario -eq 'upgrade-stopped') { 'Stopped' } else { 'Running' })
        $state.registered = $true
        $state.environment = @('pre-existing environment')
    }
    if ($Scenario -eq 'fresh-pre-existing-rule') { $state.firewall = [pscustomobject]@{ port=9090; name='SchoolTraining-HTTP' } }
    if ($Scenario -eq 'fresh-pre-existing-registration') { $state.registered = $true }
    if ($Scenario -eq 'fresh-pre-existing-empty-program-root') { New-Item -ItemType Directory -Path $installDirectory | Out-Null }
    $failure = $null
    $output = @(& {
        try { & $testInstaller @setupParameters }
        catch { $_.Exception.Message }
    })
    $failure = $output[-1]
    if ($Scenario.StartsWith('upgrade-')) {
        Assert-True ($failure.Contains('injected new version status failure')) "Unexpected installer error: $failure"
        Assert-True ($state.registered -and $state.environment[0] -eq 'pre-existing environment') 'Previous registration or environment was lost.'
        Assert-True ((Get-FileHash -LiteralPath $oldExe).Hash -eq $oldExeHash) 'Old executable was not restored.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'new-release.dll'))) 'The failed release left behind a new program file.'
        if ($Scenario -eq 'upgrade-unhealthy') {
            Assert-True ($failure.Contains('Rollback could not be verified') -and $failure.Contains('injected old version status failure')) 'Rollback failure report dropped an error.'
        } elseif ($Scenario -eq 'upgrade-healthy') {
            Assert-True ($state.starts -eq 2 -and $state.versions.Count -eq 2 -and ($output -contains 'Previous service restarted and passed its live status check.')) 'Old service restart was not health verified.'
        } else {
            Assert-True ($state.starts -eq 1 -and $state.service.Status -eq 'Stopped' -and $state.versions.Count -eq 1) 'A previously stopped service was restarted by rollback.'
        }
    } elseif ($Scenario -eq 'fresh-pre-existing-rule' -or $Scenario -eq 'fresh-pre-existing-registration') {
        Assert-True ($failure.Contains('existing product registration or firewall rule')) "Unexpected preflight error: $failure"
        Assert-True ($state.starts -eq 0 -and -not (Test-Path -LiteralPath $installDirectory)) 'Preflight refusal changed the installation.'
        Assert-True ($state.registered -or $state.firewall.port -eq 9090) 'An existing registration or firewall rule was lost.'
        return
    } else {
        $expectedFailure = if ($state.failRecoverySetup) { 'injected recovery settings failure' } else { 'injected new version status failure' }
        Assert-True ($failure -eq $expectedFailure) "Unexpected installer error: $failure"
        Assert-True (-not $state.service -and -not $state.firewall -and -not $state.registered -and $state.events.Contains('delete')) 'First install cleanup did not remove its host registrations.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'School.Training.exe'))) 'First install cleanup retained the exact installed program.'
        if ($Scenario -eq 'fresh-pre-existing-empty-program-root') {
            Assert-True (Test-Path -LiteralPath $installDirectory -PathType Container) 'Rollback removed a previously existing install folder.'
        } elseif ($Scenario -eq 'fresh-unknown-file') {
            Assert-True ([IO.File]::ReadAllText((Join-Path $installDirectory 'unknown.txt')) -eq 'untouched') 'Rollback touched an unknown program file.'
        } else {
            Assert-True (-not (Test-Path -LiteralPath $installDirectory)) 'Rollback left behind a new empty install folder.'
        }
        if ($Scenario -eq 'fresh-lan') {
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'server.json'))) 'New failed LAN configuration was left active.'
            Assert-True (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'certificates\server.pfx'))) 'New failed LAN certificate was left active.'
        }
    }
    if ($Scenario -in @('fresh-reused-data','upgrade-healthy','upgrade-stopped','upgrade-unhealthy')) {
        Assert-True ([IO.File]::ReadAllText((Join-Path $dataDirectory 'school.db')) -eq 'original database') 'Existing database was not restored.'
        Assert-True ([IO.File]::ReadAllText((Join-Path $dataDirectory 'keys\original.xml')) -eq 'original key') 'Existing credential key was not restored.'
        Assert-True ([IO.File]::ReadAllText($priorInstall) -eq $originalMetadata) 'Existing installation metadata was not restored.'
        Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'upgrade-backups') -Filter 'failed-school.db' -Recurse).Count -eq 1) 'Failed startup data was not retained.'
    } elseif (-not $state.failRecoverySetup) {
        Assert-True (-not (Test-Path -LiteralPath $priorInstall)) 'First install left failed activation metadata live.'
        Assert-True (Test-Path -LiteralPath (Join-Path $dataDirectory 'school.db')) 'First install removed database data after startup.'
    }
}

$scenarios = @('fresh-early-failure','fresh-reused-data','fresh-lan','fresh-unknown-file','fresh-pre-existing-empty-program-root',
    'fresh-pre-existing-rule','fresh-pre-existing-registration','upgrade-healthy','upgrade-stopped','upgrade-unhealthy')
foreach ($scenario in $scenarios) {
    try {
        Test-InstallFailure $scenario
        $checks.Add([ordered]@{name=$scenario;passed=$true})
        Write-Output "PASS $scenario"
    } catch {
        $checks.Add([ordered]@{name=$scenario;passed=$false;error=$_.Exception.Message})
        Write-Output "FAIL $scenario - $($_.Exception.Message)"
    }
}
$passCount = @($checks | Where-Object { $_.passed }).Count
$reportPath = [IO.Path]::GetFullPath((Join-Path $root $ReportFile))
if (-not $reportPath.StartsWith((Join-Path $root 'docs\verification') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Report must remain inside docs/verification.' }
$report = [ordered]@{
    suite='deployment-rollback-injected'; completedAtUtc=[DateTime]::UtcNow.ToString('o'); powershellVersion=$PSVersionTable.PSVersion.ToString()
    installerSha256=(Get-FileHash -LiteralPath $installer).Hash.ToLowerInvariant(); commonScriptSha256=(Get-FileHash -LiteralPath $common).Hash.ToLowerInvariant()
    scope='Production install script copied to isolated fixtures with host service, registry, firewall, ACL and health operations replaced by test doubles. Real file copy, hashing, backup and rollback exercised. No elevated operations.'
    passed=($passCount -eq $checks.Count); passedChecks=$passCount; totalChecks=$checks.Count; checks=$checks.ToArray()
}
[IO.File]::WriteAllText($reportPath,($report | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
if ($passCount -ne $checks.Count) { exit 1 }
