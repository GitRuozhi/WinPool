# WinPool 质量与验收

[English](Quality.md) | [简体中文（仅供阅读）](Quality.zh-CN.md)

> 本文件仅为中文阅读副本；质量与验收规则以无 `.zh-CN` 后缀的
> [Quality.md](Quality.md) 为准。

## 结果词汇

每个质量门或用例必须使用以下之一：`passed`、`failed`、`unverified`、`not_required`、`deferred_by_user`。跳过或不可用的检查不得报告为通过。

## 质量模型

WinPool 是原生 Windows 多进程 .NET 应用。参考项目中的浏览器 DOM 测试、Web 服务器检查和网页截图规则不适用，除非 WinPool 后续单独批准 Web 界面。

### 静态和结构门

- 必需的仓库和文档结构存在。
- 活动阶段存在时，只有一个活动 `docs/Plan.md`。
- 每份英文权威 Markdown 都有匹配的 `.zh-CN.md` 阅读副本，且副本明确声明以无后缀文档为准。
- Markdown 链接和文档路径可以解析。
- 版本源、运行时显示值和受控文档一致。
- 架构边界、封闭诊断、类型化命令和默认拒绝执行持续受测试保护。
- Git 范围包括软件引用的 `assets`，排除 `OriginArtWork`、本地资源、生成输出、数据库、日志、外部工具和发布二进制。

### .NET 自动门

在 WinPool 仓库根运行：

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

测试不得修改真实存储结构。构建警告必须有明确解释或用户批准的例外。

### Windows 原生集成门

- App、Agent、TestWorker 和 Broker 发布到要求的嵌套路径。
- App 运行时查找路径与 staging 树一致。
- 命名管道身份和 ACL、Worker 清理、SQLite 所有权及只读采集边界由自动或受控本地集成检查覆盖。
- Staging 不包含脚本、艺术源文件、数据库、测试结果、外部工具或重复子进程可执行文件。

### 人工和设备门

原生 WinUI 表现、双语切换、主题、DPI、高对比度、键盘、托盘生命周期、UAC、原生文件夹选择器、登记的 D: 工具执行、长时间监控、取消、恢复和数据位置往返都需要人工证据。

当前 V0.41 矩阵固定人工根为 `D:\WinPool-V041-Manual-Test`。人工检查不得选择其他盘根、源码树、网络共享或未登记目录。

对于 V0.5 或以后受控真实修改验证，自动测试和 CI 仍只允许模拟。单个人工用例只有在开发 Agent 取得开发者针对该操作的批准，或产品用户在当前会话显式选择“本机真实修改”后，才能执行一次真实操作。证据必须记录操作、目标、授权上下文、选中状态和结果。

## 验收策略

- 自动门建立确定性工程事实，但不能批准视觉意图或物理设备行为。
- 没有用户证据时，Agent 不得把人工门标记为通过。
- 批准的例外需要记录原因、范围、批准人、日期、风险和到期时间。
- 测试数量写入活动 Plan 或 CHANGELOG 证据，不写入长期策略。
- V0.41 永远不接受真实硬件结构修改作为验证技术。V0.5 或以后受控真实修改用例遵守已记录的显式授权流程，且不得把未执行用例写为通过。
