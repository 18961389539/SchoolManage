Set-StrictMode -Version Latest
$script:SchoolTrainingProduct = 'SchoolTraining'

function Assert-NoReparseInPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\')) { throw 'Network paths are not supported for installation or persistent data.' }
    $cursor = [IO.Path]::GetPathRoot($full)
    foreach ($segment in $full.Substring($cursor.Length).Split('\',[StringSplitOptions]::RemoveEmptyEntries)) {
        $cursor = Join-Path $cursor $segment
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points are not supported in deployment paths: $cursor" }
        }
    }
    return $full
}

function Get-CheckedDirectory([string]$Path) {
    $full = (Assert-NoReparseInPath $Path).TrimEnd('\')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw 'A local application subdirectory is required.' }
    foreach ($protected in @($env:ProgramFiles,${env:ProgramFiles(x86)},$env:ProgramData)) {
        if ($protected -and $full.Equals($protected.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'A protected root directory cannot be used.' }
    }
    $windows = [IO.Path]::GetFullPath($env:windir).TrimEnd('\')
    if ($full.Equals($windows,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($windows + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Windows system directories cannot be used.' }
    return $full
}

function Get-SafeProductChild([string]$Root,[string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or ($Relative.Replace('/','\').Split('\') -contains '..')) { throw 'Invalid product-relative path.' }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $full = [IO.Path]::GetFullPath((Join-Path $rootFull $Relative))
    if (-not $full.StartsWith($rootFull + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Product file path is outside its directory.' }
    return Assert-NoReparseInPath $full
}

function Get-VerifiedInstallManifest([string]$InstallDirectory,[switch]$AllowEmpty) {
    $full = Get-CheckedDirectory $InstallDirectory
    if (-not (Test-Path -LiteralPath $full)) { if ($AllowEmpty) { return $null }; throw 'Installation directory does not exist.' }
    $children = @(Get-ChildItem -LiteralPath $full -Force)
    if ($children.Count -eq 0 -and $AllowEmpty) { return $null }
    $file = Get-SafeProductChild $full '.schooltraining-install.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'The installation directory is nonempty and has no valid SchoolTraining installation manifest.' }
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($manifest.product -ne $script:SchoolTrainingProduct -or $manifest.schemaVersion -ne 1 -or -not $manifest.PSObject.Properties['files']) { throw 'Invalid product installation manifest.' }
    $files = @($manifest.files)
    if ($files.Count -eq 0 -or $files.Count -gt 20000) { throw 'Invalid installation file count.' }
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $files) {
        $null = Get-SafeProductChild $full ([string]$entry.path)
        if ($entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or -not $seen.Add([string]$entry.path)) { throw 'Invalid or duplicate installation file record.' }
    }
    if (-not $seen.Contains('School.Training.exe')) { throw 'Product executable record is missing.' }
    return $manifest
}

function Assert-OwnedDataDirectory([string]$DataDirectory,[switch]$AllowEmpty) {
    $full = Get-CheckedDirectory $DataDirectory
    if (-not (Test-Path -LiteralPath $full)) { if ($AllowEmpty) { return }; throw 'Data directory does not exist.' }
    $children = @(Get-ChildItem -LiteralPath $full -Force)
    if ($children.Count -eq 0 -and $AllowEmpty) { return }
    $markerFile = Get-SafeProductChild $full '.schooltraining-data.json'
    if (Test-Path -LiteralPath $markerFile -PathType Leaf) {
        $marker = Get-Content -LiteralPath $markerFile -Raw | ConvertFrom-Json
        if ($marker.product -eq $script:SchoolTrainingProduct -and $marker.schemaVersion -eq 1) { return }
    }
    $installationFile = Get-SafeProductChild $full 'installation.json'
    if (Test-Path -LiteralPath $installationFile -PathType Leaf) {
        $installation = Get-Content -LiteralPath $installationFile -Raw | ConvertFrom-Json
        if ($installation.product -eq $script:SchoolTrainingProduct -and $installation.PSObject.Properties['dataDirectory'] -and [IO.Path]::GetFullPath($installation.dataDirectory).TrimEnd('\').Equals($full,[StringComparison]::OrdinalIgnoreCase)) { return }
    }
    throw 'Nonempty data directory is not marked as SchoolTraining data. No files or permissions were changed.'
}

function Get-SafeFiles([string]$Root) {
    $rootFull = Assert-NoReparseInPath $Root
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue($rootFull)
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Published sources must not contain reparse points.' }
            if ($item.PSIsContainer) { $pending.Enqueue($item.FullName) } else { $item }
        }
    }
}

function Copy-SafeTree([string]$Source,[string]$Destination) {
    $sourceFull = Assert-NoReparseInPath $Source
    $null = Assert-NoReparseInPath $Destination
    foreach ($file in @(Get-SafeFiles $sourceFull)) {
        $relative = $file.FullName.Substring($sourceFull.TrimEnd('\').Length + 1)
        $target = Get-SafeProductChild $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

function Assert-ProtectedParentAcl([Security.AccessControl.DirectorySecurity]$Acl) {
    $trusted=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $null=$trusted.Add('S-1-5-18'); $null=$trusted.Add('S-1-5-32-544')
    try { $null=$trusted.Add((New-Object Security.Principal.NTAccount('NT SERVICE','TrustedInstaller')).Translate([Security.Principal.SecurityIdentifier]).Value) } catch [Security.Principal.IdentityNotMappedException] { }
    $owner=$Acl.GetOwner([Security.Principal.SecurityIdentifier])
    if(-not $owner -or -not $trusted.Contains($owner.Value)) { throw 'An installation/data parent is owned by an ordinary user and could be redirected later.' }
    $raw=New-Object Security.AccessControl.RawSecurityDescriptor($Acl.GetSecurityDescriptorBinaryForm(),0)
    if(-not $raw.DiscretionaryAcl) { throw 'An installation/data parent has a null DACL.' }
    # Creating unrelated children is allowed (the standard ProgramData/root policy),
    # but deleting/renaming protected children or changing the parent ACL is not.
    [long]$retargetRights=0x10000 -bor 0x40 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    foreach($rule in $Acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
        if(-not $trusted.Contains($rule.IdentityReference.Value) -and (([long]$rule.FileSystemRights) -band $retargetRights) -ne 0) { throw 'An installation/data parent grants ordinary users deletion, replacement or ACL-control rights.' }
    }
}

function Assert-ProtectedParentChain([string]$Directory) {
    $full=Get-CheckedDirectory $Directory
    $parent=Split-Path -Parent $full
    if(-not (Test-Path -LiteralPath $parent -PathType Container)) { throw 'Choose an existing protected parent directory for installation/data storage.' }
    while($parent) {
        $null=Assert-NoReparseInPath $parent
        Assert-ProtectedParentAcl (Get-Acl -LiteralPath $parent)
        $next=Split-Path -Parent $parent
        if($next -eq $parent) { break }
        $parent=$next
    }
}

function New-DeploymentAcl([ValidateSet('Program','Data')][string]$Mode,[bool]$IsDirectory) {
    if($IsDirectory) { $acl=New-Object Security.AccessControl.DirectorySecurity; $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' }
    else { $acl=New-Object Security.AccessControl.FileSecurity; $inherit=[Security.AccessControl.InheritanceFlags]::None }
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $entries=@(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'))
    if($Mode -eq 'Program') { $entries+=@(@('S-1-5-19','ReadAndExecute'),@('S-1-5-32-545','ReadAndExecute')) }
    else { $entries+=,@('S-1-5-19','Modify') }
    foreach($entry in $entries) {
        $sid=New-Object Security.Principal.SecurityIdentifier($entry[0])
        $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,$entry[1],$inherit,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    return $acl
}

function Set-DeploymentPermissions([string]$Root,[ValidateSet('Program','Data')][string]$Mode,$PlannedFiles=@()) {
    $rootFull=Get-CheckedDirectory $Root
    $directories=New-Object Collections.Generic.List[string]
    $pending=New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue($rootFull)
    $allFiles=New-Object Collections.Generic.List[string]
    while($pending.Count -gt 0) {
        $directory=$pending.Dequeue(); $directories.Add($directory)
        foreach($item in Get-ChildItem -LiteralPath $directory -Force) {
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse points are not allowed when applying product permissions.' }
            if($item.PSIsContainer) { $pending.Enqueue($item.FullName) } else { $allFiles.Add($item.FullName) }
        }
    }
    $files=New-Object Collections.Generic.List[string]
    if($Mode -eq 'Data') { foreach($file in $allFiles) { $files.Add($file) } }
    else {
        foreach($entry in $PlannedFiles) { $file=Get-SafeProductChild $rootFull $entry.path; if(Test-Path -LiteralPath $file -PathType Leaf) { $files.Add($file) } }
        $manifest=Get-SafeProductChild $rootFull '.schooltraining-install.json'; if(Test-Path -LiteralPath $manifest -PathType Leaf) { $files.Add($manifest) }
    }
    foreach($directory in $directories) { $null=Assert-NoReparseInPath $directory; Set-Acl -LiteralPath $directory -AclObject (New-DeploymentAcl $Mode $true) }
    foreach($file in $files) { $null=Assert-NoReparseInPath $file; Set-Acl -LiteralPath $file -AclObject (New-DeploymentAcl $Mode $false) }
}

function Get-ValidatedLanHosts([string]$Value,[string]$CertificateDnsName) {
    if([string]::IsNullOrWhiteSpace($Value)) { throw 'LAN AllowedHosts must explicitly list the school certificate hostname.' }
    $allowed=@($Value.Split(';') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ } | Select-Object -Unique)
    if($allowed.Count -eq 0 -or @($allowed | Where-Object { $_.Contains('*') }).Count -gt 0) { throw 'LAN AllowedHosts must contain explicit hostnames; empty and wildcard lists are not accepted.' }
    foreach($allowedHost in $allowed) {
        if($allowedHost -eq '[::1]') { continue }
        if($allowedHost.Contains(':') -or $allowedHost.Contains('/') -or $allowedHost.Contains('\') -or [Uri]::CheckHostName($allowedHost) -eq [UriHostNameType]::Unknown) { throw 'AllowedHosts contains an invalid hostname or a port/URL.' }
    }
    $external=@($allowed | Where-Object { $_ -notin @('localhost','127.0.0.1','[::1]','0.0.0.0') })
    if($external.Count -eq 0) { throw 'LAN AllowedHosts must explicitly include the school certificate hostname.' }
    $dns=$CertificateDnsName.Trim().ToLowerInvariant()
    if([string]::IsNullOrWhiteSpace($dns)) { throw 'The certificate must have a DNS identity matching the explicit AllowedHosts list.' }
    $matches=$false
    foreach($allowedHost in $external) {
        if($allowedHost -eq $dns) { $matches=$true; break }
        if($dns.StartsWith('*.')) {
            $suffix=$dns.Substring(1)
            if($allowedHost.EndsWith($suffix,[StringComparison]::Ordinal) -and $allowedHost.Length -gt $suffix.Length) {
                $label=$allowedHost.Substring(0,$allowedHost.Length-$suffix.Length)
                if(-not $label.Contains('.')) { $matches=$true; break }
            }
        }
    }
    if(-not $matches) { throw 'AllowedHosts must include an access hostname matching the certificate DNS name.' }
    return (@($allowed)+@('localhost','127.0.0.1','[::1]') | Select-Object -Unique) -join ';'
}

function Remove-InstalledPayload([string]$InstallDirectory) {
    $InstallDirectory=Get-CheckedDirectory $InstallDirectory
    $manifest=Get-VerifiedInstallManifest $InstallDirectory
    $manifestFile=Get-SafeProductChild $InstallDirectory '.schooltraining-install.json'
    $manifestHash=(Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash
    $remove=New-Object Collections.Generic.List[object]
    $directories=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    [int]$retained=0
    foreach($entry in @($manifest.files)) {
        $file=Get-SafeProductChild $InstallDirectory $entry.path
        if (-not (Test-Path -LiteralPath $file)) { continue }
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'An installed file path became a directory.' }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.Equals($entry.sha256,[StringComparison]::OrdinalIgnoreCase)) { $remove.Add([ordered]@{path=$file;sha256=$entry.sha256}) }
        else { $retained++ }
        $parent=Split-Path -Parent $file
        while($parent.StartsWith($InstallDirectory + '\',[StringComparison]::OrdinalIgnoreCase)) { $null=$directories.Add($parent); $parent=Split-Path -Parent $parent }
    }
    [int]$removed=0
    foreach($entry in $remove) {
        $null=Assert-NoReparseInPath $entry.path
        if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.Equals($entry.sha256,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $entry.path -Force; $removed++ }
        else { $retained++ }
    }
    foreach($directory in @($directories | Sort-Object Length -Descending)) {
        $null=Assert-NoReparseInPath $directory
        if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory -Force }
    }
    if ((Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash -eq $manifestHash) { Remove-Item -LiteralPath $manifestFile -Force }
    $directoryRetained=@(Get-ChildItem -LiteralPath $InstallDirectory -Force).Count -ne 0
    if (-not $directoryRetained) { Remove-Item -LiteralPath $InstallDirectory -Force }
    return [pscustomobject]@{removedFiles=$removed;modifiedFilesRetained=$retained;directoryRetained=$directoryRetained}
}

function Assert-NoPayloadCollisions([string]$InstallDirectory,$PlannedFiles,$PriorManifest) {
    $owned=@{}
    if ($PriorManifest) { foreach ($entry in @($PriorManifest.files)) { $owned[[string]$entry.path]=$entry } }
    foreach ($entry in $PlannedFiles) {
        $destination=Get-SafeProductChild $InstallDirectory ([string]$entry.path)
        if (-not (Test-Path -LiteralPath $destination)) { continue }
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw 'A release file conflicts with an existing directory.' }
        if ($entry.path -eq 'appsettings.Production.json' -and $owned.ContainsKey([string]$entry.path)) { continue }
        if (-not $owned.ContainsKey([string]$entry.path)) { throw "Release would overwrite an unknown file: $($entry.path). Move or back up that file before upgrading." }
        $actual=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if (-not $actual.Equals([string]$owned[[string]$entry.path].sha256,[StringComparison]::OrdinalIgnoreCase)) { throw "Installed file was modified: $($entry.path). Back up and resolve it before upgrading." }
    }
}

function Wait-ProductHealth([string]$DataDirectory,[string]$ExpectedVersion) {
    $port = 5080
    $installationFile = Get-SafeProductChild $DataDirectory 'installation.json'
    if (Test-Path -LiteralPath $installationFile) { $installation=Get-Content -LiteralPath $installationFile -Raw | ConvertFrom-Json; $port=[int]$installation.port }
    $url = "http://127.0.0.1:$port/api/status"
    $thumbprint = ''
    $serverFile = Get-SafeProductChild $DataDirectory 'server.json'
    if (Test-Path -LiteralPath $serverFile) {
        $config=Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json
        $properties=@($config.Kestrel.Endpoints.PSObject.Properties)
        if ($properties.Count -ne 1) { throw 'Cannot identify a single product health endpoint.' }
        $endpoint=$properties[0].Value
        $endpointUri=[UriBuilder]$endpoint.Url
        if ($endpointUri.Host -eq '0.0.0.0' -or $endpointUri.Host -eq 'localhost') { $endpointUri.Host='127.0.0.1' }
        $endpointUri.Path='/api/status'; $url=$endpointUri.Uri.AbsoluteUri
        if ($endpointUri.Scheme -eq 'https') {
            $password=''; if ($endpoint.Certificate.PSObject.Properties['Password']) { $password=[string]$endpoint.Certificate.Password }
            $certificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2($endpoint.Certificate.Path,$password,[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
            try { $thumbprint=$certificate.Thumbprint } finally { $certificate.Dispose() }
        }
    }
    if (-not ('SchoolTrainingDeployment.HealthProbe' -as [type])) {
        # HttpWebRequest supports per-request certificate pinning on Windows PowerShell 5.1.
        Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -TypeDefinition @'
using System;
using System.IO;
using System.Net;
namespace SchoolTrainingDeployment {
    public static class HealthProbe {
        public static string Get(string url, string expectedThumbprint) {
            var request=(HttpWebRequest)WebRequest.Create(url);
            request.Timeout=2000; request.ReadWriteTimeout=2000; request.Proxy=null;
            if (url.StartsWith("https://",StringComparison.OrdinalIgnoreCase)) {
                if (String.IsNullOrEmpty(expectedThumbprint)) throw new InvalidOperationException("HTTPS certificate identity is missing.");
                request.ServerCertificateValidationCallback=(sender,certificate,chain,errors)=>certificate!=null && String.Equals(certificate.GetCertHashString(),expectedThumbprint,StringComparison.OrdinalIgnoreCase);
            }
            using(var response=(HttpWebResponse)request.GetResponse())
            using(var reader=new StreamReader(response.GetResponseStream())) {
                if(response.StatusCode!=HttpStatusCode.OK) throw new InvalidOperationException("Product status endpoint did not return 200.");
                return reader.ReadToEnd();
            }
        }
    }
}
'@
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $status=[SchoolTrainingDeployment.HealthProbe]::Get($url,$thumbprint) | ConvertFrom-Json
            if ($status.PSObject.Properties['initialized'] -and $status.PSObject.Properties['version'] -and $status.PSObject.Properties['product'] -and -not [string]::IsNullOrWhiteSpace([string]$status.version) -and
                ([string]::IsNullOrEmpty($ExpectedVersion) -or $status.version -eq $ExpectedVersion)) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    } while([DateTime]::UtcNow -lt $deadline)
    throw 'The application did not pass its live database-backed status check.'
}
