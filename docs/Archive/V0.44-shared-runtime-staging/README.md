# V0.44 Shared Runtime Staging Archive

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

Status: implemented; automatic gates passed; targeted merged-directory process
smoke passed; inherited OS-matrix and full human UI cases unverified. Product
version remains V0.44. There is no active Plan.

This directory freezes the confirmed shared-runtime staging Plan and the
runtime-alignment experiment that preceded it.

| File | Role |
| --- | --- |
| [`Plan.md`](Plan.md) | Frozen implemented Plan |
| [`V0.44 App - Agent runtime alignment experiment.md`](V0.44%20App%20-%20Agent%20runtime%20alignment%20experiment.md) | Alignment experiment and result |

Portable staging is the SHA-256-checked union of independent App and Agent
self-contained publishes. Shared identical files are stored once. Collisions
fail staging. Local `artifacts\$(Configuration)\` uses the same flat root.
