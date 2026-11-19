#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupFile,
    [string]$DataDirectory = (Join-Path $env:ProgramData 'SchoolTraining'),
    [ValidateSet('SchoolTraining')][string]$ServiceName = 'SchoolTraining',
    [string]$ProgramFile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'deployment-common.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$BackupFile = [IO.Path]::GetFullPath($BackupFile)
$registration = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SchoolTraining' -ErrorAction SilentlyContinue
if ($registration -and -not $PSBoundParameters.ContainsKey('DataDirectory')) { $DataDirectory = $registration.DataDirectory }
$DataDirectory = Get-CheckedDirectory $DataDirectory
if ($registration -and -not $DataDirectory.Equals([IO.Path]::GetFullPath($registration.DataDirectory).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'DataDirectory must match the registered service data directory.' }
if (-not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) { throw 'Backup archive does not exist.' }
if (-not (Test-Path -LiteralPath $DataDirectory -PathType Container)) { throw 'Existing data directory is required.' }
Assert-OwnedDataDirectory $DataDirectory
if (-not $ProgramFile) {
    if ($registration) { $ProgramFile=Join-Path $registration.InstallLocation 'School.Training.exe' }
    else {
        foreach($candidate in @((Join-Path $PSScriptRoot '..\School.Training.exe'),(Join-Path $PSScriptRoot '..\app\School.Training.exe'))) { if(Test-Path -LiteralPath $candidate -PathType Leaf) { $ProgramFile=$candidate; break } }
    }
}
if (-not $ProgramFile) { throw 'Specify -ProgramFile pointing to the matching School.Training.exe for database validation.' }
$ProgramFile=Assert-NoReparseInPath $ProgramFile
if (-not (Test-Path -LiteralPath $ProgramFile -PathType Leaf) -or [IO.Path]::GetFileName($ProgramFile) -ne 'School.Training.exe') { throw 'Matching product executable is required.' }
if ($DataDirectory -eq [IO.Path]::GetPathRoot($DataDirectory).TrimEnd('\')) { throw 'A data subdirectory is required.' }
function Get-SafeChild([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $DataDirectory $Relative))
    if (-not $path.StartsWith($DataDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe data path.' }
    return Assert-NoReparseInPath $path
}
$operationId = [Guid]::NewGuid().ToString('N')
$staging = Get-SafeChild "restore-staging-$operationId"
$rollback = Get-SafeChild ('restore-rollback-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$database = Get-SafeChild 'school.db'
$keys = Get-SafeChild 'keys'
$archive = [IO.Compression.ZipFile]::OpenRead($BackupFile)
try {
    if ($archive.Entries.Count -gt 5000) { throw 'Too many archive entries.' }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$totalSize = 0
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\','/')
        if ($name -match '(^/|:|(^|/)\.\.(/|$)|(^|/)\.(/|$))' -or -not ($name -eq 'school.db' -or $name -eq 'manifest.json' -or $name -eq '.schooltraining-data.json' -or $name.StartsWith('keys/'))) { throw "Unexpected or unsafe backup entry: $name" }
        if (-not $seen.Add($name.TrimEnd('/'))) { throw 'Duplicate backup entry.' }
        $totalSize += $entry.Length
        if ($entry.Length -gt 1GB -or $totalSize -gt 2GB) { throw 'Backup size limit exceeded.' }
        $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if ($unixMode -eq 0xA000) { throw 'Symbolic links are not allowed in backups.' }
    }
    if (-not $seen.Contains('school.db') -or -not $seen.Contains('manifest.json') -or -not $seen.Contains('.schooltraining-data.json')) { throw 'Backup requires school.db, manifest.json and product data marker.' }
} finally { $archive.Dispose() }
try {
New-Item -ItemType Directory -Path $staging -Force | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($BackupFile,$staging)
$manifest = Get-Content -LiteralPath (Join-Path $staging 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'SchoolTraining') { throw 'Unsupported backup product or schema version.' }
$dataMarker=Get-Content -LiteralPath (Join-Path $staging '.schooltraining-data.json') -Raw | ConvertFrom-Json
if ($dataMarker.product -ne 'SchoolTraining' -or $dataMarker.schemaVersion -ne 1) { throw 'Invalid backup data marker.' }
$stream = [IO.File]::OpenRead((Join-Path $staging 'school.db'))
try { $header = New-Object byte[] 16; if ($stream.Read($header,0,16) -ne 16 -or [Text.Encoding]::ASCII.GetString($header) -ne "SQLite format 3`0") { throw 'Backup is not a SQLite database.' } }
finally { $stream.Dispose() }
& $ProgramFile '--validate-backup' (Join-Path $staging 'school.db') (Join-Path $staging 'keys')
if ($LASTEXITCODE -ne 0) { throw 'Backup failed the product database integrity/schema validation. Existing data was not changed.' }
$null=@(Get-SafeFiles $staging)
} catch {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath (Get-SafeChild "restore-staging-$operationId") -Recurse -Force }
    throw
}
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $service -and $service.Status -ne 'Stopped'
$replacementStarted = $false
$originalDatabaseExists = Test-Path -LiteralPath $database
try {
    if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; (Get-Service -Name $ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) }
    New-Item -ItemType Directory -Path $rollback -Force | Out-Null
    if ($originalDatabaseExists) {
        $exclusive = [IO.File]::Open($database,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $exclusive.Dispose()
        Copy-Item -LiteralPath $database -Destination (Join-Path $rollback 'school.db') -Force
    }
    if (Test-Path -LiteralPath $keys) { Copy-SafeTree $keys (Join-Path $rollback 'keys') }
    $replacementStarted = $true
    foreach ($suffix in @('-wal','-shm')) { $sidecar = Get-SafeChild ('school.db' + $suffix); if (Test-Path -LiteralPath $sidecar) { Move-Item -LiteralPath $sidecar -Destination (Join-Path $rollback ('school.db' + $suffix)) -Force } }
    if (Test-Path -LiteralPath $keys) { Remove-Item -LiteralPath (Get-SafeChild 'keys') -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $staging 'school.db') -Destination $database -Force
    if (Test-Path -LiteralPath (Join-Path $staging 'keys')) { Move-Item -LiteralPath (Join-Path $staging 'keys') -Destination $keys }
    else { New-Item -ItemType Directory -Path $keys -Force | Out-Null }
    if ($wasRunning) {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
        Start-Sleep -Seconds 2
        if ((Get-Service -Name $ServiceName).Status -ne 'Running') { throw 'Service failed after restoration.' }
        Wait-ProductHealth $DataDirectory
    }
    Write-Output "Backup restored. Previous database and keys preserved at: $rollback"
    Write-Output 'Sessions may need a new login. Original backups and logs were not modified.'
} catch {
    $restoreFailure = $_
    if ($replacementStarted) {
        if ($service) { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue; (Get-Service -Name $ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) }
        if (Test-Path -LiteralPath $keys) { Move-Item -LiteralPath $keys -Destination (Join-Path $rollback 'failed-restored-keys') }
        foreach ($suffix in @('-wal','-shm')) { $currentSidecar = Get-SafeChild ('school.db' + $suffix); if (Test-Path -LiteralPath $currentSidecar) { Move-Item -LiteralPath $currentSidecar -Destination (Join-Path $rollback ('failed-restored-school.db' + $suffix)) -Force } }
        if (Test-Path -LiteralPath (Join-Path $rollback 'school.db')) { Copy-Item -LiteralPath (Join-Path $rollback 'school.db') -Destination $database -Force }
        elseif (-not $originalDatabaseExists -and (Test-Path -LiteralPath $database)) { Remove-Item -LiteralPath (Get-SafeChild 'school.db') -Force }
        if (Test-Path -LiteralPath (Join-Path $rollback 'keys')) { Copy-SafeTree (Join-Path $rollback 'keys') $keys }
        foreach ($suffix in @('-wal','-shm')) { $priorSidecar = Join-Path $rollback ('school.db' + $suffix); if (Test-Path -LiteralPath $priorSidecar) { Move-Item -LiteralPath $priorSidecar -Destination (Get-SafeChild ('school.db' + $suffix)) -Force } }
        if ($wasRunning) { Start-Service -Name $ServiceName -ErrorAction SilentlyContinue }
    }
    elseif ($wasRunning) {
        try { Start-Service -Name $ServiceName -ErrorAction Stop; (Get-Service -Name $ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30)); Wait-ProductHealth $DataDirectory }
        catch { Write-Warning 'Backup failed before replacing data, and the original service could not be restarted. Check the Windows service log.' }
    }
    throw $restoreFailure
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath (Get-SafeChild "restore-staging-$operationId") -Recurse -Force }
}
