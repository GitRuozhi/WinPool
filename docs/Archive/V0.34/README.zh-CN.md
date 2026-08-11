# WinPool V0.34 验收记录

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

> 本文件仅为中文阅读副本；归档状态以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

用户于 2026-08-11 明确确认 V0.34。[`Plan.md`](Plan.md) 保存权威执行计划和
证据；未修改的手工来源记录保留为 [`V0.34修BUG.md`](V0.34修BUG.md)。

全部 494 项 Release 测试、无警告 Release 构建、依赖审计和四进程自包含 staging
均通过；App、Agent、TestWorker、Broker 均报告 V0.34；IPC 协议为 3，Agent 持有的
SQLite 合同为 schema 12。

M01--M07 与继承的原生/人工用例继续保持 `unverified`；用户确认不代表它们通过。
该决定授权对应的本地 checkpoint、`main` 推送和本机 portable 部署；不授权 tag、二进制上传、
GitHub Release 或真实存储结构修改。
