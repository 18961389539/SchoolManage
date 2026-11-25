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
    return Get-SafeProductChild $DataDirectory $Relative
}
$operationId = [Guid]::NewGuid().ToString('N')
$staging = Get-SafeChild "restore-staging-$operationId"
$rollback = Get-SafeChild ('restore-rollback-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + $operationId)
$database = Get-SafeChild 'school.db'
$keys = Get-SafeChild 'keys'
$liveItems = @('school.db','school.db-wal','school.db-shm','keys')
$businessItems = @()
$originalsMoved = New-Object 'Collections.Generic.List[string]'
$installedItems = New-Object 'Collections.Generic.List[string]'
$restoreLock = $null

function Copy-ExactBackupData([IO.Stream]$Source,[IO.Stream]$Destination,[long]$Length,[byte[]]$Buffer) {
    $remaining = $Length
    while ($remaining -gt 0) {
        $count = $Source.Read($Buffer,0,[int][Math]::Min($remaining,[long]$Buffer.Length))
        if ($count -eq 0) { throw 'Backup entry ended before its declared size.' }
        $Destination.Write($Buffer,0,$count)
        $remaining -= $count
    }
    if ($Source.ReadByte() -ne -1) { throw 'Backup entry exceeds its declared size.' }
}

function Expand-CheckedBackup([IO.Compression.ZipArchive]$Archive) {
    if ($Archive.Entries.Count -gt 100000) { throw 'Too many archive entries.' }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [long]$totalSize = 0
    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName.Replace('\','/')
        if ($name -match '(^/|:|(^|/)\.\.(/|$)|(^|/)\.(/|$))' -or -not ($name -eq 'school.db' -or $name -eq 'manifest.json' -or $name -eq '.schooltraining-data.json' -or $name.StartsWith('keys/') -or $name.StartsWith('course-files/') -or $name.StartsWith('case-evidence/'))) { throw "Unexpected or unsafe backup entry: $name" }
        if (-not $seen.Add($name.TrimEnd('/'))) { throw 'Duplicate backup entry.' }
        $length = $entry.Length
        if ($length -lt 0 -or $length -gt 8GB -or $length -gt (20GB - $totalSize)) { throw 'Backup size limit exceeded.' }
        if ($name.EndsWith('/') -and $length -ne 0) { throw 'Backup directory entry must be empty.' }
        $totalSize += $length
        $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if ($unixMode -eq 0xA000) { throw 'Symbolic links are not allowed in backups.' }
    }
    if (-not $seen.Contains('school.db') -or -not $seen.Contains('manifest.json') -or -not $seen.Contains('.schooltraining-data.json')) { throw 'Backup requires school.db, manifest.json and product data marker.' }
    New-Item -ItemType Directory -Path $staging | Out-Null
    $buffer = New-Object byte[] (64KB)
    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName.Replace('\','/')
        $destination = Get-SafeProductChild $staging ($name.TrimEnd('/'))
        if ($name.EndsWith('/')) {
            $null = [IO.Directory]::CreateDirectory($destination)
            continue
        }
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        $sourceStream = $entry.Open()
        try {
            $destinationStream = [IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try {
                # Preflight caps the sum of these lengths, and the copy cannot write past any entry's length.
                Copy-ExactBackupData $sourceStream $destinationStream $entry.Length $buffer
            } finally { $destinationStream.Dispose() }
        } finally { $sourceStream.Dispose() }
        [IO.File]::SetLastWriteTimeUtc($destination,$entry.LastWriteTime.UtcDateTime)
    }
}
function Stop-RestoreService {
    $current = Get-Service -Name $ServiceName
    if ($current.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
    }
}
function Start-VerifiedRestoreService {
    if ((Get-Service -Name $ServiceName).Status -ne 'Running') {
        Start-Service -Name $ServiceName
        (Get-Service -Name $ServiceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
    }
    Wait-ProductHealth $DataDirectory $expectedVersion
    if ((Get-Service -Name $ServiceName).Status -ne 'Running') { throw 'Service stopped before restoration could be verified.' }
}

try {
    $archive = [IO.Compression.ZipFile]::OpenRead($BackupFile)
    try { Expand-CheckedBackup $archive } finally { $archive.Dispose() }
    $manifest = Get-Content -LiteralPath (Join-Path $staging 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne 'SchoolTraining') { throw 'Unsupported backup product or schema version.' }
    $dataMarker = Get-Content -LiteralPath (Join-Path $staging '.schooltraining-data.json') -Raw | ConvertFrom-Json
    if ($dataMarker.product -ne 'SchoolTraining' -or $dataMarker.schemaVersion -ne 1) { throw 'Invalid backup data marker.' }
    # Older backups do not carry business files. Leave the live directories untouched in that case.
    $businessItems = @(@('course-files','case-evidence') | Where-Object { Test-Path -LiteralPath (Join-Path $staging $_) -PathType Container })
    $liveItems += $businessItems
    $stream = [IO.File]::OpenRead((Join-Path $staging 'school.db'))
    try {
        $header = New-Object byte[] 16
        if ($stream.Read($header,0,16) -ne 16 -or [Text.Encoding]::ASCII.GetString($header) -ne "SQLite format 3`0") { throw 'Backup is not a SQLite database.' }
    } finally { $stream.Dispose() }
    & $ProgramFile '--validate-backup' (Join-Path $staging 'school.db') (Join-Path $staging 'keys')
    if ($LASTEXITCODE -ne 0) { throw 'Backup failed the product database integrity/schema validation. Existing data was not changed.' }
    $null = @(Get-SafeFiles $staging)

    try { $restoreLock = [IO.File]::Open((Get-SafeChild '.restore-lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch [IO.IOException] { throw 'Cannot acquire the data directory restore lock. Check for another restore operation or a storage error.' }
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and -not $registration) { throw 'Cannot verify the data path of the registered SchoolTraining service.' }
    $wasRunning = $service -and $service.Status -ne 'Stopped'
    $serviceStartAttempted = $false
    if ($wasRunning) {
        $serviceProgram = Get-SafeProductChild $registration.InstallLocation 'School.Training.exe'
        if (-not (Test-Path -LiteralPath $serviceProgram -PathType Leaf)) { throw 'The installed service executable is missing.' }
        $expectedVersion = ([Diagnostics.FileVersionInfo]::GetVersionInfo($serviceProgram).ProductVersion -split '\+',2)[0]
    }

    try {
        if ($wasRunning) { Stop-RestoreService }
        if (Test-Path -LiteralPath $database) {
            $exclusive = [IO.File]::Open($database,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $exclusive.Dispose()
        }
        if (Test-Path -LiteralPath $keys) { $null = @(Get-SafeFiles $keys) }
        New-Item -ItemType Directory -Path $rollback | Out-Null
        foreach ($item in $liveItems) {
            $current = Get-SafeChild $item
            if (Test-Path -LiteralPath $current) {
                Move-Item -LiteralPath $current -Destination (Get-SafeProductChild $rollback $item)
                $originalsMoved.Add($item)
            }
        }
        foreach ($item in @('school.db','keys') + $businessItems) {
            Move-Item -LiteralPath (Get-SafeProductChild $staging $item) -Destination (Get-SafeChild $item)
            $installedItems.Add($item)
        }
        if ($wasRunning) {
            $serviceStartAttempted = $true
            Start-VerifiedRestoreService
        }
        Write-Output "Backup restored. Previous database, keys, and business files preserved at: $rollback"
        Write-Output 'Sessions may need a new login. Original backups and logs were not modified.'
    } catch {
        $restoreFailure = $_
        try {
            if ($serviceStartAttempted) { Stop-RestoreService }
            foreach ($item in $liveItems) {
                $current = Get-SafeChild $item
                if (($serviceStartAttempted -or $installedItems.Contains($item)) -and (Test-Path -LiteralPath $current)) {
                    Move-Item -LiteralPath $current -Destination (Get-SafeProductChild $rollback ('failed-restored-' + $item))
                }
                if ($originalsMoved.Contains($item)) {
                    Move-Item -LiteralPath (Get-SafeProductChild $rollback $item) -Destination $current
                }
            }
            if ($wasRunning) {
                Start-VerifiedRestoreService
                Write-Output 'Original service restarted and passed its live status check.'
            }
        } catch {
            throw [InvalidOperationException]::new("Restore failed: $($restoreFailure.Exception.Message) Rollback could not be verified: $($_.Exception.Message)",$restoreFailure.Exception)
        }
        throw $restoreFailure
    }
} finally {
    if ($restoreLock) { $restoreLock.Dispose() }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath (Get-SafeChild "restore-staging-$operationId") -Recurse -Force }
}
