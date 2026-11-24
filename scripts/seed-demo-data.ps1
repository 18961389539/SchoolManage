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


try {
    $plain = Get-PlainPassword
    if ([string]::IsNullOrWhiteSpace($plain)) { throw '请提供管理员密码。' }
    Connect-Admin $plain
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

    $assignIds = @()
    foreach ($name in @('wangjj','limin','chenhao')) { $assignIds += $teacherIds[$name] }
    foreach ($number in @('2024010101','2024010102','2024010103','2024010104','2024010201','2024010202','2024020101','2024020102')) {
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

    Write-Host ''
    Write-Step ("新增 {0} 项，跳过已存在 {1} 项。" -f $script:Created.Count, $script:Skipped.Count)
    if ($script:Created.Count -gt 0) { Write-Step ('新增：' + ($script:Created -join '；')) }
    Write-Step '演示师生初始密码为 Demo12345678，首次登录后需要修改密码。脚本未改动管理员账号。'
    Write-Step '详细说明见 docs/demo-data.md。'
}
catch {
    Write-Error $_
    exit 1
}