# 交付验证清单

## 构建与软件清单

- [x] p4 完整发布门禁通过 15 项检查。报告列明被测版本、程序集 SHA-256 及每项退出码。参见[发布测试门禁](verification/release-test-gate.json)和[p4 部署验证](verification/deployment-p4.json)。
- [x] p4 ZIP、安装器、SHA-256、版本说明以及依赖清单已验证。解压启动时加载随包分发的 .NET 与 SQLite，检查未发现禁止路径。参见[p4 部署验证](verification/deployment-p4.json)。
- [x] p4 `Setup.exe --quiet --validate-only` 返回 0，前后均未注册平台服务。Edge 仅允许回环访问，初始化与模拟设备页面加载成功，外部请求为 0。参见[p4 部署验证](verification/deployment-p4.json)。
- [x] p5 发布目录通过 16 项发布门禁和 30 项 Edge 回归。ZIP、安装器、四个部署脚本与发布证据的 SHA-256 已核对，校验模式返回 0。参见[p5 部署验证](verification/deployment-p5.json)和[p5 界面回归](verification/ui-smoke-p5.json)。
- [x] p6 通过 16 项发布门禁及 31 项 Edge 场景。参见[p6 部署验证](verification/deployment-p6.json)和[p6 界面回归](verification/ui-smoke-p6.json)。
- [x] p7 通过 17 项发布门禁及 43 项 Edge 场景。参见[p7 部署验证](verification/deployment-p7.json)和[p7 界面回归](verification/ui-smoke-p7.json)。
- [x] p8 通过 17 项发布门禁及 44 项 Edge 场景。ZIP、安装器和部署脚本已核对哈希，安装器校验模式通过，原生解包进程使用随包运行时与 SQLite。参见[p8 部署验证](verification/deployment-p8.json)和[p8 界面回归](verification/ui-smoke-p8.json)。
- [x] p9 通过 17 项发布门禁及 47 项 Edge 场景，包括追加的节点账号跨页分配。安装器载荷校验、自包含启动与包内恢复报告核对通过。[发布门禁](verification/release-test-gate.json) · [p9 界面回归](verification/ui-smoke-p9.json) · [p9 本机部署复核](verification/deployment-p9.json)
- [x] 核对 p8 依赖清单。唯一未随 npm 包提供独立许可文本的条目为 `lodash-unified@1.0.3`，登记许可为 MIT。逐一核对 15 个发布 JavaScript 文件，均未包含该包的模块。参见[依赖复核记录](verification/dependency-notice-p8.json)和[npm 包记录](https://www.npmjs.com/package/lodash-unified/v/1.0.3)。

## 业务与模拟环境

完整范围和各版本证据见[需求矩阵](requirements-acceptance.md)。含版本的复选框只记录对应版本的验证。

- [x] p4 首次管理员初始化、重复初始化保护和未登录拒绝已通过接口回归。参见[应用接口回归](verification/application-smoke.json)和[基础设施回归](verification/infrastructure-smoke.json)。
- [x] p5 专业和班级的新增、查看、搜索、编辑和删除流程通过，接口验证了关联删除限制。参见[p5 界面回归](verification/ui-smoke-p5.json)和[应用接口回归](verification/application-smoke.json)。
- [x] p4 教师和学生新增、编辑、查看、关键词搜索、密码重置、单删和批量删除已通过 Edge 回归。参见[p4 扩展界面验证](verification/ui-smoke-p4-extended.json)。
- [x] p5 班级按专业、师生按班级、教师按角色的筛选通过。浏览器覆盖关键词组合、清空、无匹配结果和切换条件时清空勾选。参见[p5 界面回归](verification/ui-smoke-p5.json)。
- [x] p5 教师和学生 CSV 导入在界面定位错误行和列，移除错误行后提交有效记录。接口验证 XLSX、已有账号保留、重复提交保护及导入取消。参见[p5 界面回归](verification/ui-smoke-p5.json)、[应用接口回归](verification/application-smoke.json)和[Excel 回归](verification/excel-smoke.json)。
- [x] p8 首次初始化创建模拟机柜、设备、节点和虚拟机。模拟所属及重复初始化通过，重启后各资源数量与身份保持一致。参见[模拟环境重启回归](verification/simulation-restart-p8.json)。
- [x] p8 模拟终端通过 CR、LF、CRLF、BS、DEL 和实际命令的联调。测试在活动会话中停止应用进程，重启后会话占用已释放。回收并重连后配置仍可读。参见[模拟环境重启回归](verification/simulation-restart-p8.json)。
- [x] p7 机柜与设备页面通过新增、编辑、搜索、分页删除、关联保护和凭据脱敏。批测显示机柜名称，从机柜进入后保留页面归属且新增默认使用当前机柜。参见[p7 界面回归](verification/ui-smoke-p7.json)。
- [x] p4 师生授权隔离、撤销后会话释放，以及模拟设备 Web 与 CLI 配置互通和并发冲突已通过回归。参见[基础设施回归](verification/infrastructure-smoke.json)和[p4 扩展界面验证](verification/ui-smoke-p4-extended.json)。
- [x] p4 五类许可证额度、批量导入原子性、并发、续期和到期处理已通过接口回归，Edge 已验证批量上传及使用说明下载。参见[应用接口回归](verification/application-smoke.json)和[p4 扩展界面验证](verification/ui-smoke-p4-extended.json)。
- [ ] 学校如要启用许可证额度，为目标机配置供应商公钥并导入匹配设备特征码的正式许可证。
- [x] p8 模拟节点启停、虚拟机状态和资源统计一致。重启后保留虚拟机状态、节点时间和网络配置、账号分配及其统计。参见[基础设施回归](verification/infrastructure-smoke.json)和[模拟环境重启回归](verification/simulation-restart-p8.json)。
- [x] p8 节点分配页面跨两页保留已勾选账号，超配额时禁用提交。一批 11 个账号分配成功，移除末页最后一项后列表回到有效页，账号重返候选且原档案保留。参见[节点账号回归](verification/ui-smoke-p8-node-accounts.json)。
- [x] p5 集群列表和详情的健康状态与使用率一致。接口验证搜索、分页、虚拟机启停引起的指标变化及未接入节点空值。参见[p5 界面回归](verification/ui-smoke-p5.json)和[基础设施回归](verification/infrastructure-smoke.json)。
- [x] p8 通过 501 节点环境中的虚拟机增、改、启、停、删。按名称选中第 501 个模拟节点后可以保存归属，编辑会回显，节点使用率随运行状态变化。参见[p8 虚拟机回归](verification/vm-node-pagination-p8.json)。
- [x] p4 模拟节点时间、IP、掩码和网关在 Edge 中保存成功，Win11 主机保持只读。CPU、内存与网卡采集共 13 项本机系统对照通过。参见[p4 扩展界面验证](verification/ui-smoke-p4-extended.json)和[主机清单对照](verification/host-inventory.json)。
- [x] p8 在 Win11 对照 CPU、内存和网卡。缺失值显示“不可获取”，真实的 0 保留，可用内存缺失时不计算使用率。参见[p8 主机回归](verification/host-inventory-p8.json)。
- [ ] 错误、空状态、加载状态、无权限状态与窄屏布局可用，按钮不静默失败。
- [x] p4 Edge 已通过 10,000 行导入预览、50 行分页、仅看问题行，以及服务恢复后重连。参见[p4 扩展界面验证](verification/ui-smoke-p4-extended.json)。

## 安全和数据

- [ ] Cookie/会话配置、CSRF、角色权限、口令重置失效逻辑有实测。
- [ ] 设备密码、跳板密码、密码散列和授权私钥不会出现在列表或审计中。
- [ ] 学校目标容量下的 SQLite 并发写入、导入和采集叠加压力测试。p5 本机实际导入 10,000 条并完成 8 笔并发写入。p6 浏览器验证 10,000 行预览。参见[万条实际导入](verification/load-smoke-p5-10000.json)和[p6 界面回归](verification/ui-smoke-p6.json)。
- [x] p7 隔离回归用真实 SQLite 数据、DPAPI 密钥和在线生成的备份 ZIP。恢复后验证受保护数据可用，新实例健康失败时验证原数据、密钥和运行状态复原。参见[恢复回归](verification/deployment-restore.json)。
- [x] p4 验证 v1–v4 旧库迁移至 v5，保留原始业务数据和预迁移快照，为内置模拟设备建立节点关联。旧库存在重复班级时阻断迁移。参见[迁移回归](verification/migration-smoke.json)。
- [x] 日志按保留天数清理；备份按数量和总容量保留；SQLite 写锁返回 503 与 Retry-After；审计日志按 `(At, Id)` 倒序稳定分页并有索引支撑。应用回归中已验证旧日志删除、备份上限和忙锁映射；证据见 `verification/application-smoke.json`，索引证据见 `verification/migration-smoke.json`。
- [x] p7 备份空间专项测试共 7 项。实际 Win11 可读取备份目录所在卷，注入空间未知和不足时返回 507，SQLite 实际触及页上限时同样返回 507，检查没有留下不完整备份。测试未填满宿主机磁盘。
- [x] 恢复脚本预检条目路径与解压大小。p9 用同一归档句柄逐段写入，每个条目的实际字节数受预检长度约束。Windows PowerShell 5.1 和 PowerShell 7 各通过 5 项普通内存流边界验证。共享路径检查及 p8 的备份清单、数据库和密钥输入回归可分别复核。[文件安全回归](verification/deployment-safety-windows-powershell5.json) · [p9 恢复回归](verification/deployment-restore.json) · [p8 部署输入回归](verification/deployment-inputs-p8.json)

## 专用 Win11 验证机

以下步骤需要实际执行，不以脚本语法检查代替：

- [ ] 清洁机器不安装 .NET SDK、Node.js、独立数据库和容器，离线安装成功。
- [ ] UAC、Program Files 与 ProgramData 路径、专用服务账户 ACL 正确。
- [ ] 服务开机启动、故障恢复与正常停止可靠。
- [ ] 首次初始化、局域网访问与学校 HTTPS 证书正确。
- [ ] 申请局域网监听但未配置有效 HTTPS 时，安装器在修改服务前明确拒绝。
- [ ] 升级保留数据库、密钥和设置；失败回退经过验证。
- [ ] 在安装版服务上验证 v1–v4 自动迁移到 v5 及迁移前快照。
- [ ] 备份恢复、恢复失败回滚通过；原备份和日志保持。
- [ ] 卸载保留数据，重装可按兼容版本继续使用。
- [x] 纯文件路径/卸载保留/升级冲突安全回归，Windows PowerShell 5.1 和 PowerShell 7 各 19/19 通过；未执行服务或系统配置操作，证据见 `verification/deployment-safety-*.json`。
- [x] p5 安装及升级回退隔离测试在 PowerShell 5.1 和 PowerShell 7 各通过 10 项。验证涉及实际文件和备份，主机服务、注册表、防火墙及 ACL 由测试替身处理。参见 [PowerShell 5.1 报告](verification/deployment-rollback-windows-powershell5.json)和 [PowerShell 7 报告](verification/deployment-rollback-powershell7.json)。
- [x] p9 恢复脚本在 Windows PowerShell 5.1 和 PowerShell 7 各通过 11 项回归。包含 5 项复制边界及 6 项真实备份的恢复、回退与并发保护。注册和服务控制由测试替身处理。[PowerShell 5.1 报告](verification/deployment-restore.json) · [PowerShell 7 报告](verification/deployment-restore-p9-powershell7.json)
- [x] p9 包内安装与恢复脚本在两种 PowerShell 各通过 14 项输入检查。正常备份和带空密钥目录条目的普通 ZIP 都通过文件哈希、SQLite 和 DPAPI 验证。[Windows PowerShell 5.1](verification/deployment-inputs-p9-windows-powershell5.json) · [PowerShell 7](verification/deployment-inputs-p9.json)
- [x] p8 包内安装脚本拒绝缺失配置、明文 HTTP 及无效证书，已验证缺证书、过期、无私钥、PFX 密码错误和 DNS 不匹配。Windows PowerShell 5.1 与 PowerShell 7 均在尝试修改系统服务前拒绝全部七项。参见[Windows PowerShell 5.1 报告](verification/deployment-inputs-p8-windows-powershell5.json)和[PowerShell 7 报告](verification/deployment-inputs-p8.json)。

## 项目验收材料

- [ ] 根据需求矩阵补齐功能证据，原文截断部分与歧义已确认或形成书面边界。
- [x] p4 已生成学生管理、设备信息、集群信息和集群配置四项页面截图，主机与网络标识已脱敏。参见[p4 扩展界面验证](verification/ui-smoke-p4-extended.json)。
- [x] p9 验收包归档学生管理、设备信息、集群信息与集群配置四项截图，以及单独的内存和网卡截图。[界面回归](verification/ui-smoke-p9.json)
- [x] p9 验收包的机柜视频展示模拟环境中的批量连接测试、终端调试、回收和重连，验证已保存的配置继续可读。[演示记录](verification/cabinet-demo-p9.json)
- [ ] 如果后续用于采购响应，由供应商完成其公章材料；这属于外部响应资料，不是软件运行功能的测试步骤。
- [x] 已确认本期采用内置模拟设备、模拟节点和虚拟机，Win11 管理主机只读。参见[需求矩阵](requirements-acceptance.md)。
- [ ] 维护责任、版本更新、数据备份负责人、容量目标和培训记录明确。
