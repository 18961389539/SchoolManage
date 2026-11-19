[CmdletBinding()]
param([string]$ReportFile='docs/verification/deployment-policy.json')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $projectRoot 'scripts/deployment-common.ps1')
$checks=New-Object Collections.Generic.List[object]
function Assert-True([bool]$Value,[string]$Message) { if(-not $Value) { throw $Message } }
function Reject([scriptblock]$Action) { $rejected=$false;try { $null=& $Action } catch { $rejected=$true }; Assert-True $rejected 'Unsafe policy was accepted.' }
function Check([string]$Name,[scriptblock]$Action) {
    try { $null=& $Action; $checks.Add([ordered]@{name=$Name;passed=$true}); Write-Output "PASS $Name" }
    catch { $checks.Add([ordered]@{name=$Name;passed=$false;error=$_.Exception.Message});Write-Output "FAIL $Name`: $($_.Exception.Message)" }
}
function Verify-Acl([string]$Mode,[bool]$IsDirectory) {
    $acl=New-DeploymentAcl $Mode $IsDirectory
    Assert-True $acl.AreAccessRulesProtected 'ACL inherits unsafe parent permissions.'
    Assert-True ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-544') 'Owner is not Administrators.'
    $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
    $expectedCount=3;if($Mode -eq 'Program'){$expectedCount=4}
    Assert-True ($rules.Count -eq $expectedCount) 'Unexpected policy principal count.'
    $rights=@{}
    foreach($rule in $rules) {
        Assert-True ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and -not $rule.IsInherited) 'Unexpected inherited or deny rule.'
        if($IsDirectory) { Assert-True ($rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit') 'Directory policy is not inherited by children.' }
        else { Assert-True ($rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]::None) 'File policy has directory-only inheritance flags.' }
        $rights[$rule.IdentityReference.Value]=[long]$rule.FileSystemRights
    }
    [long]$full=[Security.AccessControl.FileSystemRights]::FullControl
    Assert-True (($rights['S-1-5-18'] -band $full) -eq $full -and ($rights['S-1-5-32-544'] -band $full) -eq $full) 'System/administrator full control is missing.'
    if($Mode -eq 'Program') {
        [long]$read=[Security.AccessControl.FileSystemRights]::ReadAndExecute
        [long]$write=[Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
        foreach($sid in @('S-1-5-19','S-1-5-32-545')) { Assert-True (($rights[$sid] -band $read) -eq $read -and ($rights[$sid] -band $write) -eq 0) 'Program policy permits nonadministrator writes or lacks execute/read.' }
    } else {
        Assert-True (-not $rights.ContainsKey('S-1-5-32-545')) 'Data policy permits ordinary Users.'
        [long]$modify=[Security.AccessControl.FileSystemRights]::Modify
        Assert-True (($rights['S-1-5-19'] -band $modify) -eq $modify -and ($rights['S-1-5-19'] -band ([long][Security.AccessControl.FileSystemRights]::ChangePermissions)) -eq 0) 'Data service permissions are not limited to Modify.'
    }
}
Check 'Program directory ACL is administrator-owned, protected and read/execute-only for service/users' { Verify-Acl 'Program' $true }
Check 'Program file ACL explicitly grants LocalService read/execute after admin-only staging copy' { Verify-Acl 'Program' $false }
Check 'Data directory ACL excludes Users and allows service Modify' { Verify-Acl 'Data' $true }
Check 'Data file ACL is administrator-owned and excludes Users' { Verify-Acl 'Data' $false }
Check 'Default Program Files parent chain passes read-only validation' { Assert-ProtectedParentChain (Join-Path $env:ProgramFiles 'SchoolTraining-PolicyProbe') }
Check 'Default ProgramData parent chain passes read-only validation' { Assert-ProtectedParentChain (Join-Path $env:ProgramData 'SchoolTraining-PolicyProbe') }
Check 'Writable ordinary-user parent policy is refused' { $acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1301bf;;;BU)');Reject { Assert-ProtectedParentAcl $acl } }
Check 'Ordinary-user ownership of a parent is refused' { $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;$acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetSecurityDescriptorSddlForm("O:${sid}G:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)");Reject { Assert-ProtectedParentAcl $acl } }
Check 'A parent allowing unrelated child creation without retarget rights is accepted' { $acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x116;;;BU)');Assert-ProtectedParentAcl $acl }
Check 'GENERIC_ALL parent access for ordinary users is refused' { $acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GA;;;BU)');Reject { Assert-ProtectedParentAcl $acl } }
Check 'A null parent DACL is refused' { $acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:NO_ACCESS_CONTROL');Reject { Assert-ProtectedParentAcl $acl } }
Check 'Explicit certificate hostname gets only additional loopback hosts' { $result=Get-ValidatedLanHosts 'training.school.example' 'training.school.example';$hosts=@($result.Split(';'));Assert-True ($hosts.Count -eq 4 -and $hosts -contains 'training.school.example' -and $hosts -contains '127.0.0.1' -and $hosts -contains '[::1]' -and $hosts -contains 'localhost') 'Unexpected automatic hostname additions.' }
Check 'Empty AllowedHosts is refused' { Reject { Get-ValidatedLanHosts '' 'training.school.example' } }
Check 'Global wildcard AllowedHosts is refused' { Reject { Get-ValidatedLanHosts '*' 'training.school.example' } }
Check 'Wildcard mixed with an explicit host is refused' { Reject { Get-ValidatedLanHosts 'training.school.example;*' 'training.school.example' } }
Check 'URL/port values in AllowedHosts are refused' { Reject { Get-ValidatedLanHosts 'https://training.school.example:5443' 'training.school.example' } }
Check 'Certificate hostname mismatch is refused' { Reject { Get-ValidatedLanHosts 'another.school.example' 'training.school.example' } }
Check 'LAN configuration containing only loopback hosts is refused' { Reject { Get-ValidatedLanHosts 'localhost;127.0.0.1;[::1]' 'training.school.example' } }
Check 'An explicit host matching a one-label wildcard certificate is accepted' { $null=Get-ValidatedLanHosts 'training.school.example' '*.school.example' }
Check 'Wildcard certificate does not match multiple extra labels' { Reject { Get-ValidatedLanHosts 'deep.training.school.example' '*.school.example' } }
$passed=@($checks|Where-Object{$_.passed}).Count
$reportPath=[IO.Path]::GetFullPath((Join-Path $projectRoot $ReportFile))
if(-not $reportPath.StartsWith((Join-Path $projectRoot 'docs\verification')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Report must remain inside docs/verification.'}
$report=[ordered]@{suite='deployment-acl-host-policy';powershellVersion=$PSVersionTable.PSVersion.ToString();completedAtUtc=[DateTime]::UtcNow.ToString('o');commonScriptSha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot 'scripts/deployment-common.ps1') -Algorithm SHA256).Hash;passed=($passed-eq$checks.Count);passedChecks=$passed;totalChecks=$checks.Count;checks=$checks.ToArray();scope='ACL objects only in memory; existing default parent ACLs read only; AllowedHosts pure validation. No filesystem ACL changes, service registration, registry/firewall changes, real install or privileged-token enforcement test.'}
[IO.File]::WriteAllText($reportPath,($report|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Write-Output "Deployment policy: $passed/$($checks.Count) passed. Report: $reportPath"
if($passed-ne$checks.Count){exit 1}
