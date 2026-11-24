# Windows 部署与运维

## 运行边界

- 目标为受 .NET 10 支持的 Windows 11 x64；正式交付以目标机器版本实测为准。
- 发布程序自带运行时、SQLite 库和已构建的网页文件。开发用 SDK、Node.js/npm 不属于部署要求。
- 机柜和设备采用内置模拟环境。教学实操路径可使用内置模拟节点与虚拟机。管理员也可接入学校配置的 Hyper-V 主机，查看节点状态和管理实训虚拟机。
- 时间/网络配置仅作用于模拟节点；Win11 管理主机只读采集。
- 默认监听服务器本机 `127.0.0.1:5080`。局域网访问、HTTPS 证书及学校域名需配置后验收。

## 师生实训与资源授权

管理员在控制台初始化模拟环境，在「实训设备」为自建设备选择所属模拟节点，再在「实训节点」为师生分配账号。师生完成首次改密后，可在「我的实训设备」查看分配结果，并从设备列表打开模拟终端或 Web 管理。虚拟机启停由管理员执行。设备回收后可以重新连接，已保存的配置继续保留。

「许可证管理」展示资源已用量和许可额度。启用一种额度后，创建对应资源和节点账号分配时会检查剩余额度。资源授权码及续期规则可下载「使用说明」查看。初次部署如需要额度管理，请与供应商核对公钥、设备特征码和许可证后再导入。旧版许可证如需启用额度，可重新上传原签名文件。

设备、机柜和节点表单中的密码使用 DPAPI 加密保存，接口和列表不回传明文。当前模拟环境不读取这些密码；接入真实串口控制器、跳板链路或真实终端后，才用于对应协议的认证。模拟设备的连接成功不等同于真实设备认证通过。

## Hyper-V 主机管理

在服务的学校配置文件中设置 `School:HyperV:Hosts`，内容为经学校授权的主机名数组。然后在「实训节点」登记相同的主机名，选择 Hyper-V。启用或停用只改变平台中的分配状态。

- 对远端 Hyper-V 主机使用 WinRM HTTPS 和 Kerberos。学校需准备匹配主机名的受信证书，以及拥有对应 Hyper-V 和远程读取权限的 Windows 服务身份。安装器默认服务账户为 LocalService，远程管理需由学校配置有访问权限的身份。
- 实训虚拟机需由学校管理员在 Hyper-V 备注中加入独立的 `SchoolTraining` 行。平台的电源操作与移除仅作用于这些虚拟机。移除仅对已关机的虚拟机开放，保留虚拟磁盘文件。
- 「虚拟机管理 → Hyper-V」可查看虚拟机、搜索、启动、停止、重启与移除。来宾系统上报 IPv4 地址后可下载 RDP 连接文件，登录使用校方独立配置的账号。需由学校启用来宾系统远程桌面并配置网络访问。
- CPU 与内存来自 Hyper-V 主机，磁盘使用率对应默认虚拟磁盘目录所在卷。集群总数统计该主机全部虚拟机，虚拟机管理列表只显示实训虚拟机。无法采集的指标显示为空。
- 考试部署、课堂实时助教和岗位测评的教学流程仍使用模拟环境。学校需提供 Hyper-V 测试主机、可重置的来宾系统模板、虚拟交换机及账号与会话权限模型，才能用真实环境验证这些流程。

## 首次安装

双击版本对应的 `SchoolTraining-<版本>-Setup.exe`，允许本系统安装程序申请管理员权限。默认安装：

| 项目 | 默认值 |
|---|---|
| 程序 | `C:\Program Files\SchoolTraining` |
| 数据、密钥、备份 | `C:\ProgramData\SchoolTraining` |
| 服务名 | `SchoolTraining` |
| 服务账户 | `NT AUTHORITY\LocalService` |
| 本机访问 | `http://127.0.0.1:5080` |

安装完成后，在服务器本机浏览器打开地址并创建首个管理员。没有预置管理员密码。首次初始化只允许本机访问。

首次安装失败时，安装器撤销已确认由本次创建的服务、入站规则、卸载登记及哈希仍匹配的程序文件。已有数据、未识别或已变动的文件及本次启动日志会保留。若沿用旧数据目录，安装器将安装前的配置、数据库和密钥保存到 `upgrade-backups`，并在回退时恢复本次变更涉及的文件。部分写入且来源无法确认的文件、原目录 ACL 和手工维护的系统配置需要管理员核对；失败详情请以安装器输出为准。

离线 ZIP 包也可以由管理员 PowerShell 安装：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-service.ps1
```

仅解压运行、不注册服务可用于初测：

```powershell
$env:SCHOOL_DATA_DIR = 'D:\SchoolTrainingData'
.\app\School.Training.exe --urls http://127.0.0.1:5080
```

## 局域网访问

监听非回环地址必须显式选择 `AllowLan`，并提供有效的 HTTPS 证书配置。先由学校准备证书匹配的主机名、带私钥的 PFX 和配置文件，例如：

```json
{
  "AllowedHosts": "training.school.example;localhost;127.0.0.1;[::1]",
  "Kestrel": {
    "Endpoints": {
      "SchoolHttps": {
        "Url": "https://0.0.0.0:5443",
        "Certificate": {
          "Path": "D:\\SchoolDeployment\\school.pfx",
          "Password": "由学校填写的PFX密码"
        }
      }
    }
  }
}
```

配置文件属于凭据材料，应限制访问；密码不放入命令行、日志或发布包。`AllowedHosts` 必须明确填写学校实际使用、与证书 DNS 名称匹配的域名，不能为空或使用通配符；安装器只自动补充本机探测名称，不会自动放行外部域名。先用默认本机模式初始化管理员，再配置局域网 HTTPS。安装脚本检查证书有效期和私钥，将证书与配置复制到受限数据目录，由后台服务读取 `server.json`。以下示例绑定全部 IPv4 网卡，并仅对 Windows 专用/域网络配置文件增加该程序的入站规则：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-service.ps1 -ListenAddress 0.0.0.0 -Port 5443 -AllowLan -ServerConfigurationFile 'D:\SchoolDeployment\server.json'
```

对应安装程序参数：

```powershell
.\SchoolTraining-0.1.0-Setup.exe --listen-address 0.0.0.0 --port 5443 --allow-lan --server-config 'D:\SchoolDeployment\server.json'
```

HTTP 本机模式用于安装初始化与模拟验证。局域网模式不接受明文 HTTP；证书域名匹配与客户端信任需一并验收，不能将开发证书作为生产证书。初始化仍在服务器本机执行，浏览器应使用证书匹配且解析到本机的学校主机名。端口冲突、证书信任和代理配置不应通过关闭认证绕过。

## 数据与权限

程序目录只存放可替换的发布文件；所有者为 Administrators，并使用受保护 ACL：SYSTEM/Administrators 完全控制，LocalService/Users 只读执行。所有目录及发布清单文件均按此策略处理，未知文件不删除。数据目录存放数据库、Data Protection 密钥、配置、备份、日志和升级回退副本，所有者为 Administrators，SYSTEM/Administrators 完全控制，LocalService 可修改，不授予 Users 访问。

程序和数据目录的现存父目录链也必须由系统或管理员控制，不得允许普通用户删除、重命名或改写权限；junction/reparse 路径被拒绝。默认 Program Files/ProgramData 可用；自定义目录请选择已有且权限受控的父目录，普通用户拥有或可替换的父目录会在安装前被拒绝。正式安装载荷使用原子创建的管理员专用 GUID 临时目录，验证模式仍仅使用用户 TEMP。

不要删除、单独移动或重建 `keys` 目录；它用于已有受保护凭据和会话的解密。Windows 上密钥还使用 DPAPI LocalMachine 保护，恢复当前支持同一 Windows 安装。数据库备份包内含密钥，应按管理员凭据材料保存，不放到网站公开目录。

## 更新

1. 在系统内生成并下载一次备份，记录当前程序版本。
2. 使用同一安装路径运行新版本安装程序。安装过程停止已有服务、保留原程序回退副本、替换程序并启动服务。
3. 验证登录、业务数据、模拟终端、节点状态和备份功能。
4. 保留旧版本发布包和备份，直到本次升级验收结束。

现有 `appsettings.Production.json` 不自动覆盖。升级回退副本位于数据目录 `upgrade-backups`，不包含数据库迁移的逆向保证。涉及数据库结构升级时，应先备份并按版本发布说明执行回退；只替换旧 EXE 不一定能回退新数据库结构。

升级失败时使用升级前快照恢复程序、受管配置、数据库和密钥。旧服务原先运行时，回退后会验证服务状态和 `/api/status` 返回的旧版本；原先停止时，回退后保持停止。安装器同时报告升级错误和任何回退错误。手工修改的服务账户、恢复策略或防火墙设置仍需管理员核对。回退副本含凭据材料，保存在受控数据目录。

服务启动时使用 EF Core Migrations 管理结构变更。新数据库由 `InitialCreate` 迁移创建；v1–v17 的存量数据库仍由旧版升级阶梯完成结构升级，随后自动写入 `__EFMigrationsHistory` 基线，之后的变更由 EF 迁移接管。`PRAGMA user_version` 与 `application_id` 作为产品标记保留，用于备份校验和离线工具。升级前创建 `backups/pre-migration-v<旧版本>-*.db` 一致性快照，默认保留 5 份。若历史数据含同专业同名班级，启动会列出冲突，需合并或改名后重试。当前结构版本为 17。

新增数据库变更时，在仓库根目录执行 `dotnet tool restore`，再创建迁移并递增 `DatabaseSafety.SchemaVersion`：

```powershell
dotnet ef migrations add <Name> --project src/School.Training --startup-project tools/School.Training.DesignHost --output-dir Migrations
```

旧版升级阶梯的上限 `DatabaseMigrations.LegacyVersion` 保持 17，不再追加。迁移程序集随主程序发布，服务启动时自动应用；已有库存量库不会重建。

批量导入使用后台任务，页面显示批次状态。初始密码存在服务进程内存。服务重启后，管理员可以重新提交失败的导入任务。同一批次共用一次初始密码哈希：所有新账号使用管理员指定的初始密码并强制首次改密，改密后各自生成独立哈希。哈希阶段不占用数据库写事务，其他管理操作可以继续。

审计日志与业务数据同库保存，默认长期保留，并随备份一同归档。管理员可在“操作日志”页面按当前搜索条件导出 CSV（UTF-8，北京时间），导出行为本身也会记入审计。需要按留存期限自动清理时，可设置 `School:Audits:RetentionDays`（默认 `0` 表示永久保留，最大 3650 天）；启用后服务在启动 5 分钟后及每天 `School:Audits:CleanupTimeOfDay`（默认 03:30）分批删除更早的记录，并在审计表中记录清理结果。清理不可恢复，设置前应先确认学校的数据留存要求。

日志和备份均有容量上限，可在 `appsettings.Production.json` 调整：

```json
{
  "School": {
    "Database": { "BusyTimeoutSeconds": 15 },
    "Logs": { "MinimumLevel": "Information", "RetentionDays": 14, "MaxFileBytes": 10485760, "MaxTotalBytes": 209715200 },
    "Backups": { "RetentionCount": 20, "MaxTotalBytes": 5368709120, "MinimumFreeBytes": 268435456, "Automatic": { "Enabled": true, "TimeOfDay": "02:30" } },
    "Audits": { "RetentionDays": 0, "CleanupTimeOfDay": "03:30" },
    "Migrations": { "PreMigrationBackupCount": 5 }
  }
}
```

`School:Logs:MinimumLevel` 控制写入文件的最低级别（默认 `Information`，可选 `Warning`/`Error`/`None`）。文件日志由后台队列写入，主机繁忙时可提高级别减少队列压力。日志只清理 `logs/service-*.log`，备份只清理 `backups/school-*.zip` 和 `pre-migration-*.db`。创建备份前按服务账户检查备份目录所在卷的可用字节。检查失败、空间低于阈值或写入时磁盘空间耗尽均返回 507，备份列表只有写入完毕的 ZIP。

默认备份目录是数据目录下的 `backups`。可以把 `School:Backups:Directory` 设为另一个本地卷或已映射驱动器的绝对路径，让备份与数据库分开；UNC 路径也可用，但服务启动时共享必须可访问。服务账户必须对该目录有修改权限，路径无效或不可写会导致启动失败。备份包含数据库、DataProtection 密钥和可解密凭据，仍应按管理员凭据材料保管。

服务提供匿名 `GET /healthz`，只返回数据库连通性和数据/备份卷空间状态（`healthy` 或 `unhealthy`），不包含路径、账号或版本信息，供本机监控或安装器探针使用。登录会话默认 8 小时滑动，并在 24 小时后要求重新登录；绝对时限可用 `School:Session:AbsoluteHours` 调整（1–168 小时）。导入预览/提交、备份创建/下载、诊断包、统计导出和许可证导入按用户每分钟 60 次限流。

管理员可在“服务器信息”或“系统设置”页下载诊断包（ZIP）：包含服务状态、保留策略、主机与进程信息、数据库表计数与 PRAGMA 检查结果、最近三个日志文件的尾部以及清单。诊断包不包含密码、会话 Cookie、DataProtection 密钥或数据库文件，每次下载都会记入审计。

服务每天在 `School:Backups:Automatic:TimeOfDay`（服务器本地时间，默认 02:30）自动创建一份备份，可在配置中关闭。启动时若 24 小时内没有成功备份，会在 2 分钟宽限期后补做一次；失败后每小时重试。最近一次尝试、最近一次成功和失败原因显示在“备份与恢复”页面。自动任务与手工备份互斥：自动备份期间手工请求返回 409，自动任务会等待正在进行的自动或手工备份结束后再执行。

## 备份与恢复

系统备份 ZIP 应包含：

```text
manifest.json
.schooltraining-data.json
school.db
keys/...
```

数据库通过 SQLite 一致性备份 API 生成。不要在服务运行时仅复制 `school.db` 而忽略 WAL 状态。

在管理员 PowerShell 执行离线恢复：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\Program Files\SchoolTraining\scripts\restore-backup.ps1' -BackupFile 'D:\Backups\school-xxxx.zip'
```

恢复目标须已由本系统初始化并带有有效产品数据标记。恢复脚本用同一个归档句柄预检条目并解压。每项最多 1 GiB，合计最多 2 GiB，最多 5,000 项。写入时逐段限制实际解压字节并核对条目长度，随后调用对应版本程序的 `--validate-backup <数据库> <密钥目录>`，只读验证数据库完整性、支持的结构版本及已有凭据的解密能力。旧结构备份由服务启动时迁移，`--validate-database` 仍要求当前结构版本。

实际替换前会获取数据目录的独占恢复锁。同一目录已有恢复操作时，后一次执行会报错退出。脚本停止原先运行的服务，将原数据库、SQLite 辅助文件和密钥移入 `restore-rollback-*`，再放入备份内容并检查 `/api/status` 返回的版本。服务未安装或原本停止时，恢复后保持停止。

恢复失败时，脚本按已经执行的变更还原旧文件，保留失败实例的数据和日志。原服务曾在运行时，回退后会重新启动并验证旧数据库上的状态接口。若回退也失败，错误信息会同时显示恢复和回退原因，供管理员核对保留的文件、服务状态与日志。原备份文件和原日志保留。

**当前备份不能承诺跨机器或重装 Windows 后直接恢复设备凭据。** DPAPI 保护绑定当前 Windows 安装；跨机器迁移需要单独的密钥迁移方案，或重新录入受保护的设备凭据。证书与设备特征码授权也需重新验证，复制数据库不会自动转移机器绑定许可证。

## 服务诊断

```powershell
Get-Service -Name SchoolTraining
Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\SchoolTraining' -Name ImagePath,Environment
```

检查端口是否占用、数据目录 ACL、程序版本、Windows 事件记录和应用返回的错误。不要在日志中粘贴设备口令、会话 Cookie、管理员密码或授权签发私钥。

## 卸载

在 Windows 已安装应用列表卸载，或以管理员身份运行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\Program Files\SchoolTraining\scripts\uninstall-service.ps1' -RemoveProgramFiles
```

脚本仅处理与已注册产品路径一致、带有效安装清单的目录，并停止/移除本系统服务和防火墙规则。卸载按清单与 SHA-256 删除未修改的发布文件，保留未知文件和已修改的配置；不会整目录递归删除。持久数据、密钥、备份、日志均保留。仅解除服务注册、不删除程序文件时省略 `-RemoveProgramFiles`。重装时使用原数据目录可恢复访问，仍需按版本兼容性验证。

首次安装的程序目录必须为空，或带有效的本产品安装清单；数据目录必须为空或带本产品数据标记。路径中的 junction/reparse 重定向被拒绝。升级如遇未知文件冲突或已修改的发布二进制，会先拒绝覆盖，需备份并解决冲突后再安装。

## 尚需验证的交付证据

生成安装包与静态检查不代表安装实测。需要在验证机执行 UAC 安装、服务启动/重启、重启系统后自动启动、专用账户权限、局域网 HTTPS、升级回退、完整恢复和卸载保留数据测试。本轮文件安全回归没有执行这些系统配置操作，不能据此宣称安装流程已通过。
