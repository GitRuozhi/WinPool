---
project: WinPool
phase: V0.3
architecture_version: V0.3
status: accepted
integration_checkpoint: V0.32
acceptance_checkpoint: V0.32
branch: main
confirmed_by_user: 2026-08-10
code_gate: passed
native_integration_gate: passed
manual_gate: unverified
remote_gate: passed
accepted_by_user: 2026-08-10
authority: docs/Archive/V0.32/Plan.md
---

# WinPool V0.31 文档架构修正

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

> 本文件仅为中文阅读副本；计划状态、任务和验收结论以无 `.zh-CN` 后缀的
> [Plan.md](Plan.md) 为准。

## 背景

第一次 V0.31 重构错误地用通用父级归档规则覆盖了用户明确决定的 `docs/Archive`。提交 `6cf68e3` 和 `8d7fb25` 保留为审计历史；本计划通过前向修正完成纠正，不 revert、不 amend、不 force push、不打 tag、不发布。

活动文档架构选择性参考项目管理资料，并保持 WinPool 原生 Windows 技术与安全模型的针对性。

## 已确认决定

- 不保留根 `Plan`；唯一活动计划是 `docs/Plan.md`。
- 根文档只保留中英文 README，以及权威和中文阅读副本的 Agent 规则。
- 产品、开发、质量、当前计划、变更记录、参考与归档全部进入 `docs`。
- WinPool 历史文档进入 `docs/Archive`；其他过期非文档内容进入父项目 `Old`。
- 软件引用资源放在纳入 Git 的 `assets`。
- 用户手动管理的艺术源文件放在被忽略的 `OriginArtWork`；在用户决定 Git、Git LFS 或其他方案前不进入 Git。
- 英文权威 Markdown 配有 `.zh-CN.md` 阅读副本；始终以无后缀文档为准。原文已经是中文的文档无需重复。
- V0.31 是源码集成检查点，不是 tag、二进制发布或 GitHub Release。V0.32 需要用户明确验收。

## 修正工作包

| ID | 状态 | 交付物 |
| --- | --- | --- |
| DOC-01 | passed | 在 `docs/Archive/V0.31-pre-correction` 保存错误 V0.31 Plan 和索引。 |
| DOC-02 | passed | 将 16 份 V0.2 归档迁入 `docs/Archive/V0.2`，保持字节和 SHA-256 一致。 |
| DOC-03 | passed | 建立 Product、Development、Quality、Plan、CHANGELOG、Reference 和 Archive 职责。 |
| DOC-04 | passed | 清除活动 `Plan`/根 `DEVELOP` 引用并修复源码证据路径。 |
| DOC-05 | passed | 完成结构、链接、Release 测试/构建、漏洞、Git 范围和远端门。 |
| DOC-06 | passed | 增加非权威中文阅读副本、跟踪 `assets` 并忽略 `OriginArtWork`。 |

## V0.32 人工验收继承项

| ID | 状态 | 用例 |
| --- | --- | --- |
| CARRY-01 | unverified | 启动、托盘和单实例行为 |
| CARRY-02 | unverified | 六个页面及只读/模拟边界 |
| CARRY-03 | unverified | 中英文、相反主题和外部工具状态 |
| CARRY-04 | unverified | 在登记 D: 目录中执行 App 内 DiskSpd |
| CARRY-05 | unverified | 取消及下一次运行恢复 |
| CARRY-06 | unverified | RoboCopy/FullHash 生成、复制、比较和导出 |
| CARRY-07 | unverified | 关闭 App 窗口后的监控连续性 |
| CARRY-08 | unverified | R3/UAC D: flush 目标、确认、Broker 和审计 |
| CARRY-09 | unverified | 完整托盘退出及子进程清理 |
| CARRY-10 | unverified | 标准→便携→标准数据位置往返 |
| CARRY-11 | unverified | 强制终止 Worker 与 Job Object 恢复 |
| CARRY-12 | passed | 实际四进程 V0.31 staging 布局及禁止内容检查 |

## 延后的 V0.3 债务

| ID | 状态 | 工作 |
| --- | --- | --- |
| DEBT-01 | deferred_by_user | 收敛 205 个兼容性 ID 的实现、自动和人工证据 |
| DEBT-02 | deferred_by_user | 原生/脚本采集一致性及失败降级 |
| DEBT-03 | deferred_by_user | 键盘、屏幕阅读器、高对比度、DPI 和窄窗口矩阵 |
| DEBT-04 | deferred_by_user | 用户批准的双语和视觉回归矩阵 |
| DEBT-05 | deferred_by_user | 多小时监控、重连、背压、句柄和内存证据 |
| DEBT-06 | deferred_by_user | 大目录、容量边界、Dite/fio/RoboCopy 适配器证据 |
| DEBT-07 | deferred_by_user | R3 失败、取消、崩溃、审计和恢复矩阵 |
| DEBT-08 | deferred_by_user | 大型数据位置迁移和回退 |
| DEBT-09 | deferred_by_user | 隐私和采集策略决定 |
| DEBT-10 | deferred_by_user | 验证推测算法或保留其置信度标签 |
| DEBT-11 | deferred_by_user | 长测试实时曲线和事件缺口补偿 |
| DEBT-12 | deferred_by_user | 完成可复现发布工程闭环 |

## 自动完成条件

- 根 `Plan` 和根 `DEVELOP.md` 不存在。
- 必需 `docs` 文件存在，`docs/Plan.md` 是唯一活动计划。
- V0.2 恰好包含 16 个未改变文件、212,510 字节和已验证的迁移前哈希。
- 所有活动 Markdown 链接和源码证据路径可解析。
- Release restore、测试、构建和依赖漏洞检查通过。
- `git diff --check` 和明确的源码/文档暂存白名单通过。
- 修正已前向推送到 `origin/main`，未包含 force push、tag、release、二进制上传、Old、Rubbish、本地资源或生成输出。

自动条件通过后，本计划进入 `awaiting_acceptance`。只有用户明确确认才能建立 V0.32。

## V0.32 验收决定

用户于 2026-08-10 明确把当前源码检查点指定为 V0.32。本决定关闭文档架构阶段并归档本计划。
该决定不改变 11 项尚未执行的原生/人工用例：它们继续保持 `unverified`，不得根据版本指定推断测试通过。
V0.32 仍是源码检查点，不创建 tag、二进制发布或 GitHub Release。

## 已验证证据

- V0.2 归档：16 个文件、212,510 字节，16 个迁移前 SHA-256 在 2026-08-10 全部匹配。
- 文档结构测试：24/24 通过，包含唯一活动计划断言。
- 完整 Release 测试：456/456 通过（原 455 项加一项文档结构测试），0 失败、0 跳过。
- 完整 Release 构建：0 警告、0 错误。
- 直接和传递依赖漏洞扫描：所有解决方案项目均未报告易受攻击包。
- 已在 `Rubbish/20260810_winpool_v032_staging/Program/WinPool` 完成新的 V0.32 四进程 staging；App、Agent、TestWorker 和 Broker 均报告产品版本 `V0.32`、文件版本 `0.3.2.0`。
- 人工 GUI、托盘、UAC、D: 工具和数据位置门仍为 `unverified`。
- 前向修正提交 `236eb3f` 已于 2026-08-10 推送到 `origin/main`，推送前确认此前远端 HEAD 是其祖先。
- 文档/资源补充：8 对英文权威文档与中文阅读副本全部通过链接和权威声明检查；`assets` 有 35 个受 Git 控制的文件，共 15,189,113 字节；`OriginArtWork` 已忽略；完整 Release 测试 458/458 通过、0 失败，构建 0 警告、0 错误。
- V0.32 复验：37 份 Markdown 相对链接全部通过，完整 Release 测试 458/458 通过、0 失败、0 跳过，Release 构建 0 警告、0 错误，直接和传递依赖均未报告已知漏洞。
- Git 完成证据：资源/文档提交 `dc5e263` 和 V0.32 检查点提交 `7b7a798` 已于 2026-08-10 通过正常快进推送到 `origin/main`。
