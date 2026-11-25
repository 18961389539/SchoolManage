[CmdletBinding()]
param(
    [string]$AppDirectory = $env:SCHOOL_APP_DIR,
    [string]$ReportFile = 'docs/verification/deployment-restore.json'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $AppDirectory) { throw 'Supply -AppDirectory or SCHOOL_APP_DIR pointing to a published win-x64 app.' }
$app = [IO.Path]::GetFullPath($AppDirectory)
$program = Join-Path $app 'School.Training.exe'
$assembly = Join-Path $app 'School.Training.dll'
foreach ($required in @($program,$assembly,(Join-Path $app 'coreclr.dll'))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Published self-contained artifact is missing: $required" }
}
$publishedVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($program).ProductVersion -split '\+',2)[0]
$restoreScript = Join-Path $root 'scripts\restore-backup.ps1'
$commonScript = Join-Path $root 'scripts\deployment-common.ps1'
$fixtureRoot = Join-Path $root ('.local\deployment-restore-' + [Guid]::NewGuid().ToString('N'))
$fixtureScripts = Join-Path $fixtureRoot 'scripts'
New-Item -ItemType Directory -Path $fixtureScripts | Out-Null
Copy-Item -LiteralPath $commonScript -Destination $fixtureScripts
. $commonScript

$source = [IO.File]::ReadAllText($restoreScript)
$injection = 'Add-Type -AssemblyName System.IO.Compression.FileSystem'
if (-not $source.StartsWith('#Requires -RunAsAdministrator') -or -not $source.Contains($injection)) {
    throw 'The isolated restore test cannot find its guard and injection point.'
}
$fixtureRestore = Join-Path $fixtureScripts 'restore-backup.ps1'
$source = ($source -replace '\A#Requires -RunAsAdministrator\r?\n','').Replace($injection,(". (Join-Path " + '$PSScriptRoot' + " 'test-doubles.ps1')" + [Environment]::NewLine + $injection))
[IO.File]::WriteAllText($fixtureRestore,$source)
[IO.File]::WriteAllText((Join-Path $fixtureScripts 'test-doubles.ps1'),@'
$realWaitProductHealth = (Get-Command Wait-ProductHealth -CommandType Function).ScriptBlock
function Get-ItemProperty {
    [CmdletBinding()]
    param([string]$LiteralPath)
    if ($LiteralPath -ne 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SchoolTraining') { throw 'Unexpected test registration.' }
    [pscustomobject]@{ DataDirectory=$state.data; InstallLocation=$app }
}
function Get-Service {
    [CmdletBinding()]
    param([string]$Name)
    if ($Name -ne 'SchoolTraining') { throw 'Unexpected test service.' }
    $state.service
}
function Stop-Service {
    [CmdletBinding()]
    param([string]$Name,[switch]$Force)
    if ($Name -ne 'SchoolTraining') { throw 'Unexpected test service.' }
    Stop-FixtureApp $state.running
    $state.running = $null
    $state.service.Status = 'Stopped'
    $state.stopCount++
    if ($state.stopCount -eq 1) { $state.originalItems = @(Get-DatasetHashes $state.data) }
}
function Start-Service {
    [CmdletBinding()]
    param([string]$Name)
    if ($Name -ne 'SchoolTraining') { throw 'Unexpected test service.' }
    $state.startCount++
    if ($state.startCount -eq 2 -or $state.scenario -eq 'partial-move') {
        Assert-EqualDataset $state.originalItems @(Get-DatasetHashes $state.data)
        $state.restoredOriginalBeforeRestart = $true
    }
    $state.running = Start-FixtureApp $state.data $state.port
    $state.service.Status = 'Running'
}
function Wait-ProductHealth([string]$DataDirectory,[string]$ExpectedVersion) {
    Assert-FixturePath $DataDirectory
    if ($ExpectedVersion -ne $publishedVersion) { throw 'Recovery probe did not require the registered program version.' }
    & $realWaitProductHealth $DataDirectory $ExpectedVersion
    $state.probeCount++
    $school = (Get-SchoolStatus $state.port).schoolName
    $expectedSchool = if ($state.startCount -eq 2 -or $state.scenario -eq 'partial-move') { 'Original school' } else { 'Backup school' }
    Assert-True ($school -eq $expectedSchool) "The real HTTP status probe mounted the wrong SQLite database: $school"
    if ($state.scenario -in @('replacement-unhealthy','both-unhealthy') -and $state.probeCount -eq 1) { throw 'injected replacement health failure' }
    if ($state.scenario -eq 'both-unhealthy' -and $state.probeCount -eq 2) { throw 'injected rollback health failure' }
}
function Move-Item {
    [CmdletBinding()]
    param([string]$LiteralPath,[string]$Destination)
    Assert-FixturePath $LiteralPath
    Assert-FixturePath $Destination
    if ($state.scenario -eq 'partial-move' -and $LiteralPath -eq (Join-Path $state.data 'keys') -and -not $state.moveFailed) {
        $state.moveFailed = $true
        throw 'injected partial move failure'
    }
    Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
}
'@)

function Assert-True([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Assert-FixturePath([string]$Path) {
    if (-not [IO.Path]::GetFullPath($Path).StartsWith($fixtureRoot + '\',[StringComparison]::OrdinalIgnoreCase)) {
        throw "Test tried to use a path outside the isolated workspace: $Path"
    }
}
function Get-FreePort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
function Get-SchoolStatus([int]$Port) {
    Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/status" -UseBasicParsing -TimeoutSec 2
}
function Start-FixtureApp([string]$Data,[int]$Port) {
    Assert-FixturePath $Data
    $oldData = $env:SCHOOL_DATA_DIR
    $oldEnvironment = $env:ASPNETCORE_ENVIRONMENT
    $oldSpace = $env:School__Backups__MinimumFreeBytes
    $env:SCHOOL_DATA_DIR = $Data
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:School__Backups__MinimumFreeBytes = '0'
    try {
        $log = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
        $arguments = @{
            FilePath=$program; WorkingDirectory=$app; ArgumentList=@('--urls',"http://127.0.0.1:$Port")
            PassThru=$true; WindowStyle='Hidden'; RedirectStandardOutput=($log + '-out.log'); RedirectStandardError=($log + '-error.log')
        }
        $instance = Start-Process @arguments
    } finally {
        $env:SCHOOL_DATA_DIR = $oldData
        $env:ASPNETCORE_ENVIRONMENT = $oldEnvironment
        $env:School__Backups__MinimumFreeBytes = $oldSpace
    }
    try {
        for ($attempt = 0; $attempt -lt 80; $attempt++) {
            if ($instance.HasExited) { throw "Fixture executable exited early: $($instance.ExitCode)" }
            try { $status = Get-SchoolStatus $Port } catch { Start-Sleep -Milliseconds 100; continue }
            if ($status.version -eq $publishedVersion) { return $instance }
            throw 'The fixture port is occupied by another application.'
        }
        throw 'Fixture executable did not start on loopback.'
    } catch {
        Stop-FixtureApp $instance
        throw
    }
}
function Stop-FixtureApp($Instance) {
    if ($Instance -and -not $Instance.HasExited) {
        Assert-True ($Instance.Path -eq $program) 'The test refused to stop an unrelated process.'
        Stop-Process -InputObject $Instance
        if (-not $Instance.WaitForExit(8000)) { throw 'Fixture executable did not stop.' }
    }
}
function Get-DatasetHashes([string]$Data) {
    Assert-FixturePath $Data
    foreach ($name in @('school.db','school.db-wal','school.db-shm')) {
        $file = Join-Path $Data $name
        if (Test-Path -LiteralPath $file -PathType Leaf) { "$name $((Get-FileHash -LiteralPath $file).Hash)" }
    }
    foreach ($name in @('keys','course-files','case-evidence')) {
        $directory = Join-Path $Data $name
        if (Test-Path -LiteralPath $directory -PathType Container) {
            foreach ($file in @(Get-ChildItem -LiteralPath $directory -File -Recurse | Sort-Object FullName)) {
                "$($file.FullName.Substring($Data.Length + 1)) $((Get-FileHash -LiteralPath $file.FullName).Hash)"
            }
        }
    }
}
function Assert-EqualDataset($Expected,$Actual) {
    Assert-True ($Expected.Count -gt 1) 'A real database, credential key, and business files were expected in the fixture.'
    Assert-True (($Expected -join [Environment]::NewLine) -eq ($Actual -join [Environment]::NewLine)) 'Original SQLite, sidecars, key-ring, or business-file bytes changed.'
}
function Assert-BusinessFiles([string]$Data,[string]$Label) {
    $courseFile = Join-Path (Join-Path $Data 'course-files\fixture-resource') 'attachment.txt'
    $evidenceFile = Join-Path (Join-Path $Data 'case-evidence\fixture-session') 'evidence.png'
    Assert-True ((Test-Path -LiteralPath $courseFile -PathType Leaf) -and [IO.File]::ReadAllText($courseFile) -eq "$Label attachment") 'Course attachment was not restored.'
    Assert-True ((Test-Path -LiteralPath $evidenceFile -PathType Leaf) -and [IO.File]::ReadAllText($evidenceFile) -eq "$Label evidence") 'Case evidence was not restored.'
}
function New-FixtureBackup([string]$Name,[string]$Label) {
    $data = Join-Path $fixtureRoot $Name
    New-Item -ItemType Directory -Path $data | Out-Null
    $port = Get-FreePort
    $base = "http://127.0.0.1:$port"
    $instance = Start-FixtureApp $data $port
    try {
        $setup = @{ schoolName=$Label; userName='fixtureadmin'; displayName='Fixture Admin'; password=('Test9!' + [Guid]::NewGuid().ToString('N')) }
        $request = @{ Method='Post'; ContentType='application/json'; UseBasicParsing=$true }
        $login = Invoke-RestMethod @request -Uri "$base/api/setup" -Headers @{ Origin=$base } -Body ($setup | ConvertTo-Json) -SessionVariable session
        $headers = @{ Origin=$base; 'X-CSRF-TOKEN'=$login.csrfToken }
        $cabinet = @{ name='Fixture Cabinet'; isSimulated=$true; jumpPassword=('Fixture9!' + [Guid]::NewGuid().ToString('N')) }
        $created = Invoke-RestMethod @request -Uri "$base/api/cabinets" -Headers $headers -WebSession $session -Body ($cabinet | ConvertTo-Json)
        Assert-True $created.hasJumpPassword 'Fixture credential was not stored.'
        foreach ($item in @(
            @{ path = Join-Path (Join-Path $data 'course-files\fixture-resource') 'attachment.txt'; content = "$Label attachment" },
            @{ path = Join-Path (Join-Path $data 'case-evidence\fixture-session') 'evidence.png'; content = "$Label evidence" }
        )) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $item.path) -Force | Out-Null
            [IO.File]::WriteAllText($item.path, $item.content)
        }
        $backup = Invoke-RestMethod -Uri "$base/api/backups" -Method Post -Headers $headers -WebSession $session -UseBasicParsing
        $file = Join-Path $fixtureRoot ($Name + '.zip')
        Invoke-WebRequest -Uri "$base/api/backups/$($backup.fileName)" -WebSession $session -OutFile $file -UseBasicParsing
        return $file
    } finally { Stop-FixtureApp $instance }
}
function Test-ClosedDatabase([string]$Data) {
    Assert-FixturePath $Data
    & $program '--validate-backup' (Join-Path $Data 'school.db') (Join-Path $Data 'keys') | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) 'Restored real SQLite and credential key-ring did not validate.'
}
function Test-Restore([string]$Scenario,[string]$OriginalArchive,[string]$BackupArchive) {
    $data = Join-Path (Join-Path $fixtureRoot $Scenario) 'data'
    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($OriginalArchive,$data)
    $untouchedLog = Join-Path $data 'prior-log.txt'
    [IO.File]::WriteAllText($untouchedLog,'original log retained')
    $port = Get-FreePort
    [IO.File]::WriteAllText((Join-Path $data 'installation.json'),(@{ port=$port } | ConvertTo-Json))
    $status = if ($Scenario -eq 'previously-stopped') { 'Stopped' } else { 'Running' }
    $service = [pscustomobject]@{ Status=$status } | Add-Member -PassThru ScriptMethod WaitForStatus {
        param($Expected,$Timeout)
        if ($this.Status -ne $Expected) { throw "Fixture service was expected to be $Expected." }
    }
    $state = @{
        data=$data; scenario=$Scenario; port=$port; service=$service; running=$null; startCount=0; stopCount=0; probeCount=0
        originalItems=@(Get-DatasetHashes $data); moveFailed=$false; restoredOriginalBeforeRestart=$false
    }
    $selectedHash = (Get-FileHash -LiteralPath $BackupArchive).Hash
    $externalLock = $null
    try {
        if ($status -eq 'Running') { $state.running = Start-FixtureApp $data $port }
        if ($Scenario -eq 'lock-held') {
            $externalLock = [IO.File]::Open((Join-Path $data '.restore-lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        }
        $failure = $null
        $messages = New-Object 'Collections.Generic.List[string]'
        try { & $fixtureRestore -BackupFile $BackupArchive -DataDirectory $data -ProgramFile $program | ForEach-Object { $messages.Add([string]$_) } }
        catch { $failure = $_.Exception.Message }
        if ($externalLock) { $externalLock.Dispose(); $externalLock = $null }
        Assert-True ((Get-FileHash -LiteralPath $BackupArchive).Hash -eq $selectedHash) 'The source archive changed.'
        Assert-True ([IO.File]::ReadAllText($untouchedLog) -eq 'original log retained') 'The original log changed.'
        Assert-True (@(Get-ChildItem -LiteralPath $data -Filter 'restore-staging-*').Count -eq 0) 'Staged data remained after restore.'
        $snapshots = @(Get-ChildItem -LiteralPath $data -Directory -Filter 'restore-rollback-*')
        if ($Scenario -eq 'lock-held') {
            Assert-True ($failure -like '*Cannot acquire the data directory restore lock*') "Unexpected result: $failure"
            Assert-True ($snapshots.Count -eq 0 -and $state.startCount -eq 0 -and $state.stopCount -eq 0) 'A blocked restore moved data or touched the service.'
            Assert-True ((Get-SchoolStatus $port).schoolName -eq 'Original school') 'Blocked restore changed the active database.'
            Assert-BusinessFiles $data 'Original school'
            return
        }
        Assert-True ($snapshots.Count -eq 1) "The restore did not create exactly one recovery directory. Error: $failure"
        $snapshot = $snapshots[0].FullName
        if ($Scenario -in @('running-success','previously-stopped')) {
            Assert-True (-not $failure) "Unexpected restore failure: $failure"
            Assert-EqualDataset $state.originalItems @(Get-DatasetHashes $snapshot)
            Test-ClosedDatabase $snapshot
            Assert-BusinessFiles $data 'Backup school'
            if ($Scenario -eq 'previously-stopped') {
                Assert-True ($state.startCount -eq 0 -and $state.probeCount -eq 0 -and $state.service.Status -eq 'Stopped') 'A stopped service was started.'
                Test-ClosedDatabase $data
            } else {
                Assert-True ($state.startCount -eq 1 -and $state.probeCount -eq 1) 'Restored instance did not pass its real health check.'
                Assert-True ((Get-SchoolStatus $port).schoolName -eq 'Backup school') 'Restored SQLite data did not become active.'
            }
        } elseif ($Scenario -eq 'partial-move') {
            Assert-True ($failure -eq 'injected partial move failure') "Unexpected partial move result: $failure"
            Assert-True ($state.restoredOriginalBeforeRestart -and $state.startCount -eq 1 -and $state.probeCount -eq 1) 'Partial move was not reversed before restarting the original instance.'
            Assert-True ((Get-SchoolStatus $port).schoolName -eq 'Original school') 'Partial move rollback did not restore the original instance.'
            Assert-BusinessFiles $data 'Original school'
        } else {
            Assert-True ($state.restoredOriginalBeforeRestart -and $state.startCount -eq 2 -and $state.probeCount -eq 2) "The original instance was not restored and health checked. Error: $failure"
            Assert-True ((Get-SchoolStatus $port).schoolName -eq 'Original school') 'Rollback did not reactivate the original SQLite data.'
            Assert-True ((Test-Path -LiteralPath (Join-Path $snapshot 'failed-restored-school.db')) -and (Test-Path -LiteralPath (Join-Path $snapshot 'failed-restored-keys'))) 'The failed replacement state was not retained.'
            if ($Scenario -eq 'both-unhealthy') {
                Assert-True ($failure -like '*injected replacement health failure*Rollback could not be verified*injected rollback health failure*') "Rollback failure did not report both errors: $failure"
            } else {
                Assert-True ($failure -eq 'injected replacement health failure') "Unexpected replacement failure result: $failure"
                Assert-True ($messages -contains 'Original service restarted and passed its live status check.') 'Successful rollback was not reported.'
                Assert-BusinessFiles $data 'Original school'
            }
        }
    } finally {
        if ($externalLock) { $externalLock.Dispose() }
        Stop-FixtureApp $state.running
    }
}

$originalArchive = New-FixtureBackup 'source-original' 'Original school'
$backupArchive = New-FixtureBackup 'source-replacement' 'Backup school'
$checks = New-Object 'Collections.Generic.List[object]'
function Test-ExactCopy([string]$Content,[int]$Length,[string]$ExpectedError) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Content)
    $inputStream = [IO.MemoryStream]::new($bytes)
    $outputStream = [IO.MemoryStream]::new()
    try {
        $failure = $null
        try { Copy-ExactBackupData $inputStream $outputStream $Length (New-Object byte[] 3) }
        catch { $failure = $_.Exception.Message }
        Assert-True ($failure -eq $ExpectedError -or (-not $failure -and -not $ExpectedError)) "Unexpected copy result: $failure"
        $expectedContent = $Content.Substring(0,[Math]::Min($Length,$Content.Length))
        Assert-True ([Text.Encoding]::UTF8.GetString($outputStream.ToArray()) -ceq $expectedContent) 'Copy wrote unexpected bytes.'
    } finally {
        $inputStream.Dispose()
        $outputStream.Dispose()
    }
}

# Load only the production stream helper. The boundary checks use tiny,
# ordinary memory streams on both PowerShell runtimes.
$tokens = $null
$parseErrors = $null
$restoreAst = [Management.Automation.Language.Parser]::ParseFile($restoreScript,[ref]$tokens,[ref]$parseErrors)
$copyFunction = $restoreAst.Find({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Copy-ExactBackupData'
},$false)
if ($parseErrors.Count -or -not $copyFunction) { throw 'Cannot load the production backup copy helper.' }
. ([scriptblock]::Create($copyFunction.Extent.Text))
$copyCases = @(
    @{name='exact-copy-empty'; text=''; length=0; error=''},
    @{name='exact-copy-multiple-reads'; text='ordinary content'; length=16; error=''},
    @{name='exact-copy-short-stream'; text='sample'; length=7; error='Backup entry ended before its declared size.'},
    @{name='exact-copy-over-budget'; text='ordinary content'; length=10; error='Backup entry exceeds its declared size.'},
    @{name='exact-copy-zero-budget'; text='x'; length=0; error='Backup entry exceeds its declared size.'}
)
foreach ($case in $copyCases) {
    try {
        Test-ExactCopy $case.text $case.length $case.error
        $checks.Add([ordered]@{name=$case.name;passed=$true})
        Write-Output "PASS $($case.name)"
    } catch {
        $checks.Add([ordered]@{name=$case.name;passed=$false;error=$_.Exception.Message})
        Write-Output "FAIL $($case.name) - $($_.Exception.Message)"
    }
}
foreach ($scenario in @('running-success','previously-stopped','replacement-unhealthy','both-unhealthy','partial-move','lock-held')) {
    try {
        Test-Restore $scenario $originalArchive $backupArchive
        $checks.Add([ordered]@{name=$scenario;passed=$true})
        Write-Output "PASS $scenario"
    } catch {
        $checks.Add([ordered]@{name=$scenario;passed=$false;error=$_.Exception.Message})
        Write-Output "FAIL $scenario - $($_.Exception.Message)"
    }
}
$passed = @($checks | Where-Object { $_.passed }).Count
$reportPath = [IO.Path]::GetFullPath((Join-Path $root $ReportFile))
if (-not $reportPath.StartsWith((Join-Path $root 'docs\verification') + '\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report must stay inside docs/verification.'
}
$report = [ordered]@{
    suite='deployment-restore-injected'; recordedAtUtc=[DateTime]::UtcNow.ToString('o'); powershellVersion=$PSVersionTable.PSVersion.ToString()
    testedApplicationVersion=$publishedVersion; testedAssemblySha256=(Get-FileHash -LiteralPath $assembly).Hash.ToLowerInvariant()
    restoreScriptSha256=(Get-FileHash -LiteralPath $restoreScript).Hash.ToLowerInvariant(); commonScriptSha256=(Get-FileHash -LiteralPath $commonScript).Hash.ToLowerInvariant()
    scope='Five ordinary memory-stream checks use the production copy routine. Six workflow checks use the published app on loopback with two synthetic SQLite databases, DPAPI key-rings, course attachments, case evidence, and actual backup ZIPs. SCM and registration are test doubles. No elevated operations, real service changes, or school data.'
    passed=($passed -eq $checks.Count); passedChecks=$passed; totalChecks=$checks.Count; checks=$checks.ToArray()
}
[IO.File]::WriteAllText($reportPath,($report | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
if ($passed -ne $checks.Count) { exit 1 }
