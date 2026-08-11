# WinPool Agent 操作说明

[English](AGENTS.md) | [简体中文（仅供阅读）](AGENTS.zh-CN.md)

> 本文件仅为中文阅读副本；所有操作约束以无 `.zh-CN` 后缀的
> [AGENTS.md](AGENTS.md) 为准。

本文件保存 `Program\WinPool` 内稳定的操作规则。产品方向、架构、质量门、当前工作、结果和历史分别由 `docs` 下对应文档管理。

## 优先级和必读顺序

规则冲突时按以下顺序处理：

1. 用户在当前任务中的明确决定。
2. 父级和本地 `AGENTS.md` 中的安全、授权与受保护数据规则。
3. `docs/Product.md`。
4. 已确认的 `docs/Plan.md`（若存在）。
5. 项目契约和已批准设计规则。
6. `docs/Development.md` 与 `docs/Quality.md`。
7. 当前实现和自动证据。
8. 作为历史记录的 `docs/CHANGELOG.md` 与 `docs/Archive`。

通用父级规则不得暗中替换用户更具体的决定。用户已经明确决定：WinPool 历史文档进入 `docs/Archive`；父项目的 `Old` 规则仍适用于非文档的 WinPool 过期内容。

修改项目前，阅读根 README、本文件、Product、Development、Quality 和当前 Plan；检查 Git 状态、分支、上游与受保护路径。

## 环境和范围

- 支持 Windows 和 PowerShell 开发环境。
- 解决方案目标为 .NET 10、WinUI 3、Windows App SDK 和无打包 x64。
- 除非任务明确指出其他路径，只处理 `Program\WinPool`。
- 不重组 Dite、KS、Research、Tests、Showcase 或其他项目。
- 保留无关和用户已有的修改。

## 安全与数据边界

- 不实现或启用真实磁盘、分区、卷、存储池、存储层或虚拟磁盘的创建、删除、初始化、格式化、调整、修复或等价修改。
- 存储结构变化只通过模拟实现，即使界面处于 Real 模式也一样。
- 文件测试只能操作明确选择的测试目录中、登记到本次运行的文件；禁止裸设备写入。
- 缓存清理、卷刷新、TRIM/Optimize、进程调度和临时电源计划等辅助动作需要类型化计划、目标校验、必要确认、审计和可逆状态恢复。
- DiskSpd、fio、Dite、RoboCopy、RAMMap 继续作为单独安装的外部工具，由类型化适配器调用；不得捆绑或重新实现。
- 持久化、导出、导入、记录或复制的硬件数据必须经过批准的脱敏边界。
- 不保存或发布独立采集 `.ps1`；固定只读 PowerShell 保持程序集内嵌并通过标准输入传递。

## 文件和文档规则

- 默认不直接删除文件。只有用户批准的重构，并且已确认 Plan 明确列出准确的
  过期源码/测试目标、要求先完成替代实现与回归证据时，才可以获得狭义例外。
  用户已批准 V0.33 退役 `src/WinPool.Core` 与 `tests/WinPool.Core.Tests`
  使用该例外；这不代表 V0.33 Plan 确认前可以开始执行，也不授权删除其他内容。
- WinPool 历史文档进入 `docs/Archive`，并记录真实状态和索引。
- 其他确认过期的 WinPool 内容进入父项目 `Old`，尽量保留相对路径。
- 低价值生成内容进入父项目 `Rubbish`。
- 不在 WinPool 内创建本地 `Old`、`Rubbish` 或变体目录。
- `README.md` 和 `README.zh-CN.md` 是面向用户的入口。
- `AGENTS.md` 只保存操作约束。
- Product、Development、Quality、Plan、CHANGELOG、Reference 和 Archive 内容归 `docs` 管理。
- 同一时间最多只有一个活动 `docs/Plan.md`；无活动阶段时可不存在。
- 已完成或已失效计划冻结在 `docs/Archive`，不得把历史改写成从未出错。
- `assets` 保存软件引用资源并纳入 Git。
- `OriginArtWork` 保存用户手动管理的艺术源文件并保持 Git 忽略，直到用户批准 Git、Git LFS 或其他资源方案。
- `.zh-CN.md` 是对应无后缀 Markdown 的非权威中文阅读副本；两者不一致时，以无后缀文档为准。

## 版本和 Git 规则

- 产品版本采用 `Va.bc`：`a` 为大版本，`b` 为小版本，`c` 为该小版本的一位迭代编号。
- `Va.bc` 是唯一项目版本体系。.NET/Windows 必需的数字字段只能是机械派生的编译元数据，不得命名或记录为另一套项目版本。
- 架构与路线图通常只写到 `Va.b`。
- `c=8` 或 `c=9` 时提醒开发者控制范围；不得创建 `c=10`。
- 普通 `c` 检查点需要本地提交；除非明确授权，不推送、不打 tag、不发布。
- 在已批准的 V0.33 Plan 执行期间，V0.32 仍是用户确认的当前源码检查点；剩余人工用例保持 `unverified`。
- V0.33 工作可创建其已确认 Plan 要求的本地提交。未授权 push、tag、GitHub Release、二进制上传或部署。
- 推送前必须 fetch，确认远端目标是本地 HEAD 的祖先，并检查待推提交；拒绝分叉和 force push。
- tag、GitHub Release、二进制上传或部署始终需要单独明确授权。

## 验证规则

- 使用 `docs/Quality.md` 规定的命令和结果词汇。
- 自动检查不能替代 UAC、托盘、原生选择器、视觉、设备或长时间人工证据。
- 未运行或不可用的门不得报告为通过。
- V0.3 不允许用真实硬件结构修改作为验证方法。
