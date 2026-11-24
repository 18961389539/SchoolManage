#Requires -Version 5.1
<#
.SYNOPSIS
    向已初始化的本机实训平台导入演示/仿真数据。

.DESCRIPTION
    通过产品 HTTP 接口追加一套固定的演示内容（专业、班级、师生、课程、课堂、考试和岗位训练营），
    并调用内置「创建模拟环境」。默认只新增尚不存在的记录，不删除、不覆盖已有数据，也不修改 admin 账号或密码。

    前置条件：服务已在本机运行，并已在本机浏览器完成管理员初始化。
    演示师生的初始密码为 Demo12345678，首次登录后需按产品规则修改密码。
    说明见 docs/demo-data.md。

.PARAMETER BaseUrl
    服务地址，默认 http://127.0.0.1:5080

.PARAMETER UserName
    管理员账号，默认 admin

.PARAMETER Password
    管理员密码。若省略，则读取环境变量 SCHOOL_ADMIN_PASSWORD，仍为空则在控制台提示输入。不要把该密码写入仓库。
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5080',
    [string]$UserName = 'admin',
    [string]$Password
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$DemoPassword = 'Demo12345678'
$ApiRoot = $BaseUrl.TrimEnd('/') + '/api'
$script:Session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$script:CsrfToken = $null
$script:Created = New-Object System.Collections.Generic.List[string]
$script:Skipped = New-Object System.Collections.Generic.List[string]
$script:ApiNotes = New-Object System.Collections.Generic.List[string]
$script:RestorePersonIds = New-Object System.Collections.Generic.List[string]
$script:LoginCount = 0
$script:WorkPassword = 'DemoSeedWork1234'
$script:AdminPassword = $null
$script:PracticeAnswers = @{}

function Write-Step([string]$Message) { Write-Host $Message }

function Get-PlainPassword {
    if (-not [string]::IsNullOrWhiteSpace($Password)) { return $Password }
    if (-not [string]::IsNullOrWhiteSpace($env:SCHOOL_ADMIN_PASSWORD)) { return $env:SCHOOL_ADMIN_PASSWORD }
    $secure = Read-Host -Prompt '管理员密码' -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Convert-JsonBody($Body) {
    if ($null -eq $Body) { return $null }
    # 用 -InputObject，避免 Hashtable 管道枚举；直接传 JSON 字符串，避免函数 return 拆散字节数组。
    return [string](ConvertTo-Json -InputObject $Body -Depth 16 -Compress)
}

function Invoke-Api {
    param(
        [Parameter(Mandatory=$true)][ValidateSet('GET','POST','PUT','DELETE')][string]$Method,
        [Parameter(Mandatory=$true)][string]$Path,
        $Body,
        [hashtable]$Query
    )
    $uri = $ApiRoot + $Path
    if ($Query) {
        $pairs = foreach ($key in $Query.Keys) {
            $value = $Query[$key]
            if ($null -ne $value -and [string]$value -ne '') {
                '{0}={1}' -f [Uri]::EscapeDataString([string]$key), [Uri]::EscapeDataString([string]$value)
            }
        }
        $qs = @($pairs | Where-Object { $_ }) -join '&'
        if ($qs) { $uri = $uri + '?' + $qs }
    }
    $headers = @{ Accept = 'application/json' }
    if ($Method -ne 'GET' -and $script:CsrfToken) { $headers['X-CSRF-TOKEN'] = $script:CsrfToken }
    $params = @{
        Uri = $uri
        Method = $Method
        WebSession = $script:Session
        Headers = $headers
        UseBasicParsing = $true
        TimeoutSec = 60
    }
    $bytes = Convert-JsonBody $Body
    if ($null -ne $bytes) {
        $params.ContentType = 'application/json; charset=utf-8'
        $params.Body = $bytes
    }
    try {
        $response = Invoke-WebRequest @params
        if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
        # 一元逗号防止 JSON 数组被函数 return 拆成多个管道对象（Windows PowerShell 5）。
        return ,(ConvertFrom-Json -InputObject $response.Content)
    }
    catch [System.Net.WebException] {
        $detail = $_.Exception.Message
        $httpResponse = $_.Exception.Response
        if ($httpResponse) {
            $status = [int]$httpResponse.StatusCode
            try {
                $stream = $httpResponse.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $text = $reader.ReadToEnd()
                if ($text) { $detail = "$status $text" } else { $detail = "$status $detail" }
            }
            catch { $detail = "$status $detail" }
        }
        throw "接口 $Method $Path 失败：$detail"
    }
}

function Get-ApiItems {
    param([string]$Path, [hashtable]$Query)
    $result = Invoke-Api -Method GET -Path $Path -Query $Query
    if ($null -eq $result) { return @() }
    if ($result.PSObject.Properties.Name -contains 'items') { return @($result.items) }
    return @($result)
}

function Find-NamedItem {
    param($Items, [string]$Name, [string]$Property = 'name')
    foreach ($item in @($Items)) {
        if ($null -ne $item -and [string]$item.$Property -eq $Name) { return $item }
    }
    return $null
}

function Connect-Admin([string]$PlainPassword) {
    $result = Invoke-Api -Method POST -Path '/auth/login' -Body @{ userName = $UserName; password = $PlainPassword }
    if (-not $result -or -not $result.csrfToken) { throw '登录失败：未返回安全令牌。' }
    $script:CsrfToken = [string]$result.csrfToken
    $role = $result.user.role
    if ($role -ne 'Admin') { throw "当前账号角色为 $role，导入演示数据需要管理员。" }
    Write-Step ("已登录 {0}（{1}）" -f $result.user.userName, $role)
}

function Add-Profession([string]$Name, [string]$Code, [string]$Remark) {
    $existing = Find-NamedItem (Get-ApiItems '/professions' @{ page = 1; pageSize = 1000 }) $Name
    if ($existing) { $script:Skipped.Add("专业 $Name"); return $existing.id }
    $row = Invoke-Api -Method POST -Path '/professions' -Body @{ name = $Name; code = $Code; remark = $Remark }
    $script:Created.Add("专业 $Name")
    return $row.id
}

function Add-Class([string]$Name, [string]$ProfessionId, [string]$Remark) {
    $existing = Find-NamedItem (Get-ApiItems '/classes' @{ page = 1; pageSize = 1000 }) $Name
    if ($existing) { $script:Skipped.Add("班级 $Name"); return $existing.id }
    $row = Invoke-Api -Method POST -Path '/classes' -Body @{ name = $Name; professionId = $ProfessionId; remark = $Remark }
    $script:Created.Add("班级 $Name")
    return $row.id
}

function Add-Person($Person) {
    $kind = $Person.Kind
    $existing = Find-NamedItem (Get-ApiItems '/people' @{ kind = $kind; page = 1; pageSize = 1000; search = $Person.UserName }) $Person.UserName 'userName'
    if ($existing) { $script:Skipped.Add("账号 $($Person.UserName)"); return }
    $body = @{
        kind = $kind
        name = $Person.Name
        userName = $Person.UserName
        number = $Person.Number
        gender = $Person.Gender
        phone = $Person.Phone
        email = $Person.Email
        role = $Person.Role
        password = $DemoPassword
    }
    if ($Person.ClassId) { $body.classId = $Person.ClassId }
    [void](Invoke-Api -Method POST -Path '/people' -Body $body)
    $script:Created.Add(("{0} {1}" -f $(if ($kind -eq 'teacher') { '教师' } else { '学生' }), $Person.Name))
}

function Add-CourseSeries([string]$Name, [string]$Description) {
    $existing = Find-NamedItem (Get-ApiItems '/course-series' @{ manage = 'true' }) $Name
    if ($existing) { $script:Skipped.Add("课程系列 $Name"); return $existing.id }
    $row = Invoke-Api -Method POST -Path '/course-series' -Body @{ name = $Name; description = $Description }
    $script:Created.Add("课程系列 $Name")
    return $row.id
}

function Add-Course($Course) {
    $existing = Find-NamedItem (Get-ApiItems '/courses' @{ page = 1; pageSize = 1000 }) $Course.Name
    if ($existing) { $script:Skipped.Add("课程 $($Course.Name)"); return $existing.id }
    $body = @{ name = $Course.Name; type = $Course.Type; description = $Course.Description }
    if ($Course.SeriesId) { $body.seriesId = $Course.SeriesId }
    $row = Invoke-Api -Method POST -Path '/courses' -Body $body
    $courseId = $row.id
    foreach ($chapter in $Course.Chapters) {
        $createdChapter = Invoke-Api -Method POST -Path "/courses/$courseId/chapters" -Body @{ name = $chapter.Name }
        foreach ($section in $chapter.Sections) {
            $createdSection = Invoke-Api -Method POST -Path "/chapters/$($createdChapter.id)/sections" -Body @{ name = $section.Name; durationMinutes = $section.DurationMinutes }
            foreach ($resource in $section.Resources) {
                [void](Invoke-Api -Method POST -Path "/sections/$($createdSection.id)/resources" -Body $resource)
            }
        }
    }
    [void](Invoke-Api -Method POST -Path "/courses/$courseId/publish" -Query @{ activate = 'true' })
    $script:Created.Add("课程 $($Course.Name)")
    return $courseId
}

function Add-Classroom($Room) {
    $items = Get-ApiItems '/classrooms' @{ page = 1; pageSize = 1000 }
    $existing = Find-NamedItem $items $Room.Name
    if ($existing) { $script:Skipped.Add("课堂 $($Room.Name)"); return $existing.id }
    $row = Invoke-Api -Method POST -Path '/classrooms' -Body @{
        name = $Room.Name
        courseId = $Room.CourseId
        teacherUserId = $Room.TeacherUserId
        openAt = $Room.OpenAt
        closeAt = $Room.CloseAt
        classIds = @($Room.ClassIds)
    }
    [void](Invoke-Api -Method POST -Path "/classrooms/$($row.id)/open")
    $script:Created.Add("课堂 $($Room.Name)")
    return $row.id
}

function Get-IsoUtc([datetime]$Value) {
    return $Value.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
}


function Connect-User {
    param([string]$LoginName, [string]$LoginPassword)
    $script:LoginCount++
    if ($script:LoginCount -gt 1 -and (($script:LoginCount - 1) % 6) -eq 0) {
        Write-Step '为避免登录频控，稍候再继续…'
        Start-Sleep -Seconds 35
    }
    $attempt = 0
    while ($true) {
        $script:Session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
        $script:CsrfToken = $null
        try {
            $result = Invoke-Api -Method POST -Path '/auth/login' -Body @{ userName = $LoginName; password = $LoginPassword }
            if (-not $result -or -not $result.csrfToken) { throw "登录 $LoginName 失败。" }
            $script:CsrfToken = [string]$result.csrfToken
            return $result
        }
        catch {
            $message = [string]$_.Exception.Message
            if ($attempt -lt 5 -and ($message -match '429|Too Many|频繁|Retry after|rate')) {
                $attempt++
                Write-Step ("登录频控（{0}），等待后重试…" -f $LoginName)
                Start-Sleep -Seconds 20
                continue
            }
            throw
        }
    }
}

function Connect-Admin([string]$PlainPassword) {
    $result = Connect-User $UserName $PlainPassword
    $role = $result.user.role
    if ($role -ne 'Admin') { throw "当前账号角色为 $role，导入演示数据需要管理员。" }
    Write-Step ("已登录 {0}（{1}）" -f $result.user.userName, $role)
}

function Get-PersonIdMap {
    $map = @{}
    foreach ($kind in @('teacher','student')) {
        foreach ($row in (Get-ApiItems '/people' @{ kind = $kind; page = 1; pageSize = 1000 })) {
            $map[[string]$row.userName] = [string]$row.id
        }
    }
    return $map
}

function Get-StudentNumberMap {
    $map = @{}
    foreach ($row in (Get-ApiItems '/teaching/students' @{ page = 1; pageSize = 1000 })) {
        $map[[string]$row.number] = [string]$row.userId
    }
    return $map
}

function Get-TeacherNameMap {
    $map = @{}
    foreach ($row in (Get-ApiItems '/teaching/teachers' @{})) {
        $map[[string]$row.userName] = [string]$row.id
    }
    return $map
}

function Ensure-CourseContent {
    param([string]$CourseId, $Chapters)
    if (-not $CourseId) { return }
    $course = Invoke-Api -Method GET -Path "/courses/$CourseId"
    $chapterList = @()
    if ($course -and $course.PSObject.Properties.Name -contains 'chapters') { $chapterList = @($course.chapters) }
    foreach ($chapter in @($Chapters)) {
        $existingChapter = Find-NamedItem $chapterList $chapter.Name
        if ($existingChapter) {
            $script:Skipped.Add("课程章节 $($chapter.Name)")
            $chapterId = [string]$existingChapter.id
            $sectionList = @()
            if ($existingChapter.PSObject.Properties.Name -contains 'sections') { $sectionList = @($existingChapter.sections) }
        }
        else {
            $createdChapter = Invoke-Api -Method POST -Path "/courses/$CourseId/chapters" -Body @{ name = $chapter.Name }
            $chapterId = [string]$createdChapter.id
            $sectionList = @()
            $script:Created.Add("课程章节 $($chapter.Name)")
        }
        foreach ($section in @($chapter.Sections)) {
            $existingSection = Find-NamedItem $sectionList $section.Name
            if ($existingSection) {
                $script:Skipped.Add("课程小节 $($section.Name)")
                $sectionId = [string]$existingSection.id
                $resourceList = @()
                if ($existingSection.PSObject.Properties.Name -contains 'resources') { $resourceList = @($existingSection.resources) }
            }
            else {
                $createdSection = Invoke-Api -Method POST -Path "/chapters/$chapterId/sections" -Body @{
                    name = $section.Name
                    durationMinutes = $section.DurationMinutes
                }
                $sectionId = [string]$createdSection.id
                $resourceList = @()
                $script:Created.Add("课程小节 $($section.Name)")
            }
            foreach ($resource in @($section.Resources)) {
                $resourceName = [string]$resource.name
                if (Find-NamedItem $resourceList $resourceName) {
                    $script:Skipped.Add("课程资源 $resourceName")
                    continue
                }
                [void](Invoke-Api -Method POST -Path "/sections/$sectionId/resources" -Body $resource)
                $script:Created.Add("课程资源 $resourceName")
            }
        }
    }
}

function Add-BankQuestions {
    param([string]$BankId, $Items)
    if (-not $BankId) { return @() }
    $existingQuestions = Get-ApiItems '/exam-questions' @{ page = 1; pageSize = 1000; bankId = $BankId }
    $ids = @()
    foreach ($item in @($Items)) {
        $found = Find-NamedItem $existingQuestions $item.stem 'stem'
        if ($found) {
            $ids += [string]$found.id
            $script:Skipped.Add("试题 $($item.stem)")
            continue
        }
        $payload = @{
            type = $item.type
            stem = $item.stem
            options = @($item.options)
            answers = @($item.answers)
            score = $item.score
            analysis = $item.analysis
            bankId = $BankId
        }
        $created = Invoke-Api -Method POST -Path '/exam-questions' -Body $payload
        $ids += [string]$created.id
        $script:Created.Add("试题 $($item.stem)")
    }
    return ,$ids
}

function Add-BankIfMissing {
    param([string]$Name, [string]$Type)
    $banks = Get-ApiItems '/exam-question-banks' @{ page = 1; pageSize = 1000 }
    $existing = Find-NamedItem $banks $Name
    if ($existing) {
        $script:Skipped.Add("题库 $Name")
        return [string]$existing.id
    }
    $created = Invoke-Api -Method POST -Path '/exam-question-banks' -Body @{ name = $Name; type = $Type }
    $script:Created.Add("题库 $Name")
    return [string]$created.id
}

function Add-PaperIfMissing {
    param([string]$Name, [string]$Description, [string]$Remark, $QuestionIds)
    $papers = Get-ApiItems '/exam-papers' @{ page = 1; pageSize = 1000 }
    $existing = Find-NamedItem $papers $Name
    if ($existing) {
        $script:Skipped.Add("试卷 $Name")
        return [string]$existing.id
    }
    $paper = Invoke-Api -Method POST -Path '/exam-papers' -Body @{
        name = $Name
        description = $Description
        remark = $Remark
    }
    $items = @($QuestionIds | ForEach-Object { @{ questionId = $_ } })
    [void](Invoke-Api -Method POST -Path "/exam-papers/$($paper.id)/questions" -Body @{ items = $items })
    $script:Created.Add("试卷 $Name")
    return [string]$paper.id
}

function Add-ExamIfMissing {
    param($Exam)
    $exams = Get-ApiItems '/exams' @{ page = 1; pageSize = 1000 }
    $existing = Find-NamedItem $exams $Exam.Name
    if ($existing) {
        $script:Skipped.Add("考试 $($Exam.Name)")
        return [string]$existing.id
    }
    $created = Invoke-Api -Method POST -Path '/exams' -Body @{
        name = $Exam.Name
        paperId = $Exam.PaperId
        description = $Exam.Description
        startAt = $Exam.StartAt
        endAt = $Exam.EndAt
        durationMinutes = $Exam.DurationMinutes
        classIds = @($Exam.ClassIds)
    }
    [void](Invoke-Api -Method POST -Path "/exams/$($created.id)/open")
    $script:Created.Add("考试 $($Exam.Name)")
    return [string]$created.id
}

function Add-GroupIfMissing {
    param([string]$ClassroomId, [string]$Name, $MemberIds, [string]$Remark)
    if (-not $ClassroomId) { return $null }
    $groups = Get-ApiItems "/classrooms/$ClassroomId/groups" @{}
    $existing = Find-NamedItem $groups $Name
    if ($existing) {
        $script:Skipped.Add("课堂分组 $Name")
        return [string]$existing.id
    }
    $ids = @($MemberIds | Where-Object { $_ })
    if ($ids.Count -eq 0) { return $null }
    $created = Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/groups" -Body @{
        name = $Name
        userIds = @($ids)
        remark = $Remark
    }
    $script:Created.Add("课堂分组 $Name")
    if ($created -and $created.id) { return [string]$created.id }
    $again = Find-NamedItem (Get-ApiItems "/classrooms/$ClassroomId/groups" @{}) $Name
    if ($again) { return [string]$again.id }
    return $null
}

function Ensure-ClassroomEnrollment {
    param([string]$ClassroomId, [string]$ClassId)
    if (-not $ClassroomId -or -not $ClassId) { return }
    try {
        $result = Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/students" -Body @{ classIds = @($ClassId) }
        $added = 0
        if ($result -and $result.PSObject.Properties.Name -contains 'added') { $added = [int]$result.added }
        if ($added -gt 0) { $script:Created.Add("课堂补录学生 $added 人") }
        else { $script:Skipped.Add('课堂学生名单已包含该班') }
    }
    catch {
        if ("$_" -match '409|没有新增学生') { $script:Skipped.Add('课堂学生名单已包含该班') }
        else { Write-Step ("课堂补录学生已跳过：{0}" -f $_.Exception.Message) }
    }
}

function Add-DeviceIfMissing($Device) {
    $existing = Find-NamedItem (Get-ApiItems '/devices' @{ page = 1; pageSize = 1000 }) $Device.Name
    if ($existing) { $script:Skipped.Add("模拟设备 $($Device.Name)"); return }
    [void](Invoke-Api -Method POST -Path '/devices' -Body @{
        name = $Device.Name
        cabinetId = $Device.CabinetId
        deviceTypeId = $Device.DeviceTypeId
        connectionType = 'simulated'
        serialPortNumber = $Device.SerialPortNumber
        nodeId = $Device.NodeId
        remark = $Device.Remark
    })
    $script:Created.Add("模拟设备 $($Device.Name)")
}

function Add-VmIfMissing($Vm) {
    $existing = Find-NamedItem (Get-ApiItems '/virtual-machines' @{ page = 1; pageSize = 1000 }) $Vm.Name
    if ($existing) { $script:Skipped.Add("模拟虚拟机 $($Vm.Name)"); return $existing.id }
    $row = Invoke-Api -Method POST -Path '/virtual-machines' -Body @{
        name = $Vm.Name
        nodeId = $Vm.NodeId
        cpuCount = $Vm.CpuCount
        memoryMb = $Vm.MemoryMb
        diskGb = $Vm.DiskGb
        remark = $Vm.Remark
    }
    $script:Created.Add("模拟虚拟机 $($Vm.Name)")
    if ($row -and $row.id) { return $row.id }
    $created = Find-NamedItem (Get-ApiItems '/virtual-machines' @{ page = 1; pageSize = 1000 }) $Vm.Name
    if ($created) { return $created.id }
    return $null
}

function Publish-Camp([string]$CampId, [string]$Name) {
    if (-not $CampId) { return }
    try {
        [void](Invoke-Api -Method POST -Path "/camp/positions/$CampId/publish" -Body @{ published = $true })
        Write-Step "岗位训练营已发布：$Name"
    }
    catch {
        if ("$_" -match '404') {
            $script:ApiNotes.Add('当前服务版本没有岗位发布接口，已创建的岗位可直接在训练营中查看。')
            Write-Step '当前服务版本没有岗位发布接口，已创建的岗位可直接在训练营中查看。'
        }
        else {
            Write-Step "岗位发布已跳过：$($_.Exception.Message)"
        }
    }
}

function Add-CampIfMissing($Camp) {
    $camps = Get-ApiItems '/camp/positions' @{}
    $existing = Find-NamedItem $camps $Camp.Name
    if ($existing) {
        $script:Skipped.Add("岗位训练营 $($Camp.Name)")
        return [string]$existing.id
    }
    $created = Invoke-Api -Method POST -Path '/camp/positions' -Body $Camp.Body
    $script:Created.Add("岗位训练营 $($Camp.Name)")
    return [string]$created.id
}

function Restore-DemoPasswords {
    if (-not $script:RestorePersonIds -or $script:RestorePersonIds.Count -eq 0) { return }
    Connect-Admin $script:AdminPassword
    $unique = @($script:RestorePersonIds | Select-Object -Unique)
    foreach ($personId in $unique) {
        if (-not $personId) { continue }
        [void](Invoke-Api -Method POST -Path "/people/$personId/reset-password" -Body @{ newPassword = $DemoPassword })
        Write-Step "已将演示账号恢复为初始口令（人员 $personId）。"
    }
}

function New-ScoreSelection($Correct, [string]$Mode, [int]$Index) {
    $selected = @($Correct)
    if ($Mode -eq 'medium' -and ($Index % 2) -eq 1) { $selected = @() }
    elseif ($Mode -eq 'low' -and $Index -gt 0) { $selected = @() }
    return ,$selected
}

function Complete-ClassroomWork {
    param([string]$ClassroomId, [string]$Mode, $AnswerMap)
    if (-not $ClassroomId) { return }
    $content = Invoke-Api -Method GET -Path "/classrooms/$ClassroomId/content"
    $done = @{}
    if ($content -and $content.PSObject.Properties.Name -contains 'records') {
        foreach ($record in @($content.records)) {
            if ([string]$record.status -eq 'completed') { $done[[string]$record.resourceId] = $true }
        }
    }
    $resources = @()
    if ($content -and $content.PSObject.Properties.Name -contains 'tree') {
        foreach ($chapter in @($content.tree)) {
            foreach ($section in @($chapter.sections)) {
                foreach ($resource in @($section.resources)) { $resources += $resource }
            }
        }
    }
    foreach ($resource in $resources) {
        $resourceId = [string]$resource.id
        $kind = [string]$resource.kind
        if ($done.ContainsKey($resourceId)) { continue }
        if ($kind -eq 'casePractice') { continue }
        try {
            [void](Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/resources/$resourceId/start")
        }
        catch {
            if ($kind -eq 'casePractice') {
                $script:ApiNotes.Add("案例实操未能开始（$($resource.name)）：$($_.Exception.Message)")
                continue
            }
            throw
        }
        if ($kind -eq 'theory' -or $kind -eq 'case') {
            [void](Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/resources/$resourceId/complete")
        }
        elseif ($kind -eq 'theoryPractice') {
            $answers = @()
            if ($AnswerMap -and $AnswerMap.ContainsKey($resourceId)) {
                $index = 0
                foreach ($question in @($AnswerMap[$resourceId])) {
                    $selected = New-ScoreSelection @($question.answers) $Mode $index
                    $answers += @{ questionId = [string]$question.id; selected = @($selected) }
                    $index++
                }
            }
            if ($answers.Count -gt 0) {
                [void](Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/resources/$resourceId/submit" -Body @{ answers = @($answers) })
            }
        }
    }
}

function Submit-ExamWork {
    param([string]$ExamId, $Questions, [string]$Mode)
    if (-not $ExamId) { return }
    $start = Invoke-Api -Method POST -Path "/exam-hall/$ExamId/start"
    if ($Mode -eq 'progress') { return }
    Submit-ExamPracticalConfig $ExamId $Questions $Mode
    $answers = @()
    $index = 0
    foreach ($question in @($Questions)) {
        if ([string]$question.type -eq 'practical') { continue }
        $correct = @()
        if ($question.PSObject.Properties.Name -contains 'answers') { $correct = @($question.answers) }
        $selected = New-ScoreSelection $correct $Mode $index
        $answers += @{ paperQuestionId = [string]$question.id; selected = @($selected) }
        $index++
    }
    [void](Invoke-Api -Method POST -Path "/exam-hall/$ExamId/submit" -Body @{ answers = @($answers) })
}

function Submit-CampWork {
    param([string]$CampId, $Questions, [string]$Mode)
    if (-not $CampId) { return }
    [void](Invoke-Api -Method POST -Path "/camp/positions/$CampId/start")
    if ($Mode -eq 'progress') { return }
    $answers = @()
    $index = 0
    foreach ($question in @($Questions)) {
        $type = [string]$question.type
        if ($type -eq 'practical') {
            $text = ''
            if ($Mode -eq 'high') { $text = 'ip address write' }
            elseif ($Mode -eq 'medium') { $text = 'ip address' }
            $answers += @{ questionId = [string]$question.id; selected = @(); text = $text }
        }
        else {
            $correct = @()
            if ($question.PSObject.Properties.Name -contains 'answers') { $correct = @($question.answers) }
            $selected = New-ScoreSelection $correct $Mode $index
            $answers += @{ questionId = [string]$question.id; selected = @($selected); text = '' }
        }
        $index++
    }
    [void](Invoke-Api -Method POST -Path "/camp/positions/$CampId/submit" -Body @{ answers = @($answers) })
}

function Test-ExamSubmitted([string]$ExamId, [string]$UserId) {
    $rows = Get-ApiItems "/exams/$ExamId/students" @{}
    foreach ($row in $rows) {
        $uid = [string]$row.studentUserId
        if ($uid -ne $UserId) { continue }
        $status = [string]$row.attemptStatus
        if ($status -and $status -ne '' -and $status -ne 'notStarted') { return $status }
    }
    return ''
}

function Invoke-StudentActivity($Plan) {
    $loginName = [string]$Plan.UserName
    $personId = [string]$Plan.PersonId
    $session = $null
    try {
        $session = Connect-User $loginName $DemoPassword
    }
    catch {
        Write-Step ("学生 {0} 无法用演示初始密码登录，已跳过活动且不会重置该账号。" -f $loginName)
        $script:ApiNotes.Add("学生 $loginName 的初始密码已不可用，跳过学习/考试活动。")
        return
    }
    $needRestore = $false
    try {
        $mustChange = $false
        if ($session.user.PSObject.Properties.Name -contains 'mustChangePassword') {
            $mustChange = [bool]$session.user.mustChangePassword
        }
        if ($mustChange) {
            $changed = Invoke-Api -Method POST -Path '/auth/password' -Body @{
                currentPassword = $DemoPassword
                newPassword = $script:WorkPassword
            }
            if ($changed -and $changed.csrfToken) { $script:CsrfToken = [string]$changed.csrfToken }
            $needRestore = $true
            if ($personId) { [void]$script:RestorePersonIds.Add($personId) }
        }
        foreach ($classroomId in @($Plan.ClassroomIds)) {
            Complete-ClassroomWork $classroomId $Plan.LearnMode $script:PracticeAnswers
            try { Submit-CaseWork $classroomId $Plan.LearnMode $loginName }
            catch { Write-Step ("实操评分已跳过（{0}）：{1}" -f $loginName, $_.Exception.Message) }
        }
        foreach ($exam in @($Plan.Exams)) {
            if (-not $exam.Id) { continue }
            $status = [string]$exam.ExistingStatus
            if ($status -eq 'submitted' -or $status -eq 'graded') {
                $script:Skipped.Add("考试作答 $loginName/$($exam.Name)")
                continue
            }
            if ($status -eq 'inProgress' -and $exam.Mode -eq 'progress') {
                $script:Skipped.Add("考试作答 $loginName/$($exam.Name)（已在作答中）")
                continue
            }
            try {
                Submit-ExamWork $exam.Id $exam.Questions $exam.Mode
                $script:Created.Add("考试作答 $loginName/$($exam.Name)")
            }
            catch {
                Write-Step ("考试作答已跳过（{0}/{1}）：{2}" -f $loginName, $exam.Name, $_.Exception.Message)
            }
        }
        foreach ($camp in @($Plan.Camps)) {
            if (-not $camp.Id) { continue }
            try {
                Submit-CampWork $camp.Id $camp.Questions $camp.Mode
                $script:Created.Add("岗位作答 $loginName/$($camp.Name)")
            }
            catch {
                $message = [string]$_.Exception.Message
                if ($message -match '尚未发布') {
                    $script:ApiNotes.Add("岗位训练营尚未发布，当前服务可能缺少发布接口，已跳过作答。")
                }
                elseif ($message -match '已提交' -or $message -match '不能重复') {
                    $script:Skipped.Add("岗位作答 $loginName/$($camp.Name)")
                }
                else {
                    Write-Step ("岗位作答已跳过（{0}/{1}）：{2}" -f $loginName, $camp.Name, $message)
                }
            }
        }
    }
    catch {
        Write-Step ("学生 {0} 的学习/考试活动已跳过：{1}" -f $loginName, $_.Exception.Message)
    }
}

function Get-ResourceAnswerMap($ClassroomIds) {
    $map = @{}
    foreach ($classroomId in @($ClassroomIds | Select-Object -Unique | Where-Object { $_ })) {
        $content = Invoke-Api -Method GET -Path "/classrooms/$classroomId/content"
        if (-not $content -or -not ($content.PSObject.Properties.Name -contains 'tree')) { continue }
        foreach ($chapter in @($content.tree)) {
            foreach ($section in @($chapter.sections)) {
                foreach ($resource in @($section.resources)) {
                    if ([string]$resource.kind -ne 'theoryPractice') { continue }
                    $detail = Invoke-Api -Method GET -Path "/resources/$($resource.id)"
                    if ($detail -and $detail.PSObject.Properties.Name -contains 'questions') {
                        $map[[string]$resource.id] = @($detail.questions)
                    }
                }
            }
        }
    }
    return $map
}

function Get-PaperQuestions([string]$PaperId) {
    if (-not $PaperId) { return @() }
    $paper = Invoke-Api -Method GET -Path "/exam-papers/$PaperId"
    if ($paper -and $paper.PSObject.Properties.Name -contains 'questions') { return @($paper.questions) }
    return @()
}

function Get-CampQuestions([string]$CampId) {
    if (-not $CampId) { return @() }
    $detail = Invoke-Api -Method GET -Path "/camp/positions/$CampId"
    if ($detail -and $detail.PSObject.Properties.Name -contains 'questions') { return @($detail.questions) }
    return @()
}


function Invoke-Upload {
    param(
        [string]$Path,
        [string]$FilePath,
        [string]$FieldName = 'file',
        [hashtable]$Fields,
        [string]$FileContentType
    )
    $uri = $ApiRoot + $Path
    $boundary = '----SchoolDemo' + [guid]::NewGuid().ToString('N')
    $enc = New-Object System.Text.UTF8Encoding $false
    $ms = New-Object System.IO.MemoryStream
    if ($Fields) {
        foreach ($key in $Fields.Keys) {
            $part = "--$boundary`r`nContent-Disposition: form-data; name=`"$key`"`r`n`r`n$([string]$Fields[$key])`r`n"
            $bytes = $enc.GetBytes($part)
            $ms.Write($bytes, 0, $bytes.Length)
        }
    }
    $fileName = [System.IO.Path]::GetFileName($FilePath)
    $fileBytes = [System.IO.File]::ReadAllBytes($FilePath)
    $ctype = $FileContentType
    if (-not $ctype) { $ctype = 'application/octet-stream' }
    $header = "--$boundary`r`nContent-Disposition: form-data; name=`"$FieldName`"; filename=`"$fileName`"`r`nContent-Type: $ctype`r`n`r`n"
    $headerBytes = $enc.GetBytes($header)
    $ms.Write($headerBytes, 0, $headerBytes.Length)
    $ms.Write($fileBytes, 0, $fileBytes.Length)
    $endBytes = $enc.GetBytes("`r`n--$boundary--`r`n")
    $ms.Write($endBytes, 0, $endBytes.Length)
    $body = $ms.ToArray()
    $headers = @{ Accept = 'application/json' }
    if ($script:CsrfToken) { $headers['X-CSRF-TOKEN'] = $script:CsrfToken }
    try {
        $response = Invoke-WebRequest -Uri $uri -Method POST -WebSession $script:Session -Headers $headers -ContentType "multipart/form-data; boundary=$boundary" -Body $body -UseBasicParsing -TimeoutSec 60
        if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
        return ,(ConvertFrom-Json -InputObject $response.Content)
    }
    catch [System.Net.WebException] {
        $detail = $_.Exception.Message
        $httpResponse = $_.Exception.Response
        if ($httpResponse) {
            $status = [int]$httpResponse.StatusCode
            try {
                $stream = $httpResponse.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $text = $reader.ReadToEnd()
                if ($text) { $detail = "$status $text" } else { $detail = "$status $detail" }
            }
            catch { $detail = "$status $detail" }
        }
        throw "接口 POST $Path 失败：$detail"
    }
}

function Get-DemoTextPath([string]$Name, [string]$Content) {
    $dir = Join-Path $env:TEMP 'school-demo-files'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $path = Join-Path $dir $Name
    $enc = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($path, $Content, $enc)
    return $path
}

function Get-DemoPngPath([string]$Name) {
    $dir = Join-Path $env:TEMP 'school-demo-files'
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $path = Join-Path $dir $Name
    $bytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAoAAAAKCAYAAACNMs+9AAAAFUlEQVR42mNkYPhfz0AEYBxVSF+FAP5FDvcfryBUAAAAAElFTkSuQmCC')
    [System.IO.File]::WriteAllBytes($path, $bytes)
    return $path
}

function Get-LinkRubric {
    $switch = Find-NamedItem (Get-ApiItems '/devices' @{ page = 1; pageSize = 1000 }) '模拟交换机 SW-01'
    $router = Find-NamedItem (Get-ApiItems '/devices' @{ page = 1; pageSize = 1000 }) '模拟路由器 RT-01'
    if (-not $switch -or -not $router) { return @() }
    return @(
        @{ name='交换机主机名改为实训标识'; deviceId=[string]$switch.id; field='hostname'; interfaceName=$null; expected='SW-01-LAB'; score=20; guidance='请将交换机主机名设置为 SW-01-LAB。' }
        @{ name='交换机上联接口说明'; deviceId=[string]$switch.id; field='description'; interfaceName='ethernet0/0'; expected='uplink-to-rt'; score=15; guidance='请将交换机 ethernet0/0 的接口说明设置为 uplink-to-rt。' }
        @{ name='路由器主机名改为实训标识'; deviceId=[string]$router.id; field='hostname'; interfaceName=$null; expected='RT-01-LAB'; score=20; guidance='请将路由器主机名设置为 RT-01-LAB。' }
        @{ name='路由器互联接口说明'; deviceId=[string]$router.id; field='description'; interfaceName='ethernet0/0'; expected='uplink-to-sw'; score=15; guidance='请将路由器 ethernet0/0 的接口说明设置为 uplink-to-sw。' }
    )
}

function Ensure-ResourceRubric {
    param([string]$ResourceId, $Rules)
    if (-not $ResourceId -or -not $Rules -or @($Rules).Count -eq 0) { return }
    $detail = Invoke-Api -Method GET -Path "/resources/$ResourceId"
    $existing = @()
    if ($detail -and $detail.PSObject.Properties.Name -contains 'caseRubric' -and $detail.caseRubric) {
        $existing = @($detail.caseRubric)
    }
    if ($existing.Count -gt 0) {
        $script:Skipped.Add("实操评分表 $($detail.name)")
        return
    }
    $questions = @()
    if ($detail.PSObject.Properties.Name -contains 'questions' -and $detail.questions) {
        foreach ($question in @($detail.questions)) {
            $item = @{
                type = [string]$question.type
                stem = [string]$question.stem
                options = @($question.options)
                answers = @($question.answers)
                score = $question.score
                analysis = [string]$question.analysis
            }
            $questions += $item
        }
    }
    $body = @{
        name = [string]$detail.name
        kind = [string]$detail.kind
        durationMinutes = [int]$detail.durationMinutes
        content = $(if ($detail.content) { [string]$detail.content } else { '' })
        topology = $(if ($detail.topology) { [string]$detail.topology } else { '' })
        startupNodeId = $(if ($detail.startupNodeId) { [string]$detail.startupNodeId } else { $null })
        questions = @($questions)
        caseRubric = @($Rules)
        version = [int]$detail.version
    }
    [void](Invoke-Api -Method PUT -Path "/resources/$ResourceId" -Body $body)
    $script:Created.Add("实操评分表 $($detail.name)")
}

function Find-ResourceByName([string]$Name) {
    $courses = Get-ApiItems '/courses' @{ page = 1; pageSize = 1000 }
    foreach ($course in $courses) {
        $detail = Invoke-Api -Method GET -Path "/courses/$($course.id)"
        if (-not $detail -or -not ($detail.PSObject.Properties.Name -contains 'chapters')) { continue }
        foreach ($chapter in @($detail.chapters)) {
            foreach ($section in @($chapter.sections)) {
                foreach ($resource in @($section.resources)) {
                    if ([string]$resource.name -eq $Name) { return $resource }
                }
            }
        }
    }
    return $null
}

function Ensure-ResourceFile {
    param([string]$ResourceName, [string]$DisplayName, [string]$FilePath, [string]$FileContentType)
    $resource = Find-ResourceByName $ResourceName
    if (-not $resource) {
        $script:ApiNotes.Add("未找到课程资源 $ResourceName，跳过附件 $DisplayName。")
        return
    }
    $detail = Invoke-Api -Method GET -Path "/resources/$($resource.id)"
    $files = @()
    if ($detail -and $detail.PSObject.Properties.Name -contains 'files') { $files = @($detail.files) }
    if (Find-NamedItem $files $DisplayName) {
        $script:Skipped.Add("课程附件 $DisplayName")
        return
    }
    [void](Invoke-Upload -Path "/resources/$($resource.id)/files" -FilePath $FilePath -Fields @{ name = $DisplayName } -FileContentType $FileContentType)
    $script:Created.Add("课程附件 $DisplayName")
}

function Apply-CaseMode($Devices, [string]$Mode) {
    $result = @()
    foreach ($device in @($Devices)) {
        $cfg = @{
            hostname = [string]$device.configuration.hostname
            interfaces = @{}
        }
        if ($device.configuration.interfaces) {
            foreach ($key in @($device.configuration.interfaces.PSObject.Properties.Name)) {
                $port = $device.configuration.interfaces.$key
                $enabled = $true
                if ($port.PSObject.Properties.Name -contains 'enabled') { $enabled = [bool]$port.enabled }
                $cfg.interfaces[$key] = @{ enabled = $enabled; description = [string]$port.description }
            }
        }
        $id = [string]$device.id
        $name = [string]$device.name
        if ($Mode -eq 'high' -or $Mode -eq 'medium') {
            if ($id -eq 'simulation-switch' -or $name -match 'SW-01') { $cfg.hostname = 'SW-01-LAB' }
            if ($id -eq 'simulation-router' -or $name -match 'RT-01') { $cfg.hostname = 'RT-01-LAB' }
        }
        elseif ($Mode -eq 'low') {
            if ($id -eq 'simulation-switch' -or $name -match 'SW-01') { $cfg.hostname = 'SW-01-TEMP' }
        }
        if ($Mode -eq 'high') {
            if (($id -eq 'simulation-switch' -or $name -match 'SW-01') -and $cfg.interfaces.ContainsKey('ethernet0/0')) {
                $cfg.interfaces['ethernet0/0'].description = 'uplink-to-rt'
            }
            if (($id -eq 'simulation-router' -or $name -match 'RT-01') -and $cfg.interfaces.ContainsKey('ethernet0/0')) {
                $cfg.interfaces['ethernet0/0'].description = 'uplink-to-sw'
            }
        }
        $result += @{ id = $id; name = $name; configuration = $cfg }
    }
    return $result
}

function Submit-CaseWork {
    param([string]$ClassroomId, [string]$Mode, [string]$LoginName)
    if (-not $ClassroomId) { return }
    $content = Invoke-Api -Method GET -Path "/classrooms/$ClassroomId/content"
    if (-not $content -or -not ($content.PSObject.Properties.Name -contains 'tree')) { return }
    foreach ($chapter in @($content.tree)) {
        foreach ($section in @($chapter.sections)) {
            foreach ($resource in @($section.resources)) {
                if ([string]$resource.kind -ne 'casePractice') { continue }
                $resourceId = [string]$resource.id
                $work = $null
                try {
                    $work = Invoke-Api -Method GET -Path "/classrooms/$ClassroomId/resources/$resourceId/case-work"
                }
                catch { $work = $null }
                $status = ''
                $hasFinal = $false
                if ($work) {
                    $status = [string]$work.status
                    if ($work.PSObject.Properties.Name -contains 'grades') {
                        foreach ($grade in @($work.grades)) {
                            if ($grade.isFinal) { $hasFinal = $true }
                        }
                    }
                }
                if ($status -eq 'ended') {
                    $script:Skipped.Add("实操评分 $LoginName/$($resource.name)")
                    continue
                }
                if ($hasFinal -and $status -eq 'active') {
                    $updated = Apply-CaseMode @($work.devices) $Mode
                    $revision = [int]$work.revision
                    $workId = [string]$work.workId
                    foreach ($device in $updated) {
                        $saved = Invoke-Api -Method PUT -Path "/classrooms/$ClassroomId/resources/$resourceId/case-work/devices/$($device.id)" -Body @{
                            workId = $workId
                            revision = $revision
                            configuration = $device.configuration
                        }
                        if ($saved -and $saved.PSObject.Properties.Name -contains 'revision') { $revision = [int]$saved.revision }
                        else { $revision++ }
                    }
                    [void](Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/resources/$resourceId/case-work/finish" -Body @{
                        workId = $workId
                        revision = $revision
                    })
                    $script:Created.Add("实操评分补结束 $LoginName/$($resource.name)")
                    continue
                }
                if ($hasFinal) {
                    $script:Skipped.Add("实操评分 $LoginName/$($resource.name)")
                    continue
                }
                try {
                    [void](Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/resources/$resourceId/start")
                }
                catch {
                    $message = [string]$_.Exception.Message
                    if ($message -notmatch '已开始' -and $message -notmatch '实操') {
                        Write-Step ("实操开始已跳过（{0}/{1}）：{2}" -f $LoginName, $resource.name, $message)
                    }
                }
                $work = Invoke-Api -Method GET -Path "/classrooms/$ClassroomId/resources/$resourceId/case-work"
                if (-not $work) { continue }
                $status = [string]$work.status
                $hasFinal = $false
                if ($work.PSObject.Properties.Name -contains 'grades') {
                    foreach ($grade in @($work.grades)) {
                        if ($grade.isFinal) { $hasFinal = $true }
                    }
                }
                if ($status -eq 'ended' -or $hasFinal) {
                    $script:Skipped.Add("实操评分 $LoginName/$($resource.name)")
                    continue
                }
                if ($status -ne 'active') {
                    $script:ApiNotes.Add("实操会话不是进行中状态（$LoginName/$($resource.name)：$status），跳过评分。")
                    continue
                }
                $updated = Apply-CaseMode @($work.devices) $Mode
                $revision = [int]$work.revision
                $workId = [string]$work.workId
                foreach ($device in $updated) {
                    $saved = Invoke-Api -Method PUT -Path "/classrooms/$ClassroomId/resources/$resourceId/case-work/devices/$($device.id)" -Body @{
                        workId = $workId
                        revision = $revision
                        configuration = $device.configuration
                    }
                    if ($saved -and $saved.PSObject.Properties.Name -contains 'revision') { $revision = [int]$saved.revision }
                    else { $revision++ }
                }
                [void](Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/resources/$resourceId/case-work/finish" -Body @{
                    workId = $workId
                    revision = $revision
                })
                $script:Created.Add("实操评分 $LoginName/$($resource.name)")
            }
        }
    }
}

function Submit-ExamPracticalConfig {
    param([string]$ExamId, $Questions, [string]$Mode)
    foreach ($question in @($Questions)) {
        $type = [string]$question.type
        if ($type -ne 'practical') { continue }
        $questionId = [string]$question.id
        $env = Invoke-Api -Method GET -Path "/exam-hall/$ExamId/practical/$questionId"
        if (-not $env -or -not ($env.PSObject.Properties.Name -contains 'devices')) { continue }
        $updated = Apply-CaseMode @($env.devices) $Mode
        $revision = 0
        if ($env.PSObject.Properties.Name -contains 'revision') { $revision = [int]$env.revision }
        foreach ($device in $updated) {
            $saved = Invoke-Api -Method PUT -Path "/exam-hall/$ExamId/practical/$questionId/devices/$($device.id)" -Body @{
                revision = $revision
                configuration = $device.configuration
            }
            if ($saved -and $saved.PSObject.Properties.Name -contains 'revision') { $revision = [int]$saved.revision }
            else { $revision++ }
        }
    }
}

function Upload-CaseEvidence {
    param([string]$ClassroomId)
    if (-not $ClassroomId) { return }
    $sessions = Get-ApiItems "/classrooms/$ClassroomId/case-sessions" @{ status = 'all' }
    $png = Get-DemoPngPath 'case-evidence.png'
    foreach ($session in @($sessions)) {
        $sid = [string]$session.sessionId
        if (-not $sid) { $sid = [string]$session.id }
        if (-not $sid) { continue }
        $evidenceCount = 0
        if ($session.PSObject.Properties.Name -contains 'evidenceCount') { $evidenceCount = [int]$session.evidenceCount }
        if ($evidenceCount -gt 0) { $script:Skipped.Add("实操取证 $sid"); continue }
        $existing = @()
        try { $existing = Get-ApiItems "/classrooms/case-sessions/$sid/evidences" @{} } catch { $existing = @() }
        if ($existing -and @($existing).Count -gt 0) {
            $script:Skipped.Add("实操取证 $sid")
            continue
        }
        $note = '联调实操截图：已按评分表核对应主机名和互联接口说明。'
        try {
            [void](Invoke-Upload -Path "/classrooms/case-sessions/$sid/evidences" -FilePath $png -Fields @{ note = $note } -FileContentType 'image/png')
            $script:Created.Add("实操取证 $sid")
        }
        catch {
            $script:ApiNotes.Add("实操取证未能写入（$sid）：$($_.Exception.Message)")
        }
    }
}

function Close-ClassroomIfOpen {
    param([string]$ClassroomId, [string]$Name)
    if (-not $ClassroomId) { return }
    $row = Find-NamedItem (Get-ApiItems '/classrooms' @{ page = 1; pageSize = 1000 }) $Name
    $status = ''
    if ($row -and $row.PSObject.Properties.Name -contains 'status') { $status = [string]$row.status }
    if ($status -eq 'closed') {
        $script:Skipped.Add("结课 $Name")
        return
    }
    [void](Invoke-Api -Method POST -Path "/classrooms/$ClassroomId/close")
    $script:Created.Add("结课 $Name")
}

function Close-ExamIfSubmitted {
    param([string]$ExamId, [string]$Name)
    if (-not $ExamId) { return }
    $examRow = Find-NamedItem (Get-ApiItems '/exams' @{ page = 1; pageSize = 1000 }) $Name
    $status = ''
    if ($examRow -and $examRow.PSObject.Properties.Name -contains 'status') { $status = [string]$examRow.status }
    $submitted = 0
    foreach ($row in (Get-ApiItems "/exams/$ExamId/students" @{})) {
        if ([string]$row.attemptStatus -in @('submitted','graded')) { $submitted++ }
    }
    if ($status -eq 'closed') { $script:Skipped.Add("结束考试 $Name"); return }
    if ($submitted -eq 0) {
        $script:ApiNotes.Add("$Name 尚无已提交作答，未结束考试。")
        return
    }
    [void](Invoke-Api -Method POST -Path "/exams/$ExamId/close")
    $script:Created.Add("结束考试 $Name")
}

function Get-ClassroomStatus([string]$ClassroomId) {
    if (-not $ClassroomId) { return '' }
    $items = Get-ApiItems '/classrooms' @{ page = 1; pageSize = 1000 }
    foreach ($row in $items) {
        if ([string]$row.id -eq $ClassroomId) {
            if ($row.PSObject.Properties.Name -contains 'status') { return [string]$row.status }
        }
    }
    return ''
}


try {
    $plain = Get-PlainPassword
    if ([string]::IsNullOrWhiteSpace($plain)) { throw '请提供管理员密码。' }
    Connect-Admin $plain
    $script:AdminPassword = $plain
    $plain = $null
    $Password = $null

    $boot = Invoke-Api -Method POST -Path '/simulation/bootstrap'
    if ($boot -and $boot.PSObject.Properties.Name -contains 'message' -and $boot.message) { Write-Step ([string]$boot.message) } else { Write-Step '已请求创建模拟环境。' }

    $professionIds = @{
        '260301' = Add-Profession '机电一体化技术' '260301' '机械、电气与自动控制相结合的高职专业。'
        '260103' = Add-Profession '数控技术' '260103' '数控加工编程、工艺与机床维护。'
        '260304' = Add-Profession '工业机器人技术' '260304' '工业机器人编程、调试与系统集成。'
    }
    $classIds = @{
        '机电 2024 级 1 班' = Add-Class '机电 2024 级 1 班' $professionIds['260301'] '机电一体化技术一年级 1 班'
        '机电 2024 级 2 班' = Add-Class '机电 2024 级 2 班' $professionIds['260301'] '机电一体化技术一年级 2 班'
        '数控 2024 级 1 班' = Add-Class '数控 2024 级 1 班' $professionIds['260103'] '数控技术一年级 1 班'
    }

    $people = @(
        @{ Kind='teacher'; Name='王建军'; UserName='wangjj'; Number='T2024001'; ClassId=$null; Gender='男'; Phone='13800001001'; Email='wangjj@jxjx.demo'; Role='Teacher' }
        @{ Kind='teacher'; Name='李敏'; UserName='limin'; Number='T2024002'; ClassId=$null; Gender='女'; Phone='13800001002'; Email='limin@jxjx.demo'; Role='Teacher' }
        @{ Kind='teacher'; Name='陈浩'; UserName='chenhao'; Number='T2024003'; ClassId=$null; Gender='男'; Phone='13800001003'; Email='chenhao@jxjx.demo'; Role='Teacher' }
        @{ Kind='student'; Name='赵子涵'; UserName='zhaozh'; Number='2024010101'; ClassId=$classIds['机电 2024 级 1 班']; Gender='男'; Phone='13900002001'; Email='zhaozh@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='钱思远'; UserName='qiansy'; Number='2024010102'; ClassId=$classIds['机电 2024 级 1 班']; Gender='男'; Phone='13900002002'; Email='qiansy@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='孙佳怡'; UserName='sunjy'; Number='2024010103'; ClassId=$classIds['机电 2024 级 1 班']; Gender='女'; Phone='13900002003'; Email='sunjy@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='周明轩'; UserName='zhoumx'; Number='2024010104'; ClassId=$classIds['机电 2024 级 1 班']; Gender='男'; Phone='13900002004'; Email='zhoumx@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='吴雨桐'; UserName='wuyt'; Number='2024010201'; ClassId=$classIds['机电 2024 级 2 班']; Gender='女'; Phone='13900002005'; Email='wuyt@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='郑浩然'; UserName='zhenghr'; Number='2024010202'; ClassId=$classIds['机电 2024 级 2 班']; Gender='男'; Phone='13900002006'; Email='zhenghr@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='冯晓萱'; UserName='fengxx'; Number='2024020101'; ClassId=$classIds['数控 2024 级 1 班']; Gender='女'; Phone='13900002007'; Email='fengxx@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='蒋梓豪'; UserName='jiangzh'; Number='2024020102'; ClassId=$classIds['数控 2024 级 1 班']; Gender='男'; Phone='13900002008'; Email='jiangzh@jxjx.demo'; Role='Student' }
    )
    foreach ($person in $people) { Add-Person $person }

    $teacherIds = @{}
    foreach ($row in (Get-ApiItems '/teaching/teachers' @{})) {
        $teacherIds[[string]$row.userName] = [string]$row.id
    }
    $studentIds = @{}
    foreach ($row in (Get-ApiItems '/teaching/students' @{ page = 1; pageSize = 1000 })) {
        # 该接口返回 userId / name / number，不返回账号；用学号对齐演示学生。
        $studentIds[[string]$row.number] = [string]$row.userId
    }
    foreach ($need in @('wangjj','limin','chenhao')) {
        if (-not $teacherIds.ContainsKey($need)) { throw "未找到教师账号 $need" }
    }

    $seriesId = Add-CourseSeries '机电专业核心课系列' '面向机电一体化与数控专业的核心课程系列。'

    $drawingId = Add-Course @{
        Name = '机械制图基础'
        Type = 'general'
        Description = '讲授机械制图国家标准、三视图与尺寸标注，为后续专业课程打下读图和绘图基础。'
        SeriesId = $null
        Chapters = @(
            @{
                Name = '制图标准与基本规定'
                Sections = @(
                    @{
                        Name = '图纸幅面与比例'
                        DurationMinutes = 45
                        Resources = @(
                            @{ name='国家标准与图纸幅面'; kind='theory'; durationMinutes=20; content='机械图样应执行现行国家标准。常用图幅有 A0 至 A4，比例需优先采用标准系列，并在标题栏中正确填写。图框、标题栏位置和字体高度也应符合规范，避免随意缩小图线或改动图框。' }
                            @{ name='制图规范练习'; kind='theoryPractice'; durationMinutes=15; questions=@(
                                @{ type='single'; stem='机械图样优先采用的图纸幅面系列是？'; options=@('A0–A4','B1–B5','任意自定义幅面','仅可使用 A3'); answers=@(0); score=10; analysis='国家标准优先采用 A0 至 A4 图幅。' }
                                @{ type='judge'; stem='比例可以在同一张图样中随意混用且不必标注。'; options=@('正确','错误'); answers=@(1); score=10; analysis='比例应规范选用并正确标注。' }
                            )}
                        )
                    }
                )
            }
            @{
                Name = '三视图与尺寸标注'
                Sections = @(
                    @{
                        Name = '三视图投影关系'
                        DurationMinutes = 50
                        Resources = @(
                            @{ name='三视图的投影规律'; kind='theory'; durationMinutes=25; content='主视图、俯视图和左视图应满足“长对正、高平齐、宽相等”。读图时先看主视图把握形体特征，再结合另外两个视图还原空间形状，注意虚线和不可见轮廓。' }
                            @{ name='尺寸标注案例'; kind='case'; durationMinutes=20; content='以阶梯轴为例，应在能反映形状特征的视图上集中标注直径和长度，避免重复标注。圆角、倒角和键槽尺寸需完整给出，并注意基准选择，保证加工和检验有依据。' }
                            @{ name='三视图读图练习'; kind='theoryPractice'; durationMinutes=15; questions=@(
                                @{ type='single'; stem='三视图中“长对正”指的是哪两个视图的对应关系？'; options=@('主视图与俯视图','主视图与左视图','俯视图与左视图','任意两个视图'); answers=@(0); score=10; analysis='' }
                                @{ type='multiple'; stem='下列哪些属于三视图投影规律？'; options=@('长对正','高平齐','宽相等','直径必须标注在左视图'); answers=@(0,1,2); score=10; analysis='' }
                                @{ type='judge'; stem='尺寸一般不应重复标注。'; options=@('正确','错误'); answers=@(0); score=10; analysis='' }
                            )}
                        )
                    }
                )
            }
        )
    }

    $cncId = Add-Course @{
        Name = '数控机床电气控制'
        Type = 'core'
        Description = '围绕数控机床电气原理、常见故障判断与安全操作，培养现场调试能力。'
        SeriesId = $seriesId
        Chapters = @(
            @{
                Name = '电气原理与安全'
                Sections = @(
                    @{
                        Name = '电气控制基础'
                        DurationMinutes = 40
                        Resources = @(
                            @{ name='数控机床电气组成'; kind='theory'; durationMinutes=20; content='数控机床电气系统通常包括电源模块、数控装置、伺服驱动、输入输出和保护回路。识读电气原理图时，应先分清主电路与控制电路，再追踪急停、限位和复位信号。' }
                            @{ name='电气安全要点'; kind='case'; durationMinutes=15; content='实训前必须确认急停有效、防护罩到位，并禁止带电插拔伺服接头。出现异常报警时，应先记录报警号，再按规程切断动力电，不得擅自短接保护装置。' }
                            @{ name='电气基础练习'; kind='theoryPractice'; durationMinutes=15; questions=@(
                                @{ type='single'; stem='数控机床出现异常报警时，首先应？'; options=@('记录报警号并按规程处理','立即短接保护装置','直接更换伺服电机','关闭总电源后不再检查'); answers=@(0); score=10; analysis='' }
                                @{ type='judge'; stem='实训中可以短接急停回路以便连续加工。'; options=@('正确','错误'); answers=@(1); score=10; analysis='' }
                            )}
                        )
                    }
                    @{
                        Name = '模拟联调准备'
                        DurationMinutes = 30
                        Resources = @(
                            @{ name='电气联调步骤说明'; kind='theory'; durationMinutes=20; content='联调建议在模拟节点上先检查设备连通和接口配置，再进行参数核对。确认急停、限位反馈正常后，才能进行下一步运动调试。本节配合平台内置模拟环境使用。' }
                            @{ name='模拟节点联调实操'; kind='casePractice'; durationMinutes=25; content='在模拟计算节点上完成交换机与路由器的基础连通检查，用于课堂实操演示。'; topology='模拟交换机 SW-01 与模拟路由器 RT-01 互联，用于课堂电气联调准备。'; startupNodeId='simulation-node' }
                        )
                    }
                )
            }
        )
    }

    $null = Add-Course @{
        Name = '工业机器人操作入门'
        Type = 'open'
        Description = '介绍工业机器人坐标系、示教编程与安全操作，适合作为开放课程先行学习。'
        SeriesId = $null
        Chapters = @(
            @{
                Name = '机器人基础'
                Sections = @(
                    @{
                        Name = '坐标系与示教'
                        DurationMinutes = 40
                        Resources = @(
                            @{ name='机器人坐标系简介'; kind='theory'; durationMinutes=20; content='常用坐标系包括世界坐标系、基坐标系、工具坐标系和用户坐标系。示教时要明确当前坐标系，慢速接近工件，避免干涉。' }
                            @{ name='示教安全练习'; kind='theoryPractice'; durationMinutes=15; questions=@(
                                @{ type='single'; stem='示教接近工件时应？'; options=@('使用慢速并确认坐标系','以最快速度贴近','关闭安全光栅后操作','由他人在旁推动手臂'); answers=@(0); score=10; analysis='' }
                                @{ type='judge'; stem='示教前不必确认当前工具坐标系。'; options=@('正确','错误'); answers=@(1); score=10; analysis='' }
                            )}
                        )
                    }
                )
            }
        )
    }

    $now = [DateTime]::UtcNow
    $openAt = Get-IsoUtc ($now.AddDays(-1))
    $closeAt = Get-IsoUtc ($now.AddDays(30))
    $room1Id = Add-Classroom @{
        Name = '机电 2024 级机械制图课堂'
        CourseId = $drawingId
        TeacherUserId = $teacherIds['wangjj']
        OpenAt = $openAt
        CloseAt = $closeAt
        ClassIds = @($classIds['机电 2024 级 1 班'])
    }
    $null = Add-Classroom @{
        Name = '数控 2024 级电气控制实训课堂'
        CourseId = $cncId
        TeacherUserId = $teacherIds['limin']
        OpenAt = $openAt
        CloseAt = $closeAt
        ClassIds = @($classIds['数控 2024 级 1 班'])
    }

    if ($room1Id) {
        $groups = Get-ApiItems "/classrooms/$room1Id/groups" @{}
        if (Find-NamedItem $groups '制图第 1 组') {
            $script:Skipped.Add('课堂分组 制图第 1 组')
        }
        else {
            $memberIds = @()
            foreach ($number in @('2024010101','2024010102','2024010103')) {
                if ($studentIds.ContainsKey($number)) { $memberIds += $studentIds[$number] }
            }
            if ($memberIds.Count -gt 0) {
                [void](Invoke-Api -Method POST -Path "/classrooms/$room1Id/groups" -Body @{ name='制图第 1 组'; userIds=@($memberIds); remark='课堂协作演示分组' })
                $script:Created.Add('课堂分组 制图第 1 组')
            }
        }
    }

    $banks = Get-ApiItems '/exam-question-banks' @{ page = 1; pageSize = 1000 }
    $bank = Find-NamedItem $banks '机械制图理论题库'
    if ($bank) {
        $script:Skipped.Add('题库 机械制图理论题库')
        $bankId = $bank.id
    }
    else {
        $bank = Invoke-Api -Method POST -Path '/exam-question-banks' -Body @{ name='机械制图理论题库'; type='theory' }
        $bankId = $bank.id
        $script:Created.Add('题库 机械制图理论题库')
    }

    $papers = Get-ApiItems '/exam-papers' @{ page = 1; pageSize = 1000 }
    $paper = Find-NamedItem $papers '机械制图阶段测验卷'
    if ($paper) {
        $script:Skipped.Add('试卷 机械制图阶段测验卷')
        $paperId = $paper.id
    }
    else {
        $existingQuestions = Get-ApiItems '/exam-questions' @{ page = 1; pageSize = 1000; bankId = $bankId }
        $stems = @(
            @{ stem='机械图样优先采用的图纸幅面是？'; type='single'; options=@('A0–A4','B5–B0','自定义任意尺寸','仅允许 A1'); answers=@(0); score=10; analysis='国家标准优先采用 A0 至 A4。' }
            @{ stem='主视图与俯视图应满足的投影关系是？'; type='single'; options=@('长对正','高平齐','宽相等','半径相等'); answers=@(0); score=10; analysis='' }
            @{ stem='下列属于三视图投影规律的有？'; type='multiple'; options=@('长对正','高平齐','宽相等','所有虚线均可省略'); answers=@(0,1,2); score=15; analysis='' }
            @{ stem='同一要素的尺寸一般不应重复标注。'; type='judge'; options=@('正确','错误'); answers=@(0); score=10; analysis='' }
            @{ stem='比例可以不标注，由读图者自行估计。'; type='judge'; options=@('正确','错误'); answers=@(1); score=10; analysis='' }
        )
        $questionIds = @()
        foreach ($item in $stems) {
            $found = Find-NamedItem $existingQuestions $item.stem 'stem'
            if ($found) { $questionIds += $found.id; continue }
            $payload = @{ type=$item.type; stem=$item.stem; options=@($item.options); answers=@($item.answers); score=$item.score; analysis=$item.analysis; bankId=$bankId }
            $created = Invoke-Api -Method POST -Path '/exam-questions' -Body $payload
            $questionIds += $created.id
            $script:Created.Add("试题 $($item.stem)")
        }
        $paper = Invoke-Api -Method POST -Path '/exam-papers' -Body @{ name='机械制图阶段测验卷'; description='考查图幅、三视图投影关系与尺寸标注基本要求。'; remark='演示用阶段测验' }
        $paperId = $paper.id
        $items = @($questionIds | ForEach-Object { @{ questionId = $_ } })
        [void](Invoke-Api -Method POST -Path "/exam-papers/$paperId/questions" -Body @{ items = $items })
        $script:Created.Add('试卷 机械制图阶段测验卷')
    }

    $exams = Get-ApiItems '/exams' @{ page = 1; pageSize = 1000 }
    $exam = Find-NamedItem $exams '2024 学年机械制图阶段测验'
    if ($exam) {
        $script:Skipped.Add('考试 2024 学年机械制图阶段测验')
    }
    else {
        $createdExam = Invoke-Api -Method POST -Path '/exams' -Body @{
            name = '2024 学年机械制图阶段测验'
            paperId = $paperId
            description = '机电 2024 级 1 班阶段测验，时长 90 分钟。'
            startAt = Get-IsoUtc ($now.AddHours(-2))
            endAt = Get-IsoUtc ($now.AddDays(7))
            durationMinutes = 90
            classIds = @($classIds['机电 2024 级 1 班'])
        }
        [void](Invoke-Api -Method POST -Path "/exams/$($createdExam.id)/open")
        $script:Created.Add('考试 2024 学年机械制图阶段测验')
    }

    $camps = Get-ApiItems '/camp/positions' @{}
    $camp = Find-NamedItem $camps '数控机床调试员岗位训练营'
    if ($camp) {
        $script:Skipped.Add('岗位训练营 数控机床调试员岗位训练营')
        $campId = $camp.id
    }
    else {
        $camp = Invoke-Api -Method POST -Path '/camp/positions' -Body @{
            name = '数控机床调试员岗位训练营'
            description = '面向数控机床现场调试岗位，考查电气安全、联调步骤和基本故障判断。可在模拟计算节点上完成相关准备。'
            durationMinutes = 60
            startupNodeId = 'simulation-node'
            skills = @(
                @{ name='电气安全'; subName='防护与急停'; ability='能正确执行电气安全规程并处理急停。' }
                @{ name='系统联调'; subName='信号核对'; ability='能按步骤完成模拟环境下的连通与信号检查。' }
                @{ name='故障判断'; subName='报警分析'; ability='能根据报警信息判断常见电气故障。' }
            )
            questions = @(
                @{ type='single'; stem='出现电气异常时，首先应该？'; options=@('记录现象并按规程切断危险源','立即短接保护','继续加工观察','直接更换全部电缆'); answers=@(0); score=10; analysis='先保障安全并保留现场信息。'; skillIndex=0; rubric=@(); practicalRubric=@() }
                @{ type='judge'; stem='实训中可以短接急停回路以便连续调试。'; options=@('正确','错误'); answers=@(1); score=10; analysis=''; skillIndex=0; rubric=@(); practicalRubric=@() }
                @{ type='multiple'; stem='联调前应确认的事项包括？'; options=@('急停有效','限位反馈正常','设备连通正常','关闭所有日志'); answers=@(0,1,2); score=15; analysis=''; skillIndex=1; rubric=@(); practicalRubric=@() }
                @{ type='single'; stem='伺服驱动报警后较合理的做法是？'; options=@('先记录报警号再按手册排查','反复上电直至消失','加大倍率继续运行','拆掉编码器线再试'); answers=@(0); score=10; analysis=''; skillIndex=2; rubric=@(); practicalRubric=@() }
                @{ type='practical'; stem='请在模拟环境中完成接口地址配置并保存。'; options=@(); answers=@(); score=20; analysis=''; skillIndex=1; practicalRubric=@(); rubric=@(
                    @{ item='接口地址配置正确'; keyword='ip address'; score=10 }
                    @{ item='配置已保存'; keyword='write'; score=10 }
                )}
            )
        }
        $campId = $camp.id
        $script:Created.Add('岗位训练营 数控机床调试员岗位训练营')
    }
    if ($campId) {
        try {
            [void](Invoke-Api -Method POST -Path "/camp/positions/$campId/publish" -Body @{ published = $true })
            Write-Step '岗位训练营已发布。'
        }
        catch {
            if ("$_" -match '404') { Write-Step '当前服务版本没有岗位发布接口，已创建的岗位可直接在训练营中查看。' }
            else { Write-Step "岗位发布已跳过：$($_.Exception.Message)" }
        }
    }

    $classIds['工业机器人 2024 级 1 班'] = Add-Class '工业机器人 2024 级 1 班' $professionIds['260304'] '工业机器人技术一年级 1 班'

    $morePeople = @(
        @{ Kind='student'; Name='曹子墨'; UserName='caozm'; Number='2024010203'; ClassId=$classIds['机电 2024 级 2 班']; Gender='男'; Phone='13900002009'; Email='caozm@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='邓欣怡'; UserName='dengxy'; Number='2024010204'; ClassId=$classIds['机电 2024 级 2 班']; Gender='女'; Phone='13900002010'; Email='dengxy@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='刘晨'; UserName='liuc'; Number='2024020103'; ClassId=$classIds['数控 2024 级 1 班']; Gender='男'; Phone='13900002011'; Email='liuc@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='黄思琪'; UserName='huangsq'; Number='2024020104'; ClassId=$classIds['数控 2024 级 1 班']; Gender='女'; Phone='13900002012'; Email='huangsq@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='何俊杰'; UserName='hejj'; Number='2024030101'; ClassId=$classIds['工业机器人 2024 级 1 班']; Gender='男'; Phone='13900002013'; Email='hejj@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='林诗涵'; UserName='linsh'; Number='2024030102'; ClassId=$classIds['工业机器人 2024 级 1 班']; Gender='女'; Phone='13900002014'; Email='linsh@jxjx.demo'; Role='Student' }
        @{ Kind='student'; Name='高宇航'; UserName='gaoyh'; Number='2024030103'; ClassId=$classIds['工业机器人 2024 级 1 班']; Gender='男'; Phone='13900002015'; Email='gaoyh@jxjx.demo'; Role='Student' }
    )
    foreach ($person in $morePeople) { Add-Person $person }

    $teacherIds = Get-TeacherNameMap
    $studentIds = Get-StudentNumberMap
    $personIds = Get-PersonIdMap
    foreach ($need in @('wangjj','limin','chenhao')) {
        if (-not $teacherIds.ContainsKey($need)) { throw "未找到教师账号 $need" }
    }

    if ($drawingId) {
        Ensure-CourseContent $drawingId @(
            @{
                Name = '剖视图与断面图'
                Sections = @(
                    @{
                        Name = '剖视读图要点'
                        DurationMinutes = 45
                        Resources = @(
                            @{ name='剖视图的基本画法'; kind='theory'; durationMinutes=20; content='当机件内部结构较复杂时，可假想用剖切面剖开机件，将处在观察者与剖切面之间的部分移去，再将其余部分向投影面投射，得到剖视图。全剖、半剖和局部剖应依据对称性和表达需要选用，并按规定标注剖切符号与剖切位置。' }
                            @{ name='剖视图选用案例'; kind='case'; durationMinutes=15; content='对称的壳体类零件常用半剖：一半表达外形，一半表达内部腔体和肋板。轴套类零件孔、槽较多时宜采用全剖，以免虚线过多。局部剖则用于只需表达某一处内部结构、又不希望破坏外形连续的情况。' }
                            @{ name='剖视与断面练习'; kind='theoryPractice'; durationMinutes=15; questions=@(
                                @{ type='single'; stem='表达对称机件内外结构时，较常采用的是？'; options=@('半剖视图','局部放大图','轴测图','展开图'); answers=@(0); score=10; analysis='半剖可同时表达外形与内部结构。' }
                                @{ type='judge'; stem='剖视图中，已表达清楚的内部结构仍应再以虚线重复画出。'; options=@('正确','错误'); answers=@(1); score=10; analysis='已表达清楚的结构一般不再重复画虚线。' }
                                @{ type='multiple'; stem='剖视图常用的种类包括？'; options=@('全剖','半剖','局部剖','任意省略全部标注'); answers=@(0,1,2); score=10; analysis='' }
                            )}
                        )
                    }
                )
            }
        )
    }

    $hydraulicId = Add-Course @{
        Name = '液压与气动技术'
        Type = 'core'
        Description = '介绍液压与气动系统的组成、基本回路和常见故障判断，为机电专业实训提供理论基础。'
        SeriesId = $seriesId
        Chapters = @(
            @{
                Name = '液压系统基础'
                Sections = @(
                    @{
                        Name = '系统组成与回路'
                        DurationMinutes = 45
                        Resources = @(
                            @{ name='液压系统的基本组成'; kind='theory'; durationMinutes=20; content='液压系统一般由动力元件、执行元件、控制元件、辅助元件和工作介质组成。识读液压原理图时，应先找到油泵和执行元件，再沿压力油路、回油路和泄漏油路追踪控制阀的作用。' }
                            @{ name='溢流阀调压案例'; kind='case'; durationMinutes=15; content='系统压力异常升高时，应先检查溢流阀调定值和阀芯是否卡滞，再查看滤油器和油温。禁止在未卸压时拆开高压管路。实训中要以压力表读数为依据，逐步调整，避免一次大幅旋转调压螺钉。' }
                            @{ name='液压基础练习'; kind='theoryPractice'; durationMinutes=15; questions=@(
                                @{ type='single'; stem='液压系统中把机械能转换为液压能的元件是？'; options=@('液压泵','液压缸','溢流阀','油箱'); answers=@(0); score=10; analysis='' }
                                @{ type='judge'; stem='未卸压时也可以拆开高压软管进行检查。'; options=@('正确','错误'); answers=@(1); score=10; analysis='' }
                                @{ type='multiple'; stem='液压系统的基本组成部分包括？'; options=@('动力元件','执行元件','控制元件','辅助元件'); answers=@(0,1,2,3); score=10; analysis='' }
                            )}
                        )
                    }
                )
            }
        )
    }

    $robotCourse = Find-NamedItem (Get-ApiItems '/courses' @{ page = 1; pageSize = 1000 }) '工业机器人操作入门'
    $robotCourseId = $null
    if ($robotCourse) { $robotCourseId = [string]$robotCourse.id }
    if ($robotCourseId) {
        Ensure-CourseContent $robotCourseId @(
            @{
                Name = '安全与基本维护'
                Sections = @(
                    @{
                        Name = '安全防护与点检'
                        DurationMinutes = 40
                        Resources = @(
                            @{ name='机器人安全防护要点'; kind='theory'; durationMinutes=20; content='进入机器人工作单元前，应确认安全光栅、围栏门锁和急停有效。示教或点检时须切换到适当的安全速度，保持在可逃离范围，并避免身体进入可能的夹持区域。' }
                            @{ name='班前点检案例'; kind='case'; durationMinutes=15; content='班前应检查机器人本体有无漏油、电缆拖链有无破损、工具安装是否牢固，并在示教器上确认当前工具坐标系。发现异常应停机汇报，不得拆除安全装置后继续生产。' }
                            @{ name='机器人安全练习'; kind='theoryPractice'; durationMinutes=15; questions=@(
                                @{ type='single'; stem='进入机器人工作单元前首先应确认？'; options=@('光栅、围栏和急停有效','关闭所有安全装置','以自动模式直接进入','拆除围栏以便观察'); answers=@(0); score=10; analysis='' }
                                @{ type='judge'; stem='发现安全光栅故障后，可以拆除后继续生产。'; options=@('正确','错误'); answers=@(1); score=10; analysis='' }
                            )}
                        )
                    }
                )
            }
        )
    }

    $room2Id = Add-Classroom @{
        Name = '机电 2024 级 2 班机械制图课堂'
        CourseId = $drawingId
        TeacherUserId = $teacherIds['wangjj']
        OpenAt = $openAt
        CloseAt = $closeAt
        ClassIds = @($classIds['机电 2024 级 2 班'])
    }
    $roomCnc = Find-NamedItem (Get-ApiItems '/classrooms' @{ page = 1; pageSize = 1000 }) '数控 2024 级电气控制实训课堂'
    $roomCncId = $null
    if ($roomCnc) { $roomCncId = [string]$roomCnc.id }
    if ($roomCncId) { Ensure-ClassroomEnrollment $roomCncId $classIds['数控 2024 级 1 班'] }

    $roomRobotId = $null
    if ($robotCourseId) {
        $roomRobotId = Add-Classroom @{
            Name = '工业机器人 2024 级操作实训课堂'
            CourseId = $robotCourseId
            TeacherUserId = $teacherIds['chenhao']
            OpenAt = $openAt
            CloseAt = $closeAt
            ClassIds = @($classIds['工业机器人 2024 级 1 班'])
        }
    }

    $null = Add-GroupIfMissing $room2Id '制图第 2 组' @(
        $studentIds['2024010201'], $studentIds['2024010202'], $studentIds['2024010203']
    ) '机电 2 班制图协作分组'
    $cncGroupId = Add-GroupIfMissing $roomCncId '电气联调第 1 组' @(
        $studentIds['2024020101'], $studentIds['2024020102']
    ) '数控电气联调演示分组'
    $null = Add-GroupIfMissing $roomRobotId '机器人第 1 组' @(
        $studentIds['2024030101'], $studentIds['2024030102']
    ) '工业机器人实训协作分组'

    if ($roomCncId -and $cncGroupId) {
        $assignments = Get-ApiItems "/classrooms/$roomCncId/cabinet-assignments" @{}
        if ($assignments -and @($assignments).Count -gt 0) {
            $script:Skipped.Add('机柜分配 模拟实训机柜')
        }
        else {
            $cabinet = Find-NamedItem (Get-ApiItems '/cabinets' @{ page = 1; pageSize = 1000 }) '模拟实训机柜'
            $caseResource = $null
            $cncContent = Invoke-Api -Method GET -Path "/classrooms/$roomCncId/content"
            if ($cncContent -and $cncContent.PSObject.Properties.Name -contains 'tree') {
                foreach ($chapter in @($cncContent.tree)) {
                    foreach ($section in @($chapter.sections)) {
                        foreach ($resource in @($section.resources)) {
                            if ([string]$resource.name -eq '模拟节点联调实操') { $caseResource = $resource; break }
                        }
                    }
                }
            }
            if ($cabinet -and $caseResource) {
                try {
                    [void](Invoke-Api -Method POST -Path "/classrooms/$roomCncId/cabinet-assignments" -Body @{
                        cabinetIds = @($cabinet.id)
                        groupId = $cncGroupId
                        resourceId = [string]$caseResource.id
                    })
                    $script:Created.Add('机柜分配 模拟实训机柜 / 电气联调第 1 组')
                }
                catch {
                    $script:ApiNotes.Add("机柜分配未能写入：$($_.Exception.Message)")
                    Write-Step ("机柜分配已跳过：{0}" -f $_.Exception.Message)
                }
            }
            else {
                $script:ApiNotes.Add('未找到模拟实训机柜或联调实操资源，跳过机柜分配。')
            }
        }
    }

    $drawingBankId = $bankId
    if ($drawingBankId) {
        $null = Add-BankQuestions $drawingBankId @(
            @{ stem='表达对称机件内外结构时较常采用半剖视图。'; type='judge'; options=@('正确','错误'); answers=@(0); score=10; analysis='半剖可同时表达外形和内部结构。' }
            @{ stem='剖视图中已表达清楚的内部结构一般不必再画虚线。'; type='judge'; options=@('正确','错误'); answers=@(0); score=10; analysis='' }
            @{ stem='下列属于剖视图常用种类的是？'; type='multiple'; options=@('全剖','半剖','局部剖','不必标注即可任意剖切'); answers=@(0,1,2); score=15; analysis='' }
        )
    }

    $cncBankId = Add-BankIfMissing '数控电气安全题库' 'theory'
    $cncQuestionIds = Add-BankQuestions $cncBankId @(
        @{ stem='数控机床出现异常报警时，首先应？'; type='single'; options=@('记录报警号并按规程处理','立即短接保护装置','直接更换伺服电机','关闭总电源后不再检查'); answers=@(0); score=10; analysis='先保留信息，再按规程处理。' }
        @{ stem='实训中可以短接急停回路以便连续加工。'; type='judge'; options=@('正确','错误'); answers=@(1); score=10; analysis='' }
        @{ stem='联调前应确认的事项包括？'; type='multiple'; options=@('急停有效','限位反馈正常','设备连通正常','拆除全部防护罩'); answers=@(0,1,2); score=15; analysis='' }
        @{ stem='伺服驱动报警后较合理的做法是？'; type='single'; options=@('先记录报警号再按手册排查','反复上电直至消失','加大倍率继续运行','拆掉编码器线再试'); answers=@(0); score=10; analysis='' }
        @{ stem='电气柜检修前应切断动力电源并验电。'; type='judge'; options=@('正确','错误'); answers=@(0); score=10; analysis='' }
    )
    $cncPaperId = Add-PaperIfMissing '数控电气安全测验卷' '考查数控机床电气安全、联调准备和常见报警处理。' '演示用安全测验' $cncQuestionIds
    $cncExamId = Add-ExamIfMissing @{
        Name = '2024 学年数控电气安全测验'
        PaperId = $cncPaperId
        Description = '数控 2024 级 1 班安全测验，时长 60 分钟。'
        StartAt = Get-IsoUtc ($now.AddHours(-2))
        EndAt = Get-IsoUtc ($now.AddDays(7))
        DurationMinutes = 60
        ClassIds = @($classIds['数控 2024 级 1 班'])
    }
    $cncAnalysisExamId = Add-ExamIfMissing @{
        Name = '2024 学年数控电气安全分析测验'
        PaperId = $cncPaperId
        Description = '数控 2024 级 1 班安全测验（分析演示），时长 60 分钟。作答完成后结束，用于成绩与试题分析页面。'
        StartAt = Get-IsoUtc ($now.AddHours(-2))
        EndAt = Get-IsoUtc ($now.AddDays(7))
        DurationMinutes = 60
        ClassIds = @($classIds['数控 2024 级 1 班'])
    }

    $campRobotId = Add-CampIfMissing @{
        Name = '工业机器人运维员岗位训练营'
        Body = @{
            name = '工业机器人运维员岗位训练营'
            description = '面向工业机器人现场运维岗位，考查安全防护、坐标系确认和基本点检。可在模拟计算节点上完成相关准备。'
            durationMinutes = 50
            startupNodeId = 'simulation-node'
            skills = @(
                @{ name='安全防护'; subName='光栅与急停'; ability='能在进入工作单元前确认安全装置有效。' }
                @{ name='示教准备'; subName='坐标系确认'; ability='能确认当前工具坐标系并以安全速度接近。' }
                @{ name='日常点检'; subName='本体与线缆'; ability='能完成班前点检并汇报异常。' }
            )
            questions = @(
                @{ type='single'; stem='进入机器人工作单元前首先应确认？'; options=@('光栅、围栏和急停有效','关闭所有安全装置','以自动模式直接进入','拆除围栏以便观察'); answers=@(0); score=10; analysis=''; skillIndex=0; rubric=@(); practicalRubric=@() }
                @{ type='judge'; stem='发现安全光栅故障后，可以拆除后继续生产。'; options=@('正确','错误'); answers=@(1); score=10; analysis=''; skillIndex=0; rubric=@(); practicalRubric=@() }
                @{ type='multiple'; stem='示教接近工件时应做到？'; options=@('确认当前坐标系','使用安全速度','避开夹持区域','以最快速度贴近'); answers=@(0,1,2); score=15; analysis=''; skillIndex=1; rubric=@(); practicalRubric=@() }
                @{ type='single'; stem='班前点检发现电缆拖链破损时，应？'; options=@('停机汇报并待修复','用胶带简单缠绕后继续','拆除拖链继续生产','忽略并加快节拍'); answers=@(0); score=10; analysis=''; skillIndex=2; rubric=@(); practicalRubric=@() }
                @{ type='practical'; stem='请在模拟环境中完成接口地址配置并保存。'; options=@(); answers=@(); score=20; analysis=''; skillIndex=1; practicalRubric=@(); rubric=@(
                    @{ item='接口地址配置正确'; keyword='ip address'; score=10 }
                    @{ item='配置已保存'; keyword='write'; score=10 }
                )}
            )
        }
    }
    Publish-Camp $campId '数控机床调试员岗位训练营'
    Publish-Camp $campRobotId '工业机器人运维员岗位训练营'

    $deviceType = Find-NamedItem (Get-ApiItems '/device-types' @{ page = 1; pageSize = 1000 }) '模拟网络设备'
    $cabinet = Find-NamedItem (Get-ApiItems '/cabinets' @{ page = 1; pageSize = 1000 }) '模拟实训机柜'
    if ($deviceType -and $cabinet) {
        Add-DeviceIfMissing @{
            Name = '模拟防火墙 FW-01'
            CabinetId = [string]$cabinet.id
            DeviceTypeId = [string]$deviceType.id
            SerialPortNumber = 3
            NodeId = 'simulation-node'
            Remark = '演示用模拟防火墙，用于扩展实训拓扑。'
        }
        Add-DeviceIfMissing @{
            Name = '模拟工控主机 PC-01'
            CabinetId = [string]$cabinet.id
            DeviceTypeId = [string]$deviceType.id
            SerialPortNumber = 4
            NodeId = 'simulation-node'
            Remark = '演示用模拟工控主机。'
        }
    }
    else {
        $script:ApiNotes.Add('未找到模拟网络设备类型或模拟实训机柜，跳过新增模拟设备。')
    }

    $null = Add-VmIfMissing @{
        Name = '模拟液压实训环境'
        NodeId = 'simulation-node'
        CpuCount = 2
        MemoryMb = 2048
        DiskGb = 20
        Remark = '用于液压与气动课程的模拟实训环境。'
    }
    $null = Add-VmIfMissing @{
        Name = '模拟机器人示教环境'
        NodeId = 'simulation-node'
        CpuCount = 2
        MemoryMb = 2048
        DiskGb = 20
        Remark = '用于工业机器人示教与点检的模拟环境。'
    }
    $linuxVm = Find-NamedItem (Get-ApiItems '/virtual-machines' @{ page = 1; pageSize = 1000 }) '模拟 Linux 实训环境'
    if ($linuxVm) {
        $vmStatus = ''
        if ($linuxVm.PSObject.Properties.Name -contains 'status') { $vmStatus = [string]$linuxVm.status }
        if ($vmStatus -eq 'running') {
            $script:Skipped.Add('模拟虚拟机 模拟 Linux 实训环境（已运行）')
        }
        else {
            try {
                [void](Invoke-Api -Method POST -Path "/virtual-machines/$($linuxVm.id)/start")
                $script:Created.Add('启动模拟虚拟机 模拟 Linux 实训环境')
            }
            catch {
                $script:ApiNotes.Add("模拟 Linux 实训环境未能启动：$($_.Exception.Message)")
                Write-Step ("启动模拟虚拟机已跳过：{0}" -f $_.Exception.Message)
            }
        }
    }

    $assignIds = @()
    foreach ($name in @('wangjj','limin','chenhao')) { $assignIds += $teacherIds[$name] }
    foreach ($number in @(
        '2024010101','2024010102','2024010103','2024010104',
        '2024010201','2024010202','2024010203','2024010204',
        '2024020101','2024020102','2024020103','2024020104',
        '2024030101','2024030102','2024030103'
    )) {
        if ($studentIds.ContainsKey($number)) { $assignIds += $studentIds[$number] }
    }
    $already = @{}
    foreach ($row in (Get-ApiItems '/nodes/simulation-node/accounts' @{ page = 1; pageSize = 1000 })) {
        $already[[string]$row.userId] = $true
    }
    $pending = @($assignIds | Select-Object -Unique | Where-Object { $_ -and -not $already.ContainsKey([string]$_) })
    if ($pending.Count -gt 0) {
        [void](Invoke-Api -Method POST -Path '/nodes/simulation-node/accounts' -Body @{ userIds = @($pending) })
        $script:Created.Add(("节点账号关联 {0} 人" -f $pending.Count))
    }
    else {
        $script:Skipped.Add('节点账号关联（演示师生均已分配）')
    }

    $linkRubric = Get-LinkRubric
    $cncCaseResource = Find-ResourceByName '模拟节点联调实操'
    if ($cncCaseResource) { Ensure-ResourceRubric $cncCaseResource.id $linkRubric }
    else { $script:ApiNotes.Add('未找到课程资源 模拟节点联调实操，无法补评分表。') }

    $hydraulicCourse = Find-NamedItem (Get-ApiItems '/courses' @{ page = 1; pageSize = 1000 }) '液压与气动技术'
    $hydraulicCourseId = $null
    if ($hydraulicCourse) { $hydraulicCourseId = [string]$hydraulicCourse.id }
    if ($hydraulicCourseId -and $linkRubric.Count -gt 0) {
        Ensure-CourseContent $hydraulicCourseId @(
            @{
                Name = '回路联调实训'
                Sections = @(
                    @{
                        Name = '液压回路模拟联调'
                        DurationMinutes = 40
                        Resources = @(
                            @{ name='液压回路联调说明'; kind='theory'; durationMinutes=15; content='联调前应先确认泵站卸荷、压力表读数和溢流阀调定值，再在模拟节点上检查交换机与路由器的主机名及互联接口说明，作为现场网络准备的演示步骤。' }
                            @{
                                name='液压回路联调实操'
                                kind='casePractice'
                                durationMinutes=25
                                content='在模拟计算节点上完成交换机与路由器的基础连通检查，作为液压实训联调前的网络准备。'
                                topology='模拟交换机 SW-01 与模拟路由器 RT-01 互联，用于液压回路联调准备。'
                                startupNodeId='simulation-node'
                                caseRubric=@($linkRubric)
                            }
                        )
                    }
                )
            }
        )
        $hydraulicCase = Find-ResourceByName '液压回路联调实操'
        if ($hydraulicCase) { Ensure-ResourceRubric $hydraulicCase.id $linkRubric }
    }

    $txtFiles = @(
        @{ Resource='国家标准与图纸幅面'; Name='机械制图国家标准摘要.txt'; Text="机械图样应执行现行国家标准。`r`n常用图幅为 A0 至 A4，比例需优先采用标准系列，并在标题栏中正确填写。`r`n本文件仅用于演示课程资源附件，不是正式标准文本。" }
        @{ Resource='尺寸标注案例'; Name='阶梯轴标注示例说明.txt'; Text="以阶梯轴为例，应在能反映形状特征的视图上集中标注直径和长度，避免重复标注。`r`n圆角、倒角和键槽尺寸需完整给出，并注意基准选择。`r`n本说明仅用于课堂演示。" }
        @{ Resource='电气安全要点'; Name='数控电气安全检查清单.txt'; Text="1. 确认急停有效、防护罩到位。`r`n2. 禁止带电插拔伺服接头。`r`n3. 出现异常报警时先记录报警号，再按规程切断动力电。`r`n4. 不得擅自短接保护装置。`r`n本清单仅用于演示附件。" }
        @{ Resource='液压系统的基本组成'; Name='液压系统组成说明.txt'; Text="液压系统一般由动力元件、执行元件、控制元件、辅助元件和工作介质组成。`r`n识读原理图时，应先找到油泵和执行元件，再沿压力油路、回油路和泄漏油路追踪控制阀的作用。`r`n本说明仅用于演示附件。" }
        @{ Resource='机器人安全防护要点'; Name='机器人单元安全须知.txt'; Text="进入机器人工作单元前，应确认安全光栅、围栏门锁和急停有效。`r`n示教或点检时须切换到适当的安全速度，避免身体进入可能的夹持区域。`r`n本须知仅用于演示附件。" }
    )
    foreach ($item in $txtFiles) {
        $path = Get-DemoTextPath $item.Name $item.Text
        Ensure-ResourceFile $item.Resource $item.Name $path 'text/plain'
    }
    $pngPath = Get-DemoPngPath 'electrical-lab-sketch.png'
    Ensure-ResourceFile '电气联调步骤说明' '电气联调示意图.png' $pngPath 'image/png'

    $roomHydraulicId = $null
    if ($hydraulicCourseId) {
        $roomHydraulicId = Add-Classroom @{
            Name = '机电 2024 级液压与气动结课示范课堂'
            CourseId = $hydraulicCourseId
            TeacherUserId = $teacherIds['wangjj']
            OpenAt = $openAt
            CloseAt = $closeAt
            ClassIds = @($classIds['机电 2024 级 2 班'])
        }
    }

    $practicalBankId = Add-BankIfMissing '数控联调实操题库' 'mixed'
    $practicalObjectiveIds = Add-BankQuestions $practicalBankId @(
        @{ stem='联调前应先确认急停和限位反馈是否有效。'; type='judge'; options=@('正确','错误'); answers=@(0); score=10; analysis='' }
        @{ stem='修改模拟设备主机名时，较合理的做法是？'; type='single'; options=@('按评分要求设置可识别的实训标识','随意使用中文空格作为主机名','关闭设备后再改一次即可','把接口说明留空以免冲突'); answers=@(0); score=10; analysis='' }
    )
    $practicalQuestionId = $null
    if ($practicalBankId -and $linkRubric.Count -gt 0) {
        $existingPractical = Get-ApiItems '/exam-questions' @{ page = 1; pageSize = 1000; bankId = $practicalBankId }
        $foundPractical = Find-NamedItem $existingPractical '请在模拟环境中完成交换机与路由器的互联配置。' 'stem'
        if ($foundPractical) {
            $practicalQuestionId = [string]$foundPractical.id
            $script:Skipped.Add('试题 请在模拟环境中完成交换机与路由器的互联配置。')
        }
        else {
            $createdPractical = Invoke-Api -Method POST -Path '/exam-questions' -Body @{
                type = 'practical'
                stem = '请在模拟环境中完成交换机与路由器的互联配置。'
                options = @()
                answers = @()
                score = 40
                analysis = '主机名应改为实训标识，并填写互联接口说明。'
                bankId = $practicalBankId
                practicalNodeId = 'simulation-node'
                practicalResources = '请按评分表设置 SW-01-LAB、RT-01-LAB，以及 ethernet0/0 的接口说明。'
                practicalRubric = @($linkRubric)
            }
            $practicalQuestionId = [string]$createdPractical.id
            $script:Created.Add('试题 请在模拟环境中完成交换机与路由器的互联配置。')
        }
    }
    $practicalPaperQuestionIds = @()
    foreach ($id in @($practicalObjectiveIds)) { if ($id) { $practicalPaperQuestionIds += $id } }
    if ($practicalQuestionId) { $practicalPaperQuestionIds += $practicalQuestionId }
    $practicalPaperId = Add-PaperIfMissing '数控联调实操测验卷' '考查联调安全要点，以及在模拟节点上完成交换机与路由器的互联配置。' '演示用实操测验' $practicalPaperQuestionIds
    $practicalExamId = Add-ExamIfMissing @{
        Name = '2024 学年数控联调实操评分测验'
        PaperId = $practicalPaperId
        Description = '数控 2024 级 1 班实操测验，时长 60 分钟。部署模拟环境后作答，提交后按评分表自动评分。'
        StartAt = Get-IsoUtc ($now.AddHours(-2))
        EndAt = Get-IsoUtc ($now.AddDays(7))
        DurationMinutes = 60
        ClassIds = @($classIds['数控 2024 级 1 班'])
    }
    if ($practicalExamId) {
        try {
            [void](Invoke-Api -Method POST -Path "/exams/$practicalExamId/deployment" -Body @{})
            $script:Created.Add('部署考试实操环境 2024 学年数控联调实操评分测验')
        }
        catch {
            if ("$_" -match '已部署|没有配置') { $script:Skipped.Add('部署考试实操环境 2024 学年数控联调实操评分测验') }
            else {
                $script:ApiNotes.Add("考试实操环境部署失败：$($_.Exception.Message)")
                Write-Step ("考试实操环境部署已跳过：{0}" -f $_.Exception.Message)
            }
        }
    }

    $openExam = Find-NamedItem (Get-ApiItems '/exams' @{ page = 1; pageSize = 1000 }) '2024 学年机械制图阶段测验'
    $openExamId = $null
    $openPaperId = $paperId
    if ($openExam) {
        $openExamId = [string]$openExam.id
        if ($openExam.PSObject.Properties.Name -contains 'paperId' -and $openExam.paperId) { $openPaperId = [string]$openExam.paperId }
    }
    $drawingPaperQuestions = Get-PaperQuestions $openPaperId
    $cncPaperQuestions = Get-PaperQuestions $cncPaperId
    $campCncQuestions = Get-CampQuestions $campId
    $campRobotQuestions = Get-CampQuestions $campRobotId
    $script:PracticeAnswers = Get-ResourceAnswerMap @($room1Id, $room2Id, $roomCncId, $roomRobotId, $roomHydraulicId)

    $examStatus = @{}
    $practicalPaperQuestions = Get-PaperQuestions $practicalPaperId
    foreach ($pair in @(@{ Id = $openExamId; Key = 'drawing' }, @{ Id = $cncExamId; Key = 'cnc' }, @{ Id = $cncAnalysisExamId; Key = 'cncAnalysis' }, @{ Id = $practicalExamId; Key = 'cncPractical' })) {
        if (-not $pair.Id) { continue }
        $rows = Get-ApiItems "/exams/$($pair.Id)/students" @{}
        foreach ($row in $rows) {
            $examStatus[("$($pair.Key)|{0}" -f [string]$row.studentUserId)] = [string]$row.attemptStatus
        }
    }

    $activityPlans = @(
        @{ UserName='zhaozh'; Number='2024010101'; LearnMode='high'; ClassroomIds=@($room1Id); Exams=@(@{ Name='2024 学年机械制图阶段测验'; Id=$openExamId; Questions=$drawingPaperQuestions; Mode='high' }); Camps=@() }
        @{ UserName='qiansy'; Number='2024010102'; LearnMode='medium'; ClassroomIds=@($room1Id); Exams=@(@{ Name='2024 学年机械制图阶段测验'; Id=$openExamId; Questions=$drawingPaperQuestions; Mode='medium' }); Camps=@() }
        @{ UserName='sunjy'; Number='2024010103'; LearnMode='low'; ClassroomIds=@($room1Id); Exams=@(@{ Name='2024 学年机械制图阶段测验'; Id=$openExamId; Questions=$drawingPaperQuestions; Mode='low' }); Camps=@() }
        @{ UserName='zhoumx'; Number='2024010104'; LearnMode='high'; ClassroomIds=@($room1Id); Exams=@(@{ Name='2024 学年机械制图阶段测验'; Id=$openExamId; Questions=$drawingPaperQuestions; Mode='progress' }); Camps=@() }
        @{ UserName='wuyt'; Number='2024010201'; LearnMode='high'; ClassroomIds=@($room2Id; if ($roomHydraulicId -and (Get-ClassroomStatus $roomHydraulicId) -ne 'closed') { $roomHydraulicId }); Exams=@(); Camps=@() }
        @{ UserName='zhenghr'; Number='2024010202'; LearnMode='medium'; ClassroomIds=@($room2Id; if ($roomHydraulicId -and (Get-ClassroomStatus $roomHydraulicId) -ne 'closed') { $roomHydraulicId }); Exams=@(); Camps=@() }
        @{ UserName='caozm'; Number='2024010203'; LearnMode='high'; ClassroomIds=@($room2Id; if ($roomHydraulicId -and (Get-ClassroomStatus $roomHydraulicId) -ne 'closed') { $roomHydraulicId }); Exams=@(); Camps=@() }
        @{ UserName='dengxy'; Number='2024010204'; LearnMode='low'; ClassroomIds=@($room2Id; if ($roomHydraulicId -and (Get-ClassroomStatus $roomHydraulicId) -ne 'closed') { $roomHydraulicId }); Exams=@(); Camps=@() }
        @{ UserName='fengxx'; Number='2024020101'; LearnMode='high'; ClassroomIds=@($roomCncId); Exams=@(@{ Name='2024 学年数控电气安全测验'; Id=$cncExamId; Questions=$cncPaperQuestions; Mode='high' }, @{ Name='2024 学年数控电气安全分析测验'; Id=$cncAnalysisExamId; Questions=$cncPaperQuestions; Mode='high' }, @{ Name='2024 学年数控联调实操评分测验'; Id=$practicalExamId; Questions=$practicalPaperQuestions; Mode='high' }); Camps=@(@{ Name='数控机床调试员岗位训练营'; Id=$campId; Questions=$campCncQuestions; Mode='high' }) }
        @{ UserName='jiangzh'; Number='2024020102'; LearnMode='medium'; ClassroomIds=@($roomCncId); Exams=@(@{ Name='2024 学年数控电气安全测验'; Id=$cncExamId; Questions=$cncPaperQuestions; Mode='medium' }, @{ Name='2024 学年数控电气安全分析测验'; Id=$cncAnalysisExamId; Questions=$cncPaperQuestions; Mode='medium' }, @{ Name='2024 学年数控联调实操评分测验'; Id=$practicalExamId; Questions=$practicalPaperQuestions; Mode='medium' }); Camps=@(@{ Name='数控机床调试员岗位训练营'; Id=$campId; Questions=$campCncQuestions; Mode='medium' }) }
        @{ UserName='liuc'; Number='2024020103'; LearnMode='low'; ClassroomIds=@($roomCncId); Exams=@(@{ Name='2024 学年数控电气安全测验'; Id=$cncExamId; Questions=$cncPaperQuestions; Mode='low' }, @{ Name='2024 学年数控电气安全分析测验'; Id=$cncAnalysisExamId; Questions=$cncPaperQuestions; Mode='low' }, @{ Name='2024 学年数控联调实操评分测验'; Id=$practicalExamId; Questions=$practicalPaperQuestions; Mode='low' }); Camps=@(@{ Name='数控机床调试员岗位训练营'; Id=$campId; Questions=$campCncQuestions; Mode='low' }) }
        @{ UserName='huangsq'; Number='2024020104'; LearnMode='high'; ClassroomIds=@($roomCncId); Exams=@(@{ Name='2024 学年数控电气安全测验'; Id=$cncExamId; Questions=$cncPaperQuestions; Mode='high' }, @{ Name='2024 学年数控电气安全分析测验'; Id=$cncAnalysisExamId; Questions=$cncPaperQuestions; Mode='high' }, @{ Name='2024 学年数控联调实操评分测验'; Id=$practicalExamId; Questions=$practicalPaperQuestions; Mode='high' }); Camps=@(@{ Name='数控机床调试员岗位训练营'; Id=$campId; Questions=$campCncQuestions; Mode='high' }) }
        @{ UserName='hejj'; Number='2024030101'; LearnMode='high'; ClassroomIds=@($roomRobotId); Exams=@(); Camps=@(@{ Name='工业机器人运维员岗位训练营'; Id=$campRobotId; Questions=$campRobotQuestions; Mode='high' }) }
        @{ UserName='linsh'; Number='2024030102'; LearnMode='medium'; ClassroomIds=@($roomRobotId); Exams=@(); Camps=@(@{ Name='工业机器人运维员岗位训练营'; Id=$campRobotId; Questions=$campRobotQuestions; Mode='medium' }) }
        @{ UserName='gaoyh'; Number='2024030103'; LearnMode='low'; ClassroomIds=@($roomRobotId); Exams=@(); Camps=@(@{ Name='工业机器人运维员岗位训练营'; Id=$campRobotId; Questions=$campRobotQuestions; Mode='progress' }) }
    )

    try {
        foreach ($plan in $activityPlans) {
            $userId = $null
            if ($studentIds.ContainsKey($plan.Number)) { $userId = [string]$studentIds[$plan.Number] }
            $plan.PersonId = ''
            if ($personIds.ContainsKey($plan.UserName)) { $plan.PersonId = [string]$personIds[$plan.UserName] }
            foreach ($exam in @($plan.Exams)) {
                $key = ''
                if ($exam.Name -match '机械制图') { $key = 'drawing|' + $userId }
                elseif ($exam.Name -match '分析测验') { $key = 'cncAnalysis|' + $userId }
                elseif ($exam.Name -match '联调实操') { $key = 'cncPractical|' + $userId }
                elseif ($exam.Name -match '数控电气') { $key = 'cnc|' + $userId }
                $exam.ExistingStatus = ''
                if ($key -and $examStatus.ContainsKey($key)) { $exam.ExistingStatus = [string]$examStatus[$key] }
            }
            Invoke-StudentActivity $plan
        }
    }
    finally {
        try { Restore-DemoPasswords }
        catch {
            $script:ApiNotes.Add("部分演示账号密码恢复失败，请检查后手工重置为初始演示口令。$($_.Exception.Message)")
            Write-Step ("演示密码恢复失败：{0}" -f $_.Exception.Message)
        }
    }

    if ($cncAnalysisExamId) {
        try {
            Connect-Admin $script:AdminPassword
            $examRow = Find-NamedItem (Get-ApiItems '/exams' @{ page = 1; pageSize = 1000 }) '2024 学年数控电气安全分析测验'
            $examStatusNow = ''
            if ($examRow -and $examRow.PSObject.Properties.Name -contains 'status') { $examStatusNow = [string]$examRow.status }
            $submitted = 0
            foreach ($row in (Get-ApiItems "/exams/$cncAnalysisExamId/students" @{})) {
                if ([string]$row.attemptStatus -in @('submitted','graded')) { $submitted++ }
            }
            if ($examStatusNow -eq 'closed') {
                $script:Skipped.Add('结束考试 2024 学年数控电气安全分析测验')
            }
            elseif ($submitted -eq 0) {
                $script:ApiNotes.Add('数控电气安全分析测验尚无已提交作答，未结束考试。')
                Write-Step '数控电气安全分析测验尚无已提交作答，未结束考试。'
            }
            else {
                [void](Invoke-Api -Method POST -Path "/exams/$cncAnalysisExamId/close")
                $script:Created.Add('结束考试 2024 学年数控电气安全分析测验')
            }
        }
        catch {
            $script:ApiNotes.Add("数控电气安全分析测验未能结束：$($_.Exception.Message)")
            Write-Step ("结束考试已跳过：{0}" -f $_.Exception.Message)
            try { Connect-Admin $script:AdminPassword } catch { }
        }
    }

    try { Connect-Admin $script:AdminPassword } catch { }
    Upload-CaseEvidence $roomCncId
    Upload-CaseEvidence $roomHydraulicId
    if ($roomHydraulicId) {
        try { Close-ClassroomIfOpen $roomHydraulicId '机电 2024 级液压与气动结课示范课堂' }
        catch {
            $script:ApiNotes.Add("结课示范课堂未能结束：$($_.Exception.Message)")
            Write-Step ("结课已跳过：{0}" -f $_.Exception.Message)
        }
    }
    if ($practicalExamId) {
        try { Close-ExamIfSubmitted $practicalExamId '2024 学年数控联调实操评分测验' }
        catch {
            $script:ApiNotes.Add("数控联调实操测验未能结束：$($_.Exception.Message)")
            Write-Step ("结束实操考试已跳过：{0}" -f $_.Exception.Message)
        }
    }

    Write-Host ''
    Write-Step ("新增 {0} 项，跳过已存在 {1} 项。" -f $script:Created.Count, $script:Skipped.Count)
    if ($script:Created.Count -gt 0) { Write-Step ('新增：' + ($script:Created -join '；')) }
    if ($script:ApiNotes.Count -gt 0) {
        $uniqueNotes = @($script:ApiNotes | Select-Object -Unique)
        Write-Step ('接口限制或注意事项：' + ($uniqueNotes -join '；'))
    }
    Write-Step '演示师生初始密码为 Demo12345678，首次登录后需要修改密码。脚本未改动管理员账号。'
    Write-Step '详细说明见 docs/demo-data.md。'
}
catch {
    Write-Error $_
    exit 1
}