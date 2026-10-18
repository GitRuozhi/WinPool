# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool 是 Windows 桌面存储管理软件，可查看存储拓扑、编辑模拟系统、监控设备，并通过受控流程执行已支持的本机存储操作。

**当前源码版本为V0.62，正在按[已批准活动计划](docs/Plan.md)进行开发机验收，尚未公开发布。** 实际结果、候选身份及外部待验范围见[Quality](docs/Quality.md)。[V0.61阶段记录](docs/Archive/20261018-v061-completed/Plan.md)保留为历史；唯一版本源为[Directory.Build.props](Directory.Build.props)。

## 使用范围

- 管理页查看池、层、物理／虚拟磁盘、分区等关系；设置中开启开发者模式后可查看硬件报告、测试路线说明和本次运行消息。
- 存储结构页以草稿预览整批变化后 Apply，由 Agent 执行已确认的结构作业；磁盘分区页逐项提交。新池默认生成虚拟磁盘及分区目标，已有 RAW 虚拟磁盘不会自动补建分区。草稿在当前 App 生命周期内跨页面保留，模拟系统按需加载。
- 合法模拟Apply／Enter立即显示本地候选，后台合并保存；失败保留本地内容并显示状态。完整可导入文档envelope的规范编码上限为64 MiB。模拟和JSON导入／导出不修改真实磁盘，模拟结果不证明Windows能执行相同配置。
- 真实编辑默认关闭，使用准确目标和当次受控确认。App 关闭而 Agent 仍运行时，已接受作业可继续；Agent 重启仅只读核对，不自动续写。结果未知时阻止后续写入。支持范围取决于 Windows/provider；真实 MBR→GPT、已有 VD／层扩展到 MAX、直接池成员迁移仍不支持，完整边界见[Product](docs/Product.md)。
- 监控支持 0.2–20 Hz，以批次推送更新图表和告警，连接／序列缺口后重同步窗口。记录到独立数据库，达到 1 GiB 后轮换并以随附 7-Zip 归档；历史包不自动删除。CSV 仅导出当前活动库，没有监控历史浏览功能。
- 提供中英双语、主题和键盘操作。硬件字段保持原值，导出前由使用者判断分享范围。

本轮实现不代表所有环境或性能场景已经通过；普通权限启动、实机操作和候选包的实际结果以 Quality 为准，历史审查和提速数字不替代当前验证。

## 运行与构建

目标支持 Windows 10 22H2 x64，主要平台为 Windows 11 24H2／25H2 x64；跨环境覆盖尚待外部测试，不能把支持目标当作已验证矩阵。

当前使用完整的 x64 便携运行目录，App 和 Agent 必须配套。运行 `WinPool.App.exe`，Agent 位于用户托盘；替换文件前退出两者。默认数据位置是 `%LocalAppData%\WinPool`，也可显式选择可写的程序旁 `Data`。尚无正式 MSIX 或 Store 发布。

产品使用Windows自带的Windows PowerShell 5.1；运行便携包不要求另装PowerShell 7或开发SDK。项目实测兼容与上游.NET正式支持分开记录；.NET 10支持列表未列Windows 10 Pro 22H2。

候选包提供内含 `TESTING.md` 的 zip 及独立 SHA-256 校验文件；完整解压后运行，反馈请附版本、Windows／设备环境、复现步骤及相关错误。候选包生成和内部验收不等于已公开发布。

在 Windows、PowerShell 与 `global.json` 指定的 SDK 环境中构建：

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

开发规则见 [AGENTS](AGENTS.md)，构建与数据所有权见[Development](docs/Development.md)。

## 研究背景

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

研究结果只代表对应样本，软件平台测试和存储可靠性研究是不同的覆盖范围。

## 权利

本仓库未对WinPool自有代码授予许可证，保留所有权利。第三方组件遵循各自许可证，见[随包运行库索引](assets/ThirdParty/RuntimeLicenses/README.md)；7-Zip的[许可证](assets/ThirdParty/7zip/26.03/License.txt)及[来源说明](assets/ThirdParty/7zip/26.03/NOTICE.txt)单独提供。
