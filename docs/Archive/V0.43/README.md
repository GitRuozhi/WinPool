# V0.43

Implemented 2026-09-01.

This is the frozen V0.43 product-slimming plan. The full Release gate passed 350
tests with 0 failed and 0 skipped, a warning-free Release build, no known
vulnerable packages, and a verified two-process staging tree
(`WinPool.App.exe` plus `Agent\WinPool.Agent.exe`, with the expected
Windows App SDK and .NET self-contained runtime components). IPC protocol 4 and
SQLite schema 14 were confirmed on a fresh data root: the local database was
reset through `build/Reset-WinPoolLocalData.ps1`, the App was launched from the
staged tree, the welcome page and management page rendered with real local
storage inventory, and `schema_info` recorded version 14 with no retired tables
present.

Native welcome, management, and inventory rendering were verified. Inherited
device, UAC, DPI, and long-duration cases remain `unverified`. No push, tag,
Release, binary upload, or deployment was created.
