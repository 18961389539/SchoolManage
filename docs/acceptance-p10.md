# 0.1.0-p10 合同功能与验收说明

本次交付以《人工智能软件采购合同20261120》中的实训平台功能为核对范围。安装器和离线包包含当前源码中的课堂、考试、开放式资源库、技能图谱与岗位训练营，数据库结构为 v16。

## 功能验证

| 条款 | 本次结果 |
|---|---|
| 1.1.6、1.6.10 | 按确认范围使用模拟机柜。课堂分组分配、批量测试、取消分配和回收已验证。 |
| 1.2.2 | 模拟节点可配置时间、IP、掩码和网关。Win11 管理主机按确认范围只读。 |
| 1.3.1 至 1.3.3 | 模拟虚拟机搜索、连接、重启及销毁通过回归。现已增加 Hyper-V 主机状态采集和受管虚拟机搜索、电源操作与 RDP 连接入口，通过本机 PowerShell 测试替身验证。真实主机联调待安排。 |
| 1.6.4、1.7.3 | 已回归课堂实操保存、恢复、尝试评分、销毁评分和重启后的历史记录。评分过程显示阶段，记录按分值汇总平均分与最高分。 |
| 1.6.8 | 已验证模拟机监控、连接、重启与学生授权后的画面共享及助教协作。教师在真实 Hyper-V 来宾系统中的同屏控制尚缺环境接入。 |
| 2.3.10、2.4.2、2.5.2 | 已验证模拟考试环境批量部署、作答连接、重启、到时交卷、评分和回顾。实操题可编辑文本参考资料并随试卷快照保存。真实 Hyper-V 考试环境部署尚缺接入。 |
| 第 3 节 | 已回归课程激活、核心课系列、课程目录、文件预览和课堂资源访问。 |
| 第 4 节 | 已验证模拟岗位测评环境、重启、到时交卷、自动评分、历史记录及报告。真实 Hyper-V 岗位测评环境尚缺接入。 |

发布快照使用独立目录进行构建与格式整理。19 项发布门禁全部通过，其中包括 210 项后端单测、31 项前端单测和课堂、考试、岗位实操接口回归。安装器载荷校验退出码为 0。46 项 Edge 界面回归通过，另完成课堂、考试和岗位专项页面操作。验收包中的截图和演示视频来自同一个 p10 构建，使用测试数据。

以上数字是 p10 构建的快照。后续构建的提交、门禁项数和证据哈希以发布包内 `release-manifest.json` 为准，不再用本文数字推断当前版本。

## 补充材料索引

| 条款 | 截图或演示 |
|---|---|
| 1.1.4 学生管理 | `screenshots/p10-student-management.jpg` |
| 1.1.6 机柜管理 | `videos/cabinet-management.webm` |
| 1.2.1 设备信息 | `screenshots/p10-server-information-redacted.jpg`，另有内存和网卡详图 |
| 1.3.1 集群信息 | `screenshots/p10-cluster-information.jpg` |
| 1.3.2 集群配置 | `screenshots/p10-cluster-configuration.jpg` |
| 1.3.3 虚拟机信息 | `screenshots/virtual-machine-information.png` |
| 1.6.5 课堂统计 | `screenshots/classroom-statistics.png` |
| 1.6.8 实操监控 | `screenshots/classroom-practical-monitoring.png` |
| 1.6.10 分配机柜 | `videos/classroom-cabinet-assignment.webm` |
| 2.3.8 考试统计 | `screenshots/exam-statistics.png` |
| 2.3.10 考试部署 | `videos/exam-deployment.webm` |
| 2.4.2 实操答题资源 | `screenshots/exam-practical-resources.png` |
| 2.5.2 考试动态与回顾 | `screenshots/exam-review.png` |
| 4.1.3 学生岗位报告 | `videos/camp-student-report.webm` |
| 4.3.3 岗位报告 | `screenshots/camp-staff-report.png` |

## 现场仍需完成的事项

- 供应商审核并为合同要求的截图加盖公章。
- 提供可用的 Hyper-V 主机、来宾系统模板及连接权限后，完成真实考试部署、课堂监控和岗位测评的接入与验证。
- 在学校目标机验证原生 Windows 服务、校内 HTTPS、升级、备份恢复及目标并发容量。

