# 学校实训资源平台

面向 Windows 11 的校内实训资源管理软件。当前实施范围来自两张技术参数截图，并采用用户确认的**内置模拟机柜、串口设备、集群节点和虚拟机**。模拟状态始终显式标注，不代表真实硬件联调通过。

技术路线：Vue 3 / TypeScript 管理界面、ASP.NET Core / .NET 10、SQLite、Windows Service。前端构建结果由同一个服务托管，发布包自带 .NET 运行时；使用端通过浏览器访问。目标机不需要 Node.js、Java、容器环境或独立 SQLite 服务。

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

打开 `http://127.0.0.1:5080`，在服务器本机完成首次管理员初始化。系统不预置通用管理员口令。初始化完成后通过界面的“创建模拟环境”入口生成模拟资源。

## 构建 Windows 交付包

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build-release.ps1 -Version 0.1.0
```

构建会先发布 Release 程序，再对发布目录执行 C# 格式检查、xUnit 单元测试、迁移、应用、Excel、节点账号、基础设施、负载、安全和部署策略回归；前端构建阶段还会执行 Prettier 格式检查、ESLint（含复杂度告警）与 Vitest 单元测试。任一失败即停止，不生成发布包。门禁报告写入 `docs/verification/release-test-gate.json`。输出到 `artifacts/`：

- `SchoolTraining-0.1.0-Setup.exe`：自包含安装程序，申请管理员权限后注册本系统服务。
- `SchoolTraining-0.1.0-win-x64.zip`：离线发布包、安装维护脚本和文档。
- `SchoolTraining-0.1.0-SHA256SUMS.txt`：发布包校验值。

安装程序可以使用 `--quiet --validate-only` 验证嵌入载荷；该模式只在临时目录解压检查，不注册服务、不修改防火墙、不安装程序。

## 部署和验收

默认程序目录为 `C:\Program Files\SchoolTraining`，持久数据目录为 `C:\ProgramData\SchoolTraining`。服务名称为 `SchoolTraining`，默认地址 `http://127.0.0.1:5080`。修改监听、备份恢复、更新与卸载见 [Windows 部署与运维](docs/windows-operations.md)。

- [需求与验收矩阵](docs/requirements-acceptance.md)：原文、环境修订、待确认范围和逐项证据。
- [交付验证清单](docs/delivery-checklist.md)：源码构建、模拟流程、安装与恢复的验证方法。
- [API 契约](docs/api-contract.md)：前后端接口约定。
- 界面回归（可选，不属发布门禁）：`node tests/ui-smoke.mjs`，需要本机 Chromium/Edge 与 `playwright-core`；覆盖初始化登录、万行导入预览分页、仅看问题行和服务异常重试页，报告写入 `docs/verification/ui-smoke.json`。

当前版本仍在迭代。发布包生成、构建成功或模拟测试成功，均不能单独证明完整采购参数已验收；以验收矩阵中的实测证据为准。
