[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [Parameter(ParameterSetName = 'Store')][string]$Thumbprint,
    [Parameter(ParameterSetName = 'Pfx')][string]$PfxPath,
    [Parameter(ParameterSetName = 'Pfx')][string]$PfxPasswordEnvironmentVariable = 'SCHOOL_SIGNING_PFX_PASSWORD',
    [string]$SignToolPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$MachineStore,
    [switch]$Require
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-SignTool {
    if ($SignToolPath) {
        if (-not (Test-Path -LiteralPath $SignToolPath)) { throw "SignToolPath does not exist: $SignToolPath" }
        return (Resolve-Path -LiteralPath $SignToolPath).Path
    }
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kits) {
        $candidates = Get-ChildItem -LiteralPath $kits -Directory | Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } | Where-Object { Test-Path -LiteralPath $_ }
        if ($candidates) { return @($candidates)[0] }
    }
    return $null
}

$configured = ($Thumbprint -or $PfxPath)
if (-not $configured) {
    if ($Require) { throw 'Code signing is required, but neither -Thumbprint nor -PfxPath was provided.' }
    Write-Warning 'No code-signing certificate configured; release files are unsigned.'
    exit 0
}

$resolved = foreach ($file in $Files) {
    if (-not (Test-Path -LiteralPath $file)) { throw "File to sign does not exist: $file" }
    (Resolve-Path -LiteralPath $file).Path
}

$signTool = Resolve-SignTool
if (-not $signTool) { throw 'signtool.exe was not found. Install the Windows SDK or pass -SignToolPath.' }

$signArguments = @('sign', '/fd', 'SHA256')
if ($Thumbprint) {
    $signArguments += @('/sha1', $Thumbprint)
    if ($MachineStore) { $signArguments += '/sm' }
} else {
    $password = [Environment]::GetEnvironmentVariable($PfxPasswordEnvironmentVariable)
    if ([string]::IsNullOrEmpty($password)) { throw "Environment variable $PfxPasswordEnvironmentVariable is not set." }
    $signArguments += @('/f', (Resolve-Path -LiteralPath $PfxPath).Path, '/p', $password)
}
if ($TimestampUrl) { $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }

foreach ($file in $resolved) {
    & $signTool @signArguments $file
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed for $file (exit code $LASTEXITCODE)." }
    & $signTool verify /pa /v $file | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool verify failed for $file (exit code $LASTEXITCODE)." }
    Write-Output "Signed: $file"
}
exit 0