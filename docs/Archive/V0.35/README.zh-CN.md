# WinPool V0.35 验收记录

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

> 本文件仅为中文阅读副本；验收事实以无 `.zh-CN` 后缀的
> [README.md](README.md) 为准。

用户于 2026-08-12 明确确认 V0.35。[`Plan.md`](Plan.md) 保存权威执行计划和
证据；未修改的手工来源记录保留为 [`V0.35补充.txt`](V0.35补充.txt)。

507 项 Release 自动测试、零警告 Release 构建、依赖审计，以及
`D:\WinPool-V035-Candidate-Staging-Final-20260812` 的四进程 self-contained staging
均通过。App、Agent、TestWorker、Broker 均报告 V0.35；IPC 协议保持为 3，Agent 持有的
SQLite 合同保持为 schema 12。

M01--M04 与继承的原生/人工用例继续保持 `unverified`；用户确认不代表它们通过。该决定授权对应
的本地 checkpoint、`main` 推送和本机 portable 部署；不授权 tag、二进制上传、GitHub Release 或真实
存储结构修改。
