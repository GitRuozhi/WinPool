# 2026-09-16 设计审查归档

两份原文原样保留，状态均为已被替代，不代表原计划全部实施或验收通过。

| 原文 | 归档原因与现行入口 |
| --- | --- |
| [硬件读取分析与重构方案](WinPool-Hardware-Inventory-Analysis-and-Refactor-Plan.md) | 原方案基于 V0.45；核心目标已由统一来源事实、分用途采集和十段硬件报告接替。旧报告模型、13 类/154 项契约及页面布局已失效。现行约定见 [Development](../../Development.md)，执行证据见 [V0.52](../V0.52/README.md)及[硬件报告归档](../20260915-hardware-report/README.md) |
| [多版本发行方案](WinPool-Multi-Edition-Plan-Simplified.md) | 用户决定只提供功能一致的便携版与安装版，由内置开发者模式控制相关页面，不再做 Standard/Preview 功能分版。现行方案见[便携版与安装版交付方案](../../Design/WinPool-Distribution-Plan.md) |

硬件旧稿中的先测量后优化、按实测选择原生采集路径仍可作为历史参考，不形成剩余实施任务。原文中的状态、命令式措辞与兼容承诺均不能覆盖当前 Product/Development。

原文件位于 docs/Design；其中指向 README.md 的设计索引链接按[现行 Design 索引](../../Design/README.md)追溯，其他旧相对链接按原位置解释。
