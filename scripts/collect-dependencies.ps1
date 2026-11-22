[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination, [string]$FrontendDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$frontendRoot = if ($FrontendDirectory) { (Resolve-Path -LiteralPath $FrontendDirectory).Path } else { Join-Path $projectRoot 'web' }
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$components = New-Object Collections.Generic.List[object]

function Get-OptionalProperty($Value,[string]$Name,[object]$Default = '') {
    $property = $Value.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $Default
}
function Copy-Notices([string]$Folder,[string]$Target) {
    $copied = New-Object Collections.Generic.List[string]
    $candidates = @(Get-ChildItem -LiteralPath $Folder -File | Where-Object { $_.Name -match '^(licen[sc]e|copying|notice|thirdpartynotices)([._-].*)?$' })
    foreach ($file in $candidates) {
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Target $file.Name) -Force
        $copied.Add($file.Name)
    }
    return $copied.ToArray()
}

$assetsFile = Join-Path $projectRoot 'src/School.Training/obj/project.assets.json'
if (-not (Test-Path -LiteralPath $assetsFile)) { throw 'Restore backend dependencies before collecting notices.' }
$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties | ForEach-Object Name)
foreach ($libraryProperty in $assets.libraries.PSObject.Properties) {
    $library = $libraryProperty.Value
    if ($library.type -ne 'package') { continue }
    $split = $libraryProperty.Name.LastIndexOf('/')
    $name = $libraryProperty.Name.Substring(0,$split)
    $version = $libraryProperty.Name.Substring($split + 1)
    $package = $null
    foreach ($folder in $packageRoots) { $candidate = Join-Path $folder $library.path; if (Test-Path -LiteralPath $candidate) { $package = [IO.Path]::GetFullPath($candidate); break } }
    if (-not $package) { throw "NuGet package not found: $name/$version" }
    $target = Join-Path $Destination (Join-Path 'nuget' ($name + '-' + $version))
    $notices = @(Copy-Notices $package $target)
    $license = ''
    $projectUrl = ''
    $repositoryUrl = ''
    $repositoryCommit = ''
    $spec = Get-ChildItem -LiteralPath $package -Filter '*.nuspec' -File | Select-Object -First 1
    if ($spec) {
        [xml]$specXml = Get-Content -LiteralPath $spec.FullName -Raw
        $licenseNode = $specXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
        $projectNode = $specXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='projectUrl']")
        $repositoryNode = $specXml.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='repository']")
        if ($projectNode) { $projectUrl = $projectNode.InnerText }
        if ($repositoryNode) { $repositoryUrl = $repositoryNode.GetAttribute('url'); $repositoryCommit = $repositoryNode.GetAttribute('commit') }
        if ($licenseNode) {
            $license = $licenseNode.InnerText
            if ($licenseNode.GetAttribute('type') -eq 'file') {
                $licensePath = [IO.Path]::GetFullPath((Join-Path $package $licenseNode.InnerText))
                if ($licensePath.StartsWith($package.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
                    New-Item -ItemType Directory -Path $target -Force | Out-Null
                    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $target ([IO.Path]::GetFileName($licensePath))) -Force
                    $notices += [IO.Path]::GetFileName($licensePath)
                }
            }
        }
        Copy-Item -LiteralPath $spec.FullName -Destination (Join-Path $Destination ('nuget-' + $name + '-' + $version + '.nuspec')) -Force
    }
    $supplement = Join-Path $projectRoot (Join-Path 'docs/third-party-sources' ($name + '-' + $version))
    if (Test-Path -LiteralPath $supplement) { New-Item -ItemType Directory -Path $target -Force | Out-Null; Get-ChildItem -LiteralPath $supplement -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Force; $notices += $_.Name } }
    $components.Add([ordered]@{ecosystem='NuGet';name=$name;version=$version;license=$license;projectUrl=$projectUrl;repositoryUrl=$repositoryUrl;repositoryCommit=$repositoryCommit;noticeFiles=@($notices | Select-Object -Unique)})
}

$lockFile = Join-Path $frontendRoot 'package-lock.json'
if (-not (Test-Path -LiteralPath $lockFile)) { throw 'Frontend package-lock.json is required for dependency inventory.' }
$npmMetadata = & node (Join-Path $PSScriptRoot 'collect-npm-dependencies.mjs') $frontendRoot
if ($LASTEXITCODE -ne 0) { throw 'Failed to read installed frontend dependency metadata.' }
$npmComponents = $npmMetadata | ConvertFrom-Json
foreach ($package in $npmComponents) {
    $folder = $package.folder
    $name = [string]$package.name
    $version = [string]$package.version
    $safeName = ($name -replace '[^A-Za-z0-9._-]','_') + '-' + $version
    $notices = @(Copy-Notices $folder (Join-Path $Destination (Join-Path 'npm' $safeName)))
    $supplement = Join-Path $projectRoot (Join-Path 'docs/third-party-sources' ($name + '-' + $version))
    if (Test-Path -LiteralPath $supplement) { $target = Join-Path $Destination (Join-Path 'npm' $safeName); New-Item -ItemType Directory -Path $target -Force | Out-Null; Get-ChildItem -LiteralPath $supplement -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Force; $notices += $_.Name } }
    $components.Add([ordered]@{ecosystem='npm';name=$name;version=$version;license=(Get-OptionalProperty $package 'license');projectUrl=(Get-OptionalProperty $package 'homepage');noticeFiles=@($notices | Select-Object -Unique)})
}
$dotnetCommand = Get-Command dotnet.exe -ErrorAction Stop
$dotnetFolder = Split-Path -Parent $dotnetCommand.Source
$dotnetNotices = Copy-Notices $dotnetFolder (Join-Path $Destination 'dotnet-runtime')
$metadata = [ordered]@{generatedAtUtc=[DateTime]::UtcNow.ToString('o');description='Dependency metadata and notice files present in restored build inputs. Review unresolved notices before production distribution.';components=$components.ToArray();dotnetNoticeFiles=$dotnetNotices}
$metadata | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $Destination 'components.json') -Encoding UTF8
$missing = @($components | Where-Object { $_.noticeFiles.Count -eq 0 })
[ordered]@{components=$components.Count;withoutLocalNoticeFile=$missing.Count;items=@($missing | ForEach-Object { "$($_.ecosystem):$($_.name)@$($_.version) [$($_.license)]" })} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'notice-review.json') -Encoding UTF8
Write-Output "Dependency inventory: $($components.Count) components; $($missing.Count) without a local notice file."
