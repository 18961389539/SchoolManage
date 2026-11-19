[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$RuntimeVersion = '10.0.15',
    [switch]$SkipFrontend,
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') { throw 'Version must be a semantic version.' }
if ($RuntimeVersion -notmatch '^10\.\d+\.\d+$') { throw 'RuntimeVersion must be a supported .NET 10 patch version.' }
$releaseRoot = Join-Path $projectRoot 'artifacts'
$releaseDir = Join-Path $releaseRoot "SchoolTraining-$Version-win-x64"
$publishDir = Join-Path $releaseDir 'app'
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

# -SkipFrontend reuses an existing web/dist build; format/lint/unit-test gates for the
# frontend run only on the full path, so run them separately when skipping the rebuild.
if (-not $SkipFrontend) {
    Push-Location (Join-Path $projectRoot 'web')
    try {
        if (Test-Path -LiteralPath 'package-lock.json') { Invoke-Checked 'npm.cmd' @('ci', '--no-audit', '--no-fund') }
        else { Invoke-Checked 'npm.cmd' @('install', '--no-audit', '--no-fund') }
        Invoke-Checked 'npm.cmd' @('run', 'format:check')
        Invoke-Checked 'npm.cmd' @('run', 'lint')
        Invoke-Checked 'npm.cmd' @('run', 'test')
        Invoke-Checked 'npm.cmd' @('run', 'build')
    } finally { Pop-Location }
}
$frontendDist = Join-Path $projectRoot 'web/dist'
if (-not (Test-Path -LiteralPath (Join-Path $frontendDist 'index.html'))) { throw 'Frontend build missing: web/dist/index.html' }
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
$gate = [ordered]@{
    status = 'running'; version = $Version; testedAssembly = $testedAssembly; testedAssemblySha256 = $hashBefore
    startedAtUtc = [DateTime]::UtcNow.ToString('o'); completedAtUtc = $null; checks = @(); boundReports = @(); error = $null
}
$env:SCHOOL_APP_DIR = $publishDir
$env:SCHOOL_EXCEL_TEST_APP = $publishDir
$env:SCHOOL_NODE_ACCOUNT_SOURCE_DIR = $publishDir
$env:SCHOOL_LOAD_SOURCE_DIR = $publishDir
$gateError = $null
try {
    Invoke-Checked 'dotnet' @('build', (Join-Path $projectRoot 'tools/School.SecurityChecks/School.SecurityChecks.csproj'), '-c', 'Release', '--nologo')
    $gateTests = @(
        @{name='csharp-format'; command='dotnet'; args=@('format', (Join-Path $projectRoot 'src/School.Training/School.Training.csproj'), '--verify-no-changes', '--verbosity', 'minimal')},
        @{name='csharp-format-tests'; command='dotnet'; args=@('format', (Join-Path $projectRoot 'tests/School.Training.Tests/School.Training.Tests.csproj'), '--verify-no-changes', '--verbosity', 'minimal')},
        @{name='unit-tests'; command='dotnet'; args=@('test', (Join-Path $projectRoot 'tests/School.Training.Tests/School.Training.Tests.csproj'), '-c', 'Release', '--nologo', '-v', 'minimal')},
        @{name='schema-migration'; command='node'; args=@('tests/migration-smoke.mjs')},
        @{name='application'; command='node'; args=@('tests/application-smoke.mjs')},
        @{name='excel-csv'; command='node'; args=@('tests/excel-smoke.mjs')},
        @{name='node-accounts'; command='node'; args=@('tests/node-accounts-smoke.mjs')},
        @{name='infrastructure'; command='node'; args=@('tests/infrastructure-selfhost.mjs')},
        @{name='load'; command='node'; args=@('tests/load-smoke.mjs')},
        @{name='security'; command='dotnet'; args=@('run','--project',(Join-Path $projectRoot 'tools/School.SecurityChecks/School.SecurityChecks.csproj'),'-c','Release','--no-build')},
        @{name='deployment-policy'; command='powershell.exe'; args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $projectRoot 'tests/deployment-policy.ps1'))}
    )
    foreach ($test in $gateTests) {
        $started = [DateTime]::UtcNow
        Push-Location $projectRoot
        try {
            & $test.command @($test.args)
            $exitCode = $LASTEXITCODE
            if ($null -eq $exitCode) { $exitCode = 0 }
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
        @{name='load'; file='load-smoke.json'; property='testedAssemblySha256'}
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
foreach ($folder in @('scripts', 'docs')) {
    New-Item -ItemType Directory -Path (Join-Path $releaseDir $folder) -Force | Out-Null
}
foreach ($name in @('deployment-common.ps1','install-service.ps1', 'uninstall-service.ps1', 'restore-backup.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $releaseDir 'scripts') -Force
}
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -Filter '*.md' | Copy-Item -Destination (Join-Path $releaseDir 'docs') -Force
if (Test-Path -LiteralPath (Join-Path $projectRoot 'docs/verification')) { Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/verification') -Destination (Join-Path $releaseDir 'docs/verification') -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $releaseDir -Force
& (Join-Path $PSScriptRoot 'collect-dependencies.ps1') -Destination (Join-Path $releaseDir 'docs/third-party')
# Install the same notices and operator documentation with the application.
New-Item -ItemType Directory -Path (Join-Path $publishDir 'docs') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $releaseDir 'docs') -Force | Copy-Item -Destination (Join-Path $publishDir 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $releaseDir 'README.md') -Destination $publishDir -Force
$metadata = [ordered]@{ product = 'SchoolTraining'; version = $Version; runtime = 'win-x64'; runtimeFrameworkVersion = $RuntimeVersion; selfContained = $true; builtAtUtc = [DateTime]::UtcNow.ToString('o') }
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseDir 'release.json') -Encoding UTF8
$forbidden=@(Get-ChildItem -LiteralPath $releaseDir -File -Recurse | Where-Object { $_.Name -match '(?i)(^school\.db($|-)|^issuer-private|\.pfx$|\.key$|^server\.json$|^\.schooltraining-data\.json$)' })
if($forbidden.Count -gt 0) { throw 'Release contains runtime data, certificates or private signing material; refusing packaging.' }
$archive = Join-Path $releaseRoot "SchoolTraining-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $releaseDir '*') -DestinationPath $archive -Force

if (-not $SkipInstaller) {
    $installerOutput = Join-Path $releaseRoot "installer-$Version"
    Invoke-Checked 'dotnet' @('publish', (Join-Path $projectRoot 'installer/School.Training.Setup/School.Training.Setup.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', "-p:Version=$Version", "-p:RuntimeFrameworkVersion=$RuntimeVersion", "-p:PackagePayload=$archive", '-o', $installerOutput)
    $setupPath = Join-Path $releaseRoot "SchoolTraining-$Version-Setup.exe"
    Copy-Item -LiteralPath (Join-Path $installerOutput 'School.Training.Setup.exe') -Destination $setupPath -Force
}
$hashTargets = @($archive)
if (-not $SkipInstaller) { $hashTargets += $setupPath }
$lines = foreach ($file in $hashTargets) { $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256; "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))" }
$lines | Set-Content -LiteralPath (Join-Path $releaseRoot "SchoolTraining-$Version-SHA256SUMS.txt") -Encoding ASCII
Write-Output "Release ready: $releaseDir"
Write-Output "Archive: $archive"
if (-not $SkipInstaller) { Write-Output "Installer: $setupPath" }
exit 0
