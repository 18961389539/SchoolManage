# 学校实训资源平台

面向 Windows 11 的校内实训资源管理软件。平台覆盖课堂、理实一体化考试、开放课程、技能图谱和岗位训练营。机柜与教学实操可使用内置模拟环境。管理员可配置 Hyper-V 主机进行真实集群查看和实训虚拟机管理。

技术路线：Vue 3 / TypeScript 管理界面、ASP.NET Core / .NET 10、SQLite、Windows Service。前端构建结果由同一个服务托管，发布包自带 .NET 运行时；使用端通过浏览器访问。目标机不需要 Node.js、Java、容器环境或独立 SQLite 服务。

## Cloud Agent Apps 演示

客户演示用 Linux 上的网页进程，不安装 Windows 服务，也不连接 Hyper-V。在 Cloud Agent 中部署时执行：

```bash
bash scripts/start-demo.sh
```

脚本会构建管理界面，设置 `SCHOOL_DEMO=1`，并监听 `0.0.0.0` 上的 `PORT`（没有该变量时用 5080）。数据库目录默认是 `.local/demo-data`。库为空时自动写入演示学校、管理员、师生、模拟机柜、一门已发布课程、一个开放课堂、一场进行中的考试和一项已发布的岗位训练营。登录页列出账号。演示密码是 `Demo12345678`，登录后不需要再修改。

重新发布后如果数据目录是空的，会再次写入同一套演示数据。已有用户的库不会被覆盖。学校正式服务器不要设置 `SCHOOL_DEMO`。

## 开发运行

开发机需要 .NET 10 SDK 与 Node.js 22.5+/npm（发布门禁使用内置 `node:sqlite`），目标部署机只接收构建后的程序。所有命令在仓库根目录执行。

```powershell
npm.cmd --prefix web install
npm.cmd --prefix web run format:check
npm.cmd --prefix web run lint
npm.cmd --prefix web run test
npm.cmd --prefix web run build
dotnet test tests/School.Training.Tests/School.Training.Tests.csproj
New-Item -ItemType Directory -Path src/School.Training/wwwroot -Force | Out-Null
Copy-Item -Path web/dist/* -Destination src/School.Training/wwwroot -Recurse -Force
$env:SCHOOL_DATA_DIR = Join-Path (Get-Location).Path '.local/data'
dotnet run --project src/School.Training -- --urls http://127.0.0.1:5080
```

打开 `http://127.0.0.1:5080`，在服务器本机完成首次管理员初始化。系统不预置通用管理员口令。初始化完成后通过界面的“创建模拟环境”入口生成模拟资源。若还需要一套可演示的专业、班级、师生、课程、课堂、考试和岗位训练营数据，在服务已运行时执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/seed-demo-data.ps1
```

脚本默认只追加尚不存在的演示记录，不会覆盖已有数据，也不会改动管理员账号。导入后会包含课堂学习记录、考试作答和岗位训练营作答。账号与导入说明见 [演示数据](docs/demo-data.md)。

数据库结构由 EF Core Migrations 管理。新增结构变更时执行 `dotnet tool restore`，创建迁移并递增 `DatabaseSafety.SchemaVersion`：

```powershell
dotnet ef migrations add <Name> --project src/School.Training --startup-project tools/School.Training.DesignHost --output-dir Migrations
```

存量 v1–v17 数据库在启动时仍走旧版升级阶梯，完成后自动加入 `__EFMigrationsHistory` 基线。

## 构建 Windows 交付包

```powershell
# 本地验证构建：显式允许未签名
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Version 0.1.0 -AllowUnsigned
```

正式发布需在命令中加入下方签名参数，未提供证书时脚本会直接失败。

完整发布（含安装器）默认要求代码签名：安装 Windows SDK 后，用 `-SigningThumbprint <证书指纹>` 从证书存储取证书，或用 `-SigningPfx <文件>` 并设置环境变量 `SCHOOL_SIGNING_PFX_PASSWORD` 取私钥口令；脚本会对发布程序和安装器执行 Authenticode 签名并校验签名。未提供证书时构建直接失败；本地门禁或 CI 需要显式加 `-AllowUnsigned`，或用 `-SkipInstaller` 只跑门禁。`-RequireSigning` 可再显式强调签名要求。

构建会先发布 Release 程序，再对发布目录执行格式检查、xUnit、迁移、应用、Excel、节点账号、基础设施、负载、安全及部署回归。部署回归包括安装失败回退与备份恢复。逐项门禁结果以 `docs/verification/release-test-gate.json` 为准，`-SkipFrontend` 时跳过 5 项前端检查。任一检查失败即停止打包。发布包只携带本次门禁报告，并生成包含提交、构建时间和证据哈希的 `release-manifest.json`。

如果当前开发服务器占用 `web/node_modules`，将 `web` 源码及锁文件复制到独立目录，并通过 `-FrontendDirectory` 指定该目录。前端构建、测试和依赖清单将使用这份副本。

`artifacts/` 包含以下输出。

- `SchoolTraining-0.1.0-Setup.exe`：自包含安装程序，申请管理员权限后注册本系统服务。
- `SchoolTraining-0.1.0-win-x64.zip`：离线发布包、安装维护脚本和文档。
- `SchoolTraining-0.1.0-SHA256SUMS.txt`：发布包校验值。
- `SchoolTraining-0.1.0-manifest.json`：本次构建的提交、构建时间、门禁状态、证据哈希和产物校验值。

安装程序可以使用 `--quiet --validate-only` 验证嵌入载荷；该模式只在临时目录解压检查，不注册服务、不修改防火墙、不安装程序。

## 部署和验收

默认程序目录为 `C:\Program Files\SchoolTraining`，持久数据目录为 `C:\ProgramData\SchoolTraining`。服务名称为 `SchoolTraining`，默认地址 `http://127.0.0.1:5080`。修改监听、备份恢复、更新与卸载见 [Windows 部署与运维](docs/windows-operations.md)。

- [需求与验收矩阵](docs/requirements-acceptance.md)：原文、环境修订、待确认范围和逐项证据。
- [交付验证清单](docs/delivery-checklist.md)：源码构建、模拟流程、安装与恢复的验证方法。
- [API 契约](docs/api-contract.md)：前后端接口约定。
- 界面回归（可选，不属发布门禁）：`node tests/ui-smoke.mjs`，需要本机 Chromium/Edge 与 `playwright-core`；覆盖初始化登录、万行导入预览分页、仅看问题行和服务异常重试页，报告写入 `docs/verification/ui-smoke.json`。
- 套件式界面回归：`npm --prefix web run test:ui`，覆盖路由、请求、页面、列表、对话框和会话，并对控制台、学生、考试、审计页面输出 axe 无障碍报告（只报告，不影响退出码），报告写入 `.local/verification/ui-suite.json`。GitHub Actions 在每次推送和 PR 上运行同一套发布门禁及该界面回归。

当前版本仍在迭代。发布包生成、构建成功或模拟测试成功，均不能单独证明完整采购参数已验收；以验收矩阵中的实测证据为准。
