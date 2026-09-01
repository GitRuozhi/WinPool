# WinPool 质量与验收

[English](Quality.md) | [简体中文（仅供阅读）](Quality.zh-CN.md)

> 本文件仅为中文阅读副本；质量与验收规则以无 `.zh-CN` 后缀的
> [Quality.md](Quality.md) 为准。

## 结果词汇

每个质量门或用例必须使用以下之一：`passed`、`failed`、`unverified`、`not_required`、`deferred_by_user`。跳过或不可用的检查不得报告为通过。

## 何时运行质量门

有完整测试能力，不代表每次修改都应该执行全部测试。

- 纯文档修改不执行代码、native、设备或视觉测试。
- 普通小修改和普通功能开发不执行完整质量门。
- 开发者指定某项测试时，执行该范围。
- 若修改存在明显局部风险，可以执行与当前修改直接相关的最小检查，不得自动升级为完整验证。
- 正式阶段实现完成不自动开始完整验收。应询问开发者是否进入正式测试。
- 正式版本或正式验收在开发者确认后，按下列门执行。

具体阶段的测试目录、专项矩阵和验收结果进入当前 Plan 或 Archive，不写入本长期制度。测试数量若属于重要最终结果，写入 Plan 或 CHANGELOG，不写入本政策。

WinPool 1.x 的“测试”和“开发”标签页只作为路线占位页。1.x 正式验收只验证这些占位页存在、足够简单、提供双语说明且可访问。测试工作区、外部基准工具执行以及开发者/AI 工作区用例对 1.x 为 `not_required`；只要内部代码仍保留在解决方案中，就继续接受自动回归覆盖。

## 质量模型

WinPool 是原生 Windows 多进程 .NET 应用。参考项目中的浏览器 DOM 测试、Web 服务器检查和网页截图规则不适用，除非 WinPool 后续单独批准 Web 界面。

### 静态和结构门

- 必需的仓库和文档结构存在。
- 活动阶段存在时，只有一个活动 `docs/Plan.md`。
- 每份英文权威 Markdown 都有匹配的 `.zh-CN.md` 阅读副本，且副本明确声明以无后缀文档为准。
- Markdown 链接和文档路径可以解析。
- 产品版本源是 `Directory.Build.props`。运行时显示值必须与该源一致。提到产品版本的文档不得与之矛盾。
- 架构边界、封闭诊断、类型化命令和默认拒绝执行持续受测试保护。
- Git 范围包括软件引用的 `assets`，排除 `OriginArtWork`、本地资源、生成输出、数据库、日志和发布二进制。

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

- App 和 Agent 先独立发布，再合并成一个扁平 portable 目录。相对路径相同且
  SHA-256 相同：只存一份。SHA-256 不同：staging 失败。
- 本地 `dotnet build` 把同一棵扁平树写到 `artifacts\$(Configuration)\`。
- App 运行时查找路径与 staging 树一致（`WinPool.Agent.exe` 在 App 旁边）。
- 命名管道身份和 ACL、SQLite 所有权及只读采集边界由自动或受控本地集成检查覆盖。
- Staging 不包含脚本、PDB 文件、艺术源文件、数据库、测试结果或重复子进程可执行文件。构建产物仍可包含 PDB。

### 人工和设备门

原生 WinUI 表现、双语切换、主题、DPI、高对比度、键盘、托盘生命周期、原生文件夹选择器、监控启动/停止和数据位置往返都需要人工证据。

正式人工矩阵使用活动 Plan 或定义该阶段的归档中给出的目录。人工检查不得选择源码树、网络共享或未登记目录。

对于 Product 允许阶段中的受控真实修改验证，自动测试和 CI 仍只允许模拟。单个人工用例只有在开发 Agent 取得开发者针对该操作的批准，或产品用户在当前会话显式选择“本机真实修改”后，才能执行一次真实操作。证据必须记录操作、目标、授权上下文、选中状态和结果。

## 验收策略

- 自动门建立确定性工程事实，但不能批准视觉意图或物理设备行为。
- 没有用户证据时，Agent 不得把人工门标记为通过。
- 批准的例外需要记录原因、范围、批准人、日期、风险和到期时间。
- 真实硬件结构修改不是可接受的验证方法，除非已确认 Plan 在允许真实修改的阶段要求该项验证。未执行用例保持 `unverified`。
