# 变更记录

[English](CHANGELOG.md) | [简体中文（仅供阅读）](CHANGELOG.zh-CN.md)

> 本文件仅为中文阅读副本；实际变更历史以无 `.zh-CN` 后缀的
> [CHANGELOG.md](CHANGELOG.md) 为准。

本文件只记录已经实际发生的结果。活动阶段的计划工作保留在 `Plan.md`，历史计划保留在 `Archive`。

## Unreleased

V0.33 之后暂无未发布变更记录。

## V0.33 — 2026-08-11

- 用户明确确认 V0.33，并授权归档文档、提交 Git 和推送 `main`。

- 将 `WinPool.Core` 收敛进权威 Application 模型，并保留系统/文档身份、模拟、投影、
  启动、通知和布局行为。
- 强化 Agent、Worker、Broker、Control IPC 和 Event IPC 生命周期：可重试关闭、有界
  进程终止、typed abort、坏客户端隔离、断线恢复、snapshot reseed 和明确 event-gap 状态。
- 增加进程实例身份、有界 terminal diagnostics 和真实 SQLite v10→v11 历史迁移；
  V0.33 唯一一次 wire protocol bump 为 2。
- 外部工具路径改由 Agent 持有；每次工具调用只解析一次 numeric output code page，
  stdout/stderr 分别进行 stateful decoding，同时保留原始字节。
- storage-location 从覆盖复制改成同卷精确 staging 事务：捕获源和目标，只 drain 源
  store，验证 manifest 与 SQLite identity，在取消或失败时恢复旧目标，并移除陈旧的
  managed target payload。
- 将测试状态、系统支持和库存所有权拆入三个聚焦的 Agent coordinator，
  `DesktopAgentRuntime` 继续作为 request facade。
- 全部 486 项 Release 测试、无警告 Release 构建、传递依赖审计、Markdown 检查和
  V0.33 四进程 staging 均通过。
- 十项原生/人工用例继续保持 `unverified`；版本确认不代表这些用例通过。

V0.33 是已确认的项目版本，不是 tag、二进制发布或 GitHub Release。
实现提交范围：`6b66c68` 至 `0dcd22a`；版本提交 `38ff043`；验收文档提交
`e148b61`。这些提交已存在于 `origin/main`。

## V0.32 — 2026-08-10

- 用户在审阅 V0.31 重构后明确指定 V0.32。
- 按用户规定的 `Va.bc` 规则将唯一项目版本设为 V0.32。
- 为英文项目文档增加非权威 `.zh-CN.md` 阅读副本；无后缀文档保持控制权。
- 将软件引用的 `assets` 纳入 Git，并将用户手动管理的 `OriginArtWork` 排除在 Git 外。
- 11 项尚未执行的原生/人工用例继续标记为 `unverified`；版本指定不代表这些用例通过。
- 重新验证全部 458 项 Release 测试和 V0.32 四进程嵌套 staging；四个可执行文件均报告项目版本 V0.32。
- 删除错误引入的 `TechnicalVersion` 概念。.NET/Windows 必需的数字字段是派生编译元数据，不是另一套项目版本。

V0.32 是当时确认的项目版本，不是 tag、二进制发布或 GitHub Release。
提交：`dc5e263`、`7b7a798`（已推送到 `origin/main`）。

### V0.31 文档架构修正 — 2026-08-10

- 用规定的 `docs` 信息架构替换错误的根 `Plan` 布局。
- 恢复用户批准的仓库内文档归档策略。
- 将错误 V0.31 计划保存为已替代的审计历史，不改写或 force push Git 历史。
- V0.32 人工验收保持未验证。
- 前向修正提交：`236eb3f`（已推送到 `origin/main`）。

本次修正不是 tag、二进制发布或 GitHub Release。

## V0.31 源码集成 — 2026-08-10

- 增加共享 V0.31 版本源。该提交也曾错误地把数字编译元数据命名为技术版本；V0.32 后续修正了该语义。
- 增加可复现四进程发布 staging 和真实布局验证。
- 更新源码和自动架构检查。
- 提交：`6cf68e3`、`8d7fb25`。

上述提交中的原文档归档决定无效，并由前述修正替代。

## V0.21 — 2026-08-09

- 发布采用 V0.13 视觉基线的 V0.2 多进程架构集成。
- 在 `ec8b34a` 修复无打包部署打包基线。
- 发布提交：`fcebb67`。
