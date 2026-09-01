# V0.44 共享运行时发行归档

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

> 本文件仅为中文阅读副本；归档说明以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

状态：已实施；自动门通过；目标合并目录进程冒烟通过；继承的操作系统矩阵与完整
人工 UI 用例未验证。产品版本仍为 V0.44。当前没有活动 Plan。

本目录冻结已确认的共享运行时 staging Plan，以及它之前的运行时对齐实验。

| 文件 | 角色 |
| --- | --- |
| [`Plan.md`](Plan.md) | 已实施并冻结的 Plan |
| [`V0.44 App - Agent runtime alignment experiment.md`](V0.44%20App%20-%20Agent%20runtime%20alignment%20experiment.md) | 对齐实验与结果 |

portable staging 是 App 与 Agent 两份独立 self-contained 发布经 SHA-256 检查的
并集。相同文件只存一份。冲突会使 staging 失败。本地 `artifacts\$(Configuration)\`
使用同一扁平根目录。
