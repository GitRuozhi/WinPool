# WinPool V0.34 Acceptance Record

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

The user explicitly accepted V0.34 on 2026-08-11. [`Plan.md`](Plan.md)
preserves the authoritative execution plan and evidence; the unchanged manual
source record is retained as [`V0.34修BUG.md`](V0.34修BUG.md).

All 494 Release tests, the warning-free Release build, dependency audit, and
four-process self-contained staging passed. App, Agent, TestWorker, and Broker
all report V0.34; IPC is protocol 3 and the Agent-owned SQLite contract is
schema 12.

M01--M07 and inherited native/manual cases remain `unverified`; user acceptance
does not claim that they passed. The decision authorizes the associated local
checkpoint, `main` push, and local portable deployment. It does not authorize a
tag, binary upload, GitHub Release, or real storage-structure mutation.
