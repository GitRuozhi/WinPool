# WinPool V0.35 Acceptance Record

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

The user explicitly accepted V0.35 on 2026-08-12. [`Plan.md`](Plan.md)
preserves the authoritative execution plan and evidence; the unchanged manual
source record is retained as [`V0.35补充.txt`](V0.35补充.txt).

All 507 Release tests, the warning-free Release build, dependency audit, and
four-process self-contained staging at
`D:\WinPool-V035-Candidate-Staging-Final-20260812` passed. App, Agent,
TestWorker, and Broker all report V0.35; IPC remains protocol 3 and the
Agent-owned SQLite contract remains schema 12.

M01--M04 and inherited native/manual cases remain `unverified`; user acceptance
does not claim that they passed. The decision authorizes the associated local
checkpoint, `main` push, and local portable deployment. It does not authorize a
tag, binary upload, GitHub Release, or real storage-structure mutation.
