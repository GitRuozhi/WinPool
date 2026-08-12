# WinPool 文档归档

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

> 本文件仅为中文阅读副本；归档索引和状态以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

归档内容记录历史状态，不定义当前要求。当前没有活动 Plan；V0.36 是最新实施记录。

| 条目 | 状态 | 日期 | 版本/提交 | 内容 |
| --- | --- | --- | --- | --- |
| [`V0.2/`](V0.2/) | archived | 2026-08-10 | V0.2；源码基线 `ec8b34a` | 15 份架构、实现、验收和人工测试计划及其冻结索引 |
| [`V0.31-pre-correction/`](V0.31-pre-correction/) | superseded / invalid | 2026-08-10 | `6cf68e3`、`8d7fb25` | 错误覆盖用户 `docs/Archive` 决定的 Plan 和 Plan 索引 |
| [`V0.32/`](V0.32/) | accepted；人工用例未验证 | 2026-08-10 | V0.32；`dc5e263`、`7b7a798` | V0.31 修正最终状态及用户的 V0.32 版本决定 |
| [`V0.33/`](V0.33/) | accepted；原生/人工用例未验证；已推送 | 2026-08-11 | V0.33；实现提交 `6b66c68`…`0dcd22a`；版本 `38ff043`；验收 `e148b61` | 架构收口、生命周期硬化、精确迁移、验证证据和用户确认 |
| [`V0.34/`](V0.34/) | accepted；原生/人工用例未验证 | 2026-08-11 | V0.34；实现提交 `f9a9869`…`aee9eb6`；版本 `b18f119` | 缺陷收口、严格进程身份、schema-12 clean break、事件 reseed/backpressure 与已接受的执行记录 |
| [`V0.35/`](V0.35/) | accepted；原生/人工用例未验证 | 2026-08-12 | V0.35；实现提交 `ab83458`…`5338603`；candidate `a2ab8ae`；验收 checkpoint | Local identity 权威化、watcher 隔离、终态持久化、有界关闭、schema 验证和进程 incarnation 收口 |
| [`V0.36/`](V0.36/) | 已实施；自动门通过；原生/人工用例未验证 | 2026-08-12 | V0.36；本地 Git checkpoint | schema 约束、连接释放、watcher 计数、worker 单调持久化、历史 Local identity 与协议异常收口 |
| [`V0.33重构.md`](V0.33重构.md) 与 [`V0.33重构补充.md`](V0.33重构补充.md) | archived source records | 2026-08-11 | V0.33 | 从 `docs/` 移入后保持未改动的 V0.33 重构与补充原始记录 |

前向修正由已经存在于 `origin/main` 的提交 `236eb3f` 记录。它保留上述两个已替代提交作为审计历史。

除修复失效链接或明确事实纠正外，归档文件保持只读。任何纠正必须标记为后加注释；不得编造历史验收结果。
