[CmdletBinding()]
param([string]$ReportFile = 'docs/verification/deployment-safety.json')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$common=Join-Path $projectRoot 'scripts/deployment-common.ps1'
. $common
$started=[DateTime]::UtcNow
$fixtureRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot ('.local/deployment-safety-' + [Guid]::NewGuid().ToString('N'))))
if (-not $fixtureRoot.StartsWith((Join-Path $projectRoot '.local') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture location.' }
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$checks=New-Object Collections.Generic.List[object]
$junction=$null

function Assert-True([bool]$Condition,[string]$Message) { if(-not $Condition) { throw $Message } }
function Assert-Rejected([scriptblock]$Action) {
    $rejected=$false
    try { $null=& $Action } catch { $rejected=$true }
    Assert-True $rejected 'The unsafe operation was accepted.'
}
function Run-Check([string]$Name,[scriptblock]$Action) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    try {
        $null=& $Action
        $checks.Add([ordered]@{name=$Name;passed=$true;durationMs=$timer.ElapsedMilliseconds})
        Write-Output "PASS $Name"
    } catch {
        $checks.Add([ordered]@{name=$Name;passed=$false;durationMs=$timer.ElapsedMilliseconds;error=$_.Exception.Message})
        Write-Output "FAIL $Name`: $($_.Exception.Message)"
    }
}
function New-FixtureDirectory([string]$Name) {
    $path=Join-Path $fixtureRoot $Name
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}
function Write-FixtureFile([string]$Root,[string]$Relative,[string]$Content) {
    $path=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if(-not $path.StartsWith($fixtureRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture file escaped isolated directory.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path,$Content)
    return $path
}
function New-OwnedInstall([string]$Name,[hashtable]$Payload=@{'School.Training.exe'='fixture executable'}) {
    $path=New-FixtureDirectory $Name
    $files=New-Object Collections.Generic.List[object]
    foreach($entry in $Payload.GetEnumerator()) {
        $file=Write-FixtureFile $path $entry.Key $entry.Value
        $files.Add([ordered]@{path=$entry.Key;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
    $manifest=[ordered]@{product='SchoolTraining';schemaVersion=1;state='installed';files=$files.ToArray()}
    [IO.File]::WriteAllText((Join-Path $path '.schooltraining-install.json'),($manifest | ConvertTo-Json -Depth 8))
    return [pscustomobject]@{path=$path;manifest=(Get-VerifiedInstallManifest $path)}
}
function New-Plan([string]$Relative,[string]$Content) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Content))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [pscustomobject]@{path=$Relative;sha256=$hash}
}

try {
    Run-Check 'Empty installation directory is accepted without mutation' {
        $dir=New-FixtureDirectory 'empty-install'
        $manifest=Get-VerifiedInstallManifest $dir -AllowEmpty
        Assert-True ($null -eq $manifest) 'An empty directory unexpectedly produced an installation manifest.'
        Assert-True (@(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0) 'Guard mutated an empty directory.'
    }
    Run-Check 'Unknown nonempty program directory is refused and unchanged' {
        $dir=New-FixtureDirectory 'unknown-install'
        $file=Write-FixtureFile $dir 'customer-document.txt' 'customer owned'
        $hash=(Get-FileHash -LiteralPath $file).Hash; $acl=(Get-Acl -LiteralPath $dir).Sddl
        Assert-Rejected { Get-VerifiedInstallManifest $dir -AllowEmpty }
        Assert-True ((Get-FileHash -LiteralPath $file).Hash -eq $hash) 'Unknown file changed.'
        Assert-True ((Get-Acl -LiteralPath $dir).Sddl -eq $acl) 'Guard changed directory ACL.'
    }
    Run-Check 'Unknown nonempty data directory is refused and keys are preserved' {
        $dir=New-FixtureDirectory 'unknown-data'
        $file=Write-FixtureFile $dir 'keys\customer-key.txt' 'not application data'
        $hash=(Get-FileHash -LiteralPath $file).Hash
        Assert-Rejected { Assert-OwnedDataDirectory $dir -AllowEmpty }
        Assert-True ((Get-FileHash -LiteralPath $file).Hash -eq $hash) 'Unknown key data changed.'
    }
    Run-Check 'Valid product data marker is accepted' {
        $dir=New-FixtureDirectory 'owned-data'
        $null=Write-FixtureFile $dir '.schooltraining-data.json' '{"product":"SchoolTraining","schemaVersion":1}'
        Assert-OwnedDataDirectory $dir
    }
    Run-Check 'Wrong product data marker is refused' {
        $dir=New-FixtureDirectory 'wrong-data-marker'
        $null=Write-FixtureFile $dir '.schooltraining-data.json' '{"product":"OtherProduct","schemaVersion":1}'
        Assert-Rejected { Assert-OwnedDataDirectory $dir }
    }
    Run-Check 'Drive root and Windows system roots are refused' {
        foreach($path in @([IO.Path]::GetPathRoot($projectRoot),$env:windir,(Join-Path $env:windir 'SchoolTraining'),$env:ProgramFiles,$env:ProgramData)) { Assert-Rejected { Get-CheckedDirectory $path } }
    }
    Run-Check 'Traversal, absolute, UNC and alternate-stream relative paths are refused' {
        $dir=New-FixtureDirectory 'path-guards'
        foreach($relative in @('..\escape.txt','assets/../../escape.txt','C:\escape.txt','\\server\share\file','payload.txt:stream','..')) { Assert-Rejected { Get-SafeProductChild $dir $relative } }
        $safe=Get-SafeProductChild $dir 'wwwroot\assets\main.js'
        Assert-True ($safe.StartsWith($dir + '\',[StringComparison]::OrdinalIgnoreCase)) 'Safe child was resolved outside the root.'
    }
    Run-Check 'A manifest with an escaping file path is refused' {
        $owned=New-OwnedInstall 'malicious-manifest'
        $files=@($owned.manifest.files) + @(New-Plan '..\outside.txt' 'unexpected')
        $manifest=[ordered]@{product='SchoolTraining';schemaVersion=1;files=$files}
        [IO.File]::WriteAllText((Join-Path $owned.path '.schooltraining-install.json'),($manifest | ConvertTo-Json -Depth 8))
        Assert-Rejected { Get-VerifiedInstallManifest $owned.path }
    }
    Run-Check 'Case-insensitive duplicate manifest paths are refused' {
        $owned=New-OwnedInstall 'duplicate-manifest'
        $duplicate=New-Plan 'school.training.EXE' 'same path'
        $manifest=[ordered]@{product='SchoolTraining';schemaVersion=1;files=@($owned.manifest.files) + @($duplicate)}
        [IO.File]::WriteAllText((Join-Path $owned.path '.schooltraining-install.json'),($manifest | ConvertTo-Json -Depth 8))
        Assert-Rejected { Get-VerifiedInstallManifest $owned.path }
    }
    Run-Check 'Uninstall removes unchanged payload but preserves modified and unknown files' {
        $owned=New-OwnedInstall 'uninstall-preserve' @{'School.Training.exe'='fixture executable';'wwwroot\main.js'='release code';'appsettings.Production.json'='release defaults'}
        $modified=Write-FixtureFile $owned.path 'appsettings.Production.json' 'school modified configuration'
        $unknown=Write-FixtureFile $owned.path 'user-documents\notes.txt' 'customer notes'
        $data=New-FixtureDirectory 'uninstall-independent-data'
        $db=Write-FixtureFile $data 'school.db' 'customer database sentinel'
        $modifiedHash=(Get-FileHash -LiteralPath $modified).Hash; $unknownHash=(Get-FileHash -LiteralPath $unknown).Hash; $dbHash=(Get-FileHash -LiteralPath $db).Hash
        $result=Remove-InstalledPayload $owned.path
        Assert-True ($result.removedFiles -eq 2 -and $result.modifiedFilesRetained -eq 1 -and $result.directoryRetained) 'Unexpected removal result.'
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $owned.path 'School.Training.exe'))) 'Unchanged payload executable was retained.'
        Assert-True ((Get-FileHash -LiteralPath $modified).Hash -eq $modifiedHash) 'Modified configuration was changed or removed.'
        Assert-True ((Get-FileHash -LiteralPath $unknown).Hash -eq $unknownHash) 'Unknown user file was changed or removed.'
        Assert-True ((Get-FileHash -LiteralPath $db).Hash -eq $dbHash) 'Independent persistent data was changed.'
    }
    Run-Check 'Uninstall removes an empty product tree without recursive deletion' {
        $owned=New-OwnedInstall 'uninstall-clean' @{'School.Training.exe'='fixture executable';'wwwroot\assets\main.js'='release code'}
        $result=Remove-InstalledPayload $owned.path
        Assert-True ($result.removedFiles -eq 2 -and -not $result.directoryRetained) 'Unexpected clean uninstall result.'
        Assert-True (-not (Test-Path -LiteralPath $owned.path)) 'Empty application tree remains.'
    }
    Run-Check 'Upgrade refuses to overwrite an unknown payload collision' {
        $owned=New-OwnedInstall 'upgrade-unknown'
        $unknown=Write-FixtureFile $owned.path 'new-library.dll' 'customer owned file'
        $hash=(Get-FileHash -LiteralPath $unknown).Hash
        $planned=@((New-Plan 'School.Training.exe' 'next executable'),(New-Plan 'new-library.dll' 'new release library'))
        Assert-Rejected { Assert-NoPayloadCollisions $owned.path $planned $owned.manifest }
        Assert-True ((Get-FileHash -LiteralPath $unknown).Hash -eq $hash) 'Collision guard changed unknown data.'
    }
    Run-Check 'Upgrade refuses to overwrite a modified installed payload file' {
        $owned=New-OwnedInstall 'upgrade-modified'
        $file=Write-FixtureFile $owned.path 'School.Training.exe' 'modified executable sentinel'
        $hash=(Get-FileHash -LiteralPath $file).Hash
        Assert-Rejected { Assert-NoPayloadCollisions $owned.path @((New-Plan 'School.Training.exe' 'next executable')) $owned.manifest }
        Assert-True ((Get-FileHash -LiteralPath $file).Hash -eq $hash) 'Collision guard changed modified data.'
    }
    Run-Check 'Upgrade accepts unchanged ownership and preserves owned production configuration' {
        $owned=New-OwnedInstall 'upgrade-owned-config' @{'School.Training.exe'='fixture executable';'appsettings.Production.json'='release defaults'}
        $config=Write-FixtureFile $owned.path 'appsettings.Production.json' 'school customization'
        $hash=(Get-FileHash -LiteralPath $config).Hash
        Assert-NoPayloadCollisions $owned.path @((New-Plan 'School.Training.exe' 'next executable'),(New-Plan 'appsettings.Production.json' 'next defaults')) $owned.manifest
        Assert-True ((Get-FileHash -LiteralPath $config).Hash -eq $hash) 'Production configuration changed during collision validation.'
    }
    Run-Check 'Upgrade does not exempt an unknown production configuration file' {
        $owned=New-OwnedInstall 'upgrade-unknown-config'
        $null=Write-FixtureFile $owned.path 'appsettings.Production.json' 'unknown customer configuration'
        Assert-Rejected { Assert-NoPayloadCollisions $owned.path @((New-Plan 'appsettings.Production.json' 'new defaults')) $owned.manifest }
    }
    Run-Check 'Upgrade refuses a release file that conflicts with an existing directory' {
        $owned=New-OwnedInstall 'upgrade-directory-collision'
        New-Item -ItemType Directory -Path (Join-Path $owned.path 'new-library.dll') -Force | Out-Null
        Assert-Rejected { Assert-NoPayloadCollisions $owned.path @((New-Plan 'new-library.dll' 'binary')) $owned.manifest }
    }
    $junctionTarget=New-FixtureDirectory 'junction-target'
    $sentinel=Write-FixtureFile $junctionTarget 'sentinel.txt' 'junction target must be untouched'
    $sentinelHash=(Get-FileHash -LiteralPath $sentinel).Hash
    $junction=Join-Path $fixtureRoot 'junction-alias'
    New-Item -ItemType Junction -Path $junction -Value $junctionTarget | Out-Null
    Run-Check 'Junction paths and descendants are refused' {
        Assert-Rejected { Get-CheckedDirectory $junction }
        Assert-Rejected { Get-CheckedDirectory (Join-Path $junction 'new-data') }
        Assert-Rejected { Get-SafeProductChild $fixtureRoot 'junction-alias\sentinel.txt' }
        Assert-True ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash) 'Junction target changed.'
    }
    Run-Check 'Source traversal refuses junctions without copying target data' {
        $destination=New-FixtureDirectory 'junction-copy-destination'
        Assert-Rejected { Copy-SafeTree $junction $destination }
        Assert-True (@(Get-ChildItem -LiteralPath $destination -Force).Count -eq 0) 'Source traversal copied through a junction.'
        Assert-True ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash) 'Source traversal changed junction target.'
    }
    Run-Check 'Upgrade path validation refuses junction-backed destinations' {
        Assert-Rejected { Assert-NoPayloadCollisions $fixtureRoot @((New-Plan 'junction-alias\sentinel.txt' 'replacement')) $null }
        Assert-True ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash) 'Upgrade touched a junction target.'
    }
} finally {
    if ($junction -and (Test-Path -LiteralPath $junction)) {
        $linkFull=[IO.Path]::GetFullPath($junction)
        if(-not $linkFull.StartsWith($fixtureRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe junction cleanup path.' }
        if(((Get-Item -LiteralPath $linkFull -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { throw 'Cleanup target is no longer a junction.' }
        # Nonrecursive Directory.Delete removes this known junction itself, not its target.
        [IO.Directory]::Delete($linkFull,$false)
        if(-not (Test-Path -LiteralPath $sentinel)) { throw 'Junction target sentinel disappeared during cleanup.' }
    }
}
$passed=@($checks | Where-Object { $_.passed }).Count
$reportPath=[IO.Path]::GetFullPath((Join-Path $projectRoot $ReportFile))
$reportRoot=Join-Path $projectRoot 'docs\verification'
if(-not $reportPath.StartsWith($reportRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Report must stay in docs/verification.' }
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$report=[ordered]@{
    suite='deployment-filesystem-safety'; startedAtUtc=$started.ToString('o'); completedAtUtc=[DateTime]::UtcNow.ToString('o');
    powershellVersion=$PSVersionTable.PSVersion.ToString(); commonScriptSha256=(Get-FileHash -LiteralPath $common -Algorithm SHA256).Hash;
    testScriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash; fixtureDirectory=$fixtureRoot;
    passed=($passed -eq $checks.Count); passedChecks=$passed; totalChecks=$checks.Count; checks=$checks.ToArray();
    scope='Isolated .local fixtures using deployment-common file/path functions only. No service registration, registry changes, ACL changes, firewall changes, real installation, reboot or production restoration.'
}
[IO.File]::WriteAllText($reportPath,($report | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output "Deployment filesystem safety: $passed/$($checks.Count) checks passed. Report: $reportPath"
if($passed -ne $checks.Count) { exit 1 }
