# V0.44

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

Implemented 2026-09-01.

This is the frozen V0.44 platform-upgrade and distribution-slimming plan.
Windows App SDK is 2.4.0. Windows-targeted projects share TFM
`net10.0-windows10.0.26100.0`; `net10.0-windows10.0.28000.0` was rejected by
the pinned .NET SDK (NETSDK1140). BuildTools is `10.0.28000.2705`. Unused
Windows App SDK AI/ML/Search/Widgets assets are excluded from publish.
The nested `Agent\WinPool.Agent.exe` layout is retained because five same-name
desktop assemblies differ between App and Agent. Formal staging contains no
PDB.

Release portable staging measured 779 files and 338.40 MiB, down from the
V0.43 baseline of 853 files and 380.44 MiB. The full Release gate passed 352
tests with 0 failed and 0 skipped, a warning-free Release build, and no known
vulnerable packages. App and Agent launched from the staged tree.

Inherited device, UAC, DPI, Win10 22H2, Win11 24H2/25H2, and long-duration
cases remain `unverified`. No push, tag, Release, binary upload, or deployment
was created.

Later annotation, 2026-09-01: the temporary App/Agent runtime-collision analysis
note was moved here from `docs/`. Source commit `23ed240`. Product version
remains V0.44. The note is historical input and is not a current requirement.
