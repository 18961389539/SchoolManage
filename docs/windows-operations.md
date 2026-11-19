# Windows 部署与运维

## 运行边界

- 目标为受 .NET 10 支持的 Windows 11 x64；正式交付以目标机器版本实测为准。
- 发布程序自带运行时、SQLite 库和已构建的网页文件。开发用 SDK、Node.js/npm 不属于部署要求。
- 本版本采用用户确认的内置模拟机柜、设备、节点与虚拟机；无需启用 Hyper-V。
- 时间/网络配置仅作用于模拟节点；Win11 管理主机只读采集。
- 默认监听服务器本机 `127.0.0.1:5080`。局域网访问、HTTPS 证书及学校域名需配置后验收。

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

服务启动时会读取 SQLite `PRAGMA user_version` 并按顺序执行受控迁移。升级前先写入 `backups/pre-migration-v<旧版本>-*.db` 一致性快照；v1→v2 建立“同专业班级名称唯一”索引，v2→v3 为导入批次增加状态、进度和结果字段，v3→v4 为审计日志建立 `(At, Id)` 复合索引以支撑按时间倒序的稳定分页。如存在历史同名班级，启动会失败并列出冲突项，不会擅自改名或删除数据。迁移成功后仍保留该快照，默认保留 5 份。当前结构版本为 v4。批量导入提交后由受控后台任务处理，页面轮询批次状态；初始密码只存在于服务进程内存，服务重启会安全地将未完成任务标记为失败并要求重新提交。导入先做整批复校验、再为有效行创建账号：问题行不触发密码哈希。密码哈希（310000 次 PBKDF2 迭代）是单批导入的主要耗时，按 CPU 核数并行（上限 8）；本机 16 核实测 1 万行导入约 4 分钟，绝大部分耗在哈希。哈希阶段不占用数据库写事务，期间其他管理操作可继续。

审计日志与业务数据同库保存，默认长期保留、系统不会自动删除，并随备份一同归档；如学校有明确的留存期限要求，应在交付时约定归档或清理方式，而不是依赖系统自动截断。备份前会检查备份目录所在磁盘的剩余空间；如果读取不到剩余空间，接口返回 507 并提示人工检查，不会在空间未知时继续写入。

日志和备份均有容量上限，可在 `appsettings.Production.json` 调整：

```json
{
  "School": {
    "Database": { "BusyTimeoutSeconds": 15 },
    "Logs": { "RetentionDays": 14, "MaxFileBytes": 10485760, "MaxTotalBytes": 209715200 },
    "Backups": { "RetentionCount": 20, "MaxTotalBytes": 5368709120, "MinimumFreeBytes": 268435456 },
    "Migrations": { "PreMigrationBackupCount": 5 }
  }
}
```

日志只清理 `logs/service-*.log`；备份只清理 `backups/school-*.zip` 和 `pre-migration-*.db`。创建备份前会检查剩余空间，空间不足返回 507，不会静默生成不完整 ZIP。

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

恢复目标须已由本系统初始化，带有效产品数据标记；不会接管任意非空文件夹。恢复脚本检查归档路径、条目、大小、产品清单、数据标记和 SQLite 文件头，然后调用对应版本程序的 `--validate-backup <数据库> <密钥目录>`，只读验证数据库结构、完整性及已有受保护凭据的解密能力。`--validate-backup` 接受受支持的旧结构备份，恢复后由服务启动迁移升级；`--validate-database` 仍要求当前结构版本。通过后才停止服务，将原数据库及密钥保存在 `restore-rollback-*`，恢复备份并按原运行状态重新启动；运行中的服务还要通过实际 `/api/status` 检查。失败时尝试回滚，原备份和日志不删除。服务未安装或本来停止时，恢复结束保持停止。

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
