[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$RuntimeVersion = '10.0.15',
    [string]$FrontendDirectory,
    [switch]$SkipFrontend,
    [switch]$SkipInstaller,
    [string]$SigningThumbprint,
    [string]$SigningPfx,
    [switch]$SigningMachineStore,
    [switch]$RequireSigning,
    [switch]$AllowUnsigned,
    [string]$SignToolPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$frontendRoot = if ($FrontendDirectory) { (Resolve-Path -LiteralPath $FrontendDirectory).Path } else { Join-Path $projectRoot 'web' }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') { throw 'Version must be a semantic version.' }
if ($RuntimeVersion -notmatch '^10\.\d+\.\d+$') { throw 'RuntimeVersion must be a supported .NET 10 patch version.' }
$releaseRoot = Join-Path $projectRoot 'artifacts'
$releaseDir = Join-Path $releaseRoot "SchoolTraining-$Version-win-x64"
# A reused output directory can silently carry files and evidence from an earlier build.
if (Test-Path -LiteralPath $releaseDir) {
    $releaseDirFull = [IO.Path]::GetFullPath($releaseDir)
    if (-not $releaseDirFull.StartsWith([IO.Path]::GetFullPath($releaseRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe release directory.' }
    Remove-Item -LiteralPath $releaseDirFull -Recurse -Force
}
$publishDir = Join-Path $releaseDir 'app'
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

function Get-GitMetadata {
    $git = Get-Command git.exe -ErrorAction SilentlyContinue
    if (-not $git) { return [ordered]@{ commit = 'unknown'; workTreeDirty = $null } }
    $commit = [string](& $git.Source -C $projectRoot rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) { return [ordered]@{ commit = 'unknown'; workTreeDirty = $null } }
    $status = @(& $git.Source -C $projectRoot status --porcelain 2>$null)
    if ($LASTEXITCODE -ne 0) { return [ordered]@{ commit = $commit.Trim(); workTreeDirty = $null } }
    return [ordered]@{ commit = $commit.Trim(); workTreeDirty = ($status.Count -gt 0) }
}


$signingConfigured = -not [string]::IsNullOrWhiteSpace($SigningThumbprint) -or -not [string]::IsNullOrWhiteSpace($SigningPfx)
if ($SigningThumbprint -and $SigningPfx) { throw 'Choose either -SigningThumbprint or -SigningPfx, not both.' }
if ($RequireSigning -and $AllowUnsigned) { throw 'Choose either -RequireSigning or -AllowUnsigned.' }
# A packaged release (installer included) is signed by default; CI and local gate runs use
# -SkipInstaller or -AllowUnsigned explicitly, so an unsigned full build cannot happen by accident.
$signingRequired = $RequireSigning -or (-not $SkipInstaller -and -not $AllowUnsigned)
if ($signingRequired -and -not $signingConfigured) {
    throw 'A full release requires code signing. Pass -SigningThumbprint or -SigningPfx, or use -AllowUnsigned for a validation build.'
}
function Invoke-Signing([string[]]$SignFiles) {
    $arguments = @{ Files = $SignFiles; TimestampUrl = $TimestampUrl; Require = [bool]$signingRequired }
    if ($SignToolPath) { $arguments.SignToolPath = $SignToolPath }
    if ($SigningThumbprint) {
        $arguments.Thumbprint = $SigningThumbprint
        if ($SigningMachineStore) { $arguments.MachineStore = $true }
    } else { $arguments.PfxPath = $SigningPfx }
    & (Join-Path $PSScriptRoot 'sign-release.ps1') @arguments
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed with exit code $LASTEXITCODE." }
}

# -SkipFrontend reuses an existing frontend dist build; format/lint/unit-test gates for the
# frontend run only on the full path, so run them separately when skipping the rebuild.
if (-not $SkipFrontend) {
    Push-Location $frontendRoot
    try {
        if (Test-Path -LiteralPath 'package-lock.json') { Invoke-Checked 'npm.cmd' @('ci', '--no-audit', '--no-fund') }
        else { Invoke-Checked 'npm.cmd' @('install', '--no-audit', '--no-fund') }
        Invoke-Checked 'npm.cmd' @('run', 'format:check')
        Invoke-Checked 'npm.cmd' @('run', 'lint')
        Invoke-Checked 'npm.cmd' @('run', 'test')
        Invoke-Checked 'npm.cmd' @('run', 'build')
    } finally { Pop-Location }
}
$frontendDist = Join-Path $frontendRoot 'dist'
if (-not (Test-Path -LiteralPath (Join-Path $frontendDist 'index.html'))) { throw "Frontend build missing: $frontendDist/index.html" }
$wwwroot = Join-Path $projectRoot 'src/School.Training/wwwroot'
New-Item -ItemType Directory -Path $wwwroot -Force | Out-Null
# Remove only prior generated frontend files inside the verified project wwwroot.
$wwwrootFull = [IO.Path]::GetFullPath($wwwroot)
if (-not $wwwrootFull.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe wwwroot path.' }
Get-ChildItem -LiteralPath $wwwrootFull -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
Copy-Item -Path (Join-Path $frontendDist '*') -Destination $wwwroot -Recurse -Force
Invoke-Checked 'dotnet' @('publish', (Join-Path $projectRoot 'src/School.Training/School.Training.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', "-p:Version=$Version", "-p:RuntimeFrameworkVersion=$RuntimeVersion", '-o', $publishDir)

# Release gate: all suites run against the exact published output. A failure blocks packaging.
$gateDirectory = Join-Path $projectRoot 'docs/verification'
New-Item -ItemType Directory -Path $gateDirectory -Force | Out-Null
$gatePath = Join-Path $gateDirectory 'release-test-gate.json'
$testedAssembly = Join-Path $publishDir 'School.Training.dll'
$hashBefore = (Get-FileHash -LiteralPath $testedAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
$gitMetadata = Get-GitMetadata
$gate = [ordered]@{
    status = 'running'; version = $Version; testedAssembly = $testedAssembly; testedAssemblySha256 = $hashBefore
    commit = $gitMetadata.commit; workTreeDirty = $gitMetadata.workTreeDirty
    startedAtUtc = [DateTime]::UtcNow.ToString('o'); completedAtUtc = $null; checks = @(); boundReports = @(); retries = @(); error = $null
}
$env:SCHOOL_APP_DIR = $publishDir
$env:SCHOOL_EXCEL_TEST_APP = $publishDir
$env:SCHOOL_NODE_ACCOUNT_SOURCE_DIR = $publishDir
$env:SCHOOL_LOAD_SOURCE_DIR = $publishDir
$env:SCHOOL_LOAD_REPORT_PATH = Join-Path $gateDirectory 'load-smoke.json'
# Automatic backups run on their own daily schedule; a smoke suite starting near that time
# must not race the job or change the backup files it asserts on.
$env:School__Backups__Automatic__Enabled = 'false'
$gateError = $null
try {
    Invoke-Checked 'dotnet' @('build', (Join-Path $projectRoot 'tools/School.SecurityChecks/School.SecurityChecks.csproj'), '-c', 'Release', '--nologo')
    # The model snapshot must match the model; otherwise a schema change could ship without
    # a migration. The tool is pinned by dotnet-tools.json and used by the design host only.
    Invoke-Checked 'dotnet' @('tool', 'restore')
    $gateTests = @(
        @{name='ef-model'; command='dotnet'; args=@('ef','migrations','has-pending-model-changes','--project',(Join-Path $projectRoot 'src/School.Training'),'--startup-project',(Join-Path $projectRoot 'tools/School.Training.DesignHost'))},
        @{name='csharp-format'; command='dotnet'; args=@('format', (Join-Path $projectRoot 'src/School.Training/School.Training.csproj'), '--verify-no-changes', '--verbosity', 'minimal')},
        @{name='csharp-format-tests'; command='dotnet'; args=@('format', (Join-Path $projectRoot 'tests/School.Training.Tests/School.Training.Tests.csproj'), '--verify-no-changes', '--verbosity', 'minimal')},
        @{name='unit-tests'; command='dotnet'; args=@('test', (Join-Path $projectRoot 'tests/School.Training.Tests/School.Training.Tests.csproj'), '-c', 'Release', '--nologo', '-v', 'minimal')},
        @{name='schema-migration'; command='node'; args=@('tests/migration-smoke.mjs')},
        @{name='application'; command='node'; args=@('tests/application-smoke.mjs')},
        @{name='excel-csv'; command='node'; args=@('tests/excel-smoke.mjs')},
        @{name='node-accounts'; command='node'; args=@('tests/node-accounts-smoke.mjs')},
        @{name='infrastructure'; command='node'; args=@('tests/infrastructure-selfhost.mjs')},
        @{name='exam-practical'; command='node'; args=@('tests/exam-practical-smoke.mjs')},
        @{name='classroom-camp-practical'; command='node'; args=@('tests/classroom-case-work-smoke.mjs')},
        @{name='load'; command='node'; args=@('tests/load-smoke.mjs')},
        @{name='security'; command='dotnet'; args=@('run','--project',(Join-Path $projectRoot 'tools/School.SecurityChecks/School.SecurityChecks.csproj'),'-c','Release','--no-build')},
        @{name='deployment-policy'; command='powershell.exe'; args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $projectRoot 'tests/deployment-policy.ps1'))},
        @{name='deployment-rollback'; command='powershell.exe'; args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $projectRoot 'tests/deployment-rollback.ps1'))},
        @{name='deployment-restore'; command='powershell.exe'; args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $projectRoot 'tests/deployment-restore.ps1'))}
    )
    # Frontend checks live in the same gate as the backend suites: they are cheap and a
    # formatting, lint or unit regression must block packaging exactly like a failed smoke run.
    if (-not $SkipFrontend) {
        $gateTests += @(
            @{name='frontend-format'; command='npm.cmd'; args=@('--prefix',$frontendRoot,'run','format:check')},
            @{name='frontend-lint'; command='npm.cmd'; args=@('--prefix',$frontendRoot,'run','lint')},
            @{name='frontend-tests'; command='npm.cmd'; args=@('--prefix',$frontendRoot,'run','test')},
            @{name='frontend-time'; command='npm.cmd'; args=@('--prefix',$frontendRoot,'run','check:time')},
            @{name='frontend-sizes'; command='npm.cmd'; args=@('--prefix',$frontendRoot,'run','check:sizes')},
            @{name='frontend-api-contract'; command='npm.cmd'; args=@('--prefix',$frontendRoot,'run','check:api')}
        )
    }
    foreach ($test in $gateTests) {
        $started = [DateTime]::UtcNow
        Push-Location $projectRoot
        try {
            & $test.command @($test.args)
            $exitCode = $LASTEXITCODE
            if ($null -eq $exitCode) { $exitCode = 0 }
            # A Windows fatal exception (0xC0000409) in the test runner is environmental
            # rather than a business assertion failure. Retry that exact code once and keep
            # the event in the evidence; any normal nonzero exit still fails immediately.
            if ($exitCode -eq -1073740791 -or $exitCode -eq 3221226505) {
                Write-Warning "Windows fatal exception in $($test.name); retrying once."
                $gate.retries += [ordered]@{
                    name = $test.name; exitCode = $exitCode; atUtc = [DateTime]::UtcNow.ToString('o')
                }
                & $test.command @($test.args)
                $exitCode = $LASTEXITCODE
                if ($null -eq $exitCode) { $exitCode = 0 }
            }
        } catch {
            $exitCode = 1
            $gate['error'] = $_.Exception.Message
        } finally { Pop-Location }
        $gate.checks += [ordered]@{
            name = $test.name; command = "$($test.command) $($test.args -join ' ')"; exitCode = $exitCode
            passed = ($exitCode -eq 0); durationSeconds = [Math]::Round(([DateTime]::UtcNow - $started).TotalSeconds, 2)
        }
        if ($exitCode -ne 0) { throw "Release gate failed: $($test.name) exited with code $exitCode." }
    }
    $boundReports = @(
        @{name='schema-migration'; file='migration-smoke.json'; property='testedAssemblySha256'},
        @{name='application'; file='application-smoke.json'; property='testedAssemblySha256'},
        @{name='excel-csv'; file='excel-smoke.json'; property='backendSha256'},
        @{name='node-accounts'; file='node-accounts-smoke.json'; property='testedAssemblySha256'},
        @{name='infrastructure'; file='infrastructure-smoke.json'; property='testedAssemblySha256'},
        @{name='load'; file='load-smoke.json'; property='testedAssemblySha256'},
        @{name='deployment-restore'; file='deployment-restore.json'; property='testedAssemblySha256'}
    )
    foreach ($binding in $boundReports) {
        $reportPath = Join-Path $gateDirectory $binding.file
        if (-not (Test-Path -LiteralPath $reportPath)) { throw "Release gate evidence missing: $($binding.file)" }
        # Windows PowerShell 5.1 decodes BOM-less files with the ANSI code page, which
        # corrupts Chinese evidence. Read UTF-8 explicitly before parsing JSON.
        $report = [IO.File]::ReadAllText($reportPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $actual = [string]$report.PSObject.Properties[$binding.property].Value
        if ([string]::IsNullOrWhiteSpace($actual) -or $actual.ToLowerInvariant() -ne $hashBefore) {
            throw "Release gate evidence is not bound to the published assembly: $($binding.file)"
        }
        $gate.boundReports += [ordered]@{ name = $binding.name; file = $binding.file; sha256 = $actual.ToLowerInvariant() }
    }
    $hashAfter = (Get-FileHash -LiteralPath $testedAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hashAfter -ne $hashBefore) { throw 'Published assembly changed while release tests were running.' }
    $gate.status = 'passed'
} catch {
    $gateError = $_
    $gate.status = 'failed'
    if (-not $gate['error']) { $gate['error'] = $_.Exception.Message }
} finally {
    $gate.completedAtUtc = [DateTime]::UtcNow.ToString('o')
    $gate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $gatePath -Encoding UTF8
}
if ($gateError) { throw $gateError }
# Only reports produced by this gate run are packaged. Historical verification files stay in
# the repository for reference but must not travel with a different build.
$currentEvidence = @(
    [ordered]@{ name = 'release-test-gate.json'; check = $null },
    [ordered]@{ name = 'migration-smoke.json'; check = 'schema-migration' },
    [ordered]@{ name = 'application-smoke.json'; check = 'application' },
    [ordered]@{ name = 'excel-smoke.json'; check = 'excel-csv' },
    [ordered]@{ name = 'node-accounts-smoke.json'; check = 'node-accounts' },
    [ordered]@{ name = 'infrastructure-smoke.json'; check = 'infrastructure' },
    [ordered]@{ name = 'load-smoke.json'; check = 'load' },
    [ordered]@{ name = 'security-invariants.json'; check = 'security' },
    [ordered]@{ name = 'deployment-policy.json'; check = 'deployment-policy' },
    [ordered]@{ name = 'deployment-rollback.json'; check = 'deployment-rollback' },
    [ordered]@{ name = 'deployment-restore.json'; check = 'deployment-restore' }
)
$evidence = @()
foreach ($item in $currentEvidence) {
    $name = $item.name
    $path = Join-Path $gateDirectory $name
    if (-not (Test-Path -LiteralPath $path)) { throw "Release evidence missing: $name" }
    $status = 'passed'
    if ($item.check) {
        $matched = @($gate.checks | Where-Object { $_.name -eq $item.check })
        if ($matched.Count -eq 0) { throw "Release gate check missing: $($item.check)" }
        if (-not $matched[0].passed) { $status = 'failed' }
    }
    $evidence += [ordered]@{
        name = $name; check = $item.check; status = $status
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $path).Length
    }
}
$manifest = [ordered]@{
    product = 'SchoolTraining'; version = $Version; runtime = 'win-x64'; runtimeFrameworkVersion = $RuntimeVersion
    commit = $gitMetadata.commit; workTreeDirty = $gitMetadata.workTreeDirty
    builtAtUtc = [DateTime]::UtcNow.ToString('o'); testedAssemblySha256 = $hashBefore
    gateStatus = [string]$gate.status; gateChecks = $gate.checks.Count; evidence = $evidence
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $releaseDir 'release-manifest.json') -Encoding UTF8
foreach ($folder in @('scripts', 'docs')) {
    New-Item -ItemType Directory -Path (Join-Path $releaseDir $folder) -Force | Out-Null
}
foreach ($name in @('deployment-common.ps1','install-service.ps1', 'uninstall-service.ps1', 'restore-backup.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $releaseDir 'scripts') -Force
}
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -Filter '*.md' | Copy-Item -Destination (Join-Path $releaseDir 'docs') -Force
$releaseEvidence = Join-Path $releaseDir 'docs/verification'
New-Item -ItemType Directory -Path $releaseEvidence -Force | Out-Null
foreach ($item in $evidence) {
    Copy-Item -LiteralPath (Join-Path $gateDirectory $item.name) -Destination $releaseEvidence -Force
}
Copy-Item -LiteralPath (Join-Path $releaseDir 'release-manifest.json') -Destination $releaseEvidence -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $releaseDir -Force
& (Join-Path $PSScriptRoot 'collect-dependencies.ps1') -Destination (Join-Path $releaseDir 'docs/third-party') -FrontendDirectory $frontendRoot
# Install the same notices and operator documentation with the application.
New-Item -ItemType Directory -Path (Join-Path $publishDir 'docs') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $releaseDir 'docs') -Force | Copy-Item -Destination (Join-Path $publishDir 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $releaseDir 'README.md') -Destination $publishDir -Force
$metadata = [ordered]@{
    product = 'SchoolTraining'; version = $Version; runtime = 'win-x64'; runtimeFrameworkVersion = $RuntimeVersion
    selfContained = $true; builtAtUtc = [DateTime]::UtcNow.ToString('o')
    commit = $gitMetadata.commit; workTreeDirty = $gitMetadata.workTreeDirty
    testedAssemblySha256 = $hashBefore; gateStatus = [string]$gate.status
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseDir 'release.json') -Encoding UTF8
$forbidden=@(Get-ChildItem -LiteralPath $releaseDir -File -Recurse | Where-Object { $_.Name -match '(?i)(^school\.db($|-)|^issuer-private|\.pfx$|\.key$|^server\.json$|^\.schooltraining-data\.json$)' })
if($forbidden.Count -gt 0) { throw 'Release contains runtime data, certificates or private signing material; refusing packaging.' }
if ($signingConfigured) {
    $appExe = Join-Path $publishDir 'School.Training.exe'
    if (-not (Test-Path -LiteralPath $appExe)) { throw 'Published app executable is missing; cannot sign the release.' }
    Invoke-Signing @($appExe)
} else {
    Write-Warning 'No code-signing certificate configured; this validation build is unsigned.'
}
$archive = Join-Path $releaseRoot "SchoolTraining-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $archive -Force

if (-not $SkipInstaller) {
    $installerOutput = Join-Path $releaseRoot "installer-$Version"
    Invoke-Checked 'dotnet' @('publish', (Join-Path $projectRoot 'installer/School.Training.Setup/School.Training.Setup.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', "-p:Version=$Version", "-p:RuntimeFrameworkVersion=$RuntimeVersion", "-p:PackagePayload=$archive", '-o', $installerOutput)
    $setupPath = Join-Path $releaseRoot "SchoolTraining-$Version-Setup.exe"
    Copy-Item -LiteralPath (Join-Path $installerOutput 'School.Training.Setup.exe') -Destination $setupPath -Force
    if ($signingConfigured) { Invoke-Signing @($setupPath) }
}
$hashTargets = @($archive)
if (-not $SkipInstaller) { $hashTargets += $setupPath }
$artifactHashes = @()
$lines = foreach ($file in $hashTargets) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    $name = [IO.Path]::GetFileName($file)
    $artifactHashes += [ordered]@{ file = $name; sha256 = $hash.Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $file).Length }
    "$($hash.Hash.ToLowerInvariant())  $name"
}
$lines | Set-Content -LiteralPath (Join-Path $releaseRoot "SchoolTraining-$Version-SHA256SUMS.txt") -Encoding ASCII
# The in-package manifest cannot hash the archive that contains it; the sidecar can.
$manifest['artifacts'] = $artifactHashes
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $releaseRoot "SchoolTraining-$Version-manifest.json") -Encoding UTF8
Write-Output "Release ready: $releaseDir"
Write-Output "Archive: $archive"
if (-not $SkipInstaller) { Write-Output "Installer: $setupPath" }
exit 0
