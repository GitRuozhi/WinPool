# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a third-party WinUI 3 desktop application for understanding, testing,
monitoring, and safely planning operations across Windows storage systems.

## Current version

V0.34 is the current user-confirmed version. It retains the multi-process
App/Agent/TestWorker/Broker architecture and closes the local identity,
storage-boundary, process-lifecycle, event-recovery, and tool-stream defects
identified after V0.33. IPC is protocol 3 and the Agent-owned SQLite contract
is schema 12.

The user accepted V0.34 on 2026-08-11 after 494 automatic Release tests, a
warning-free Release build, dependency audit, and reproducible four-process
staging passed. Native UI, tray, UAC, device, external-tool, data-location,
and V0.34 M01--M07 cases remain `unverified`; acceptance does not fabricate
those results. This confirmation authorizes the associated local checkpoint,
`main` push, and local portable deployment, but not a tag, binary upload, or
GitHub Release.

## Safety boundary

Real storage-structure mutation is not implemented or authorized. WinPool must
not create, initialize, format, resize, repair, or remove real disks, partitions,
volumes, Storage Pools, Storage Tiers, or Virtual Disks.

Simulation is the supported path for storage-structure editing. File tests are
limited to run-owned files in an explicitly registered directory. DiskSpd, fio,
Dite, RoboCopy, and RAMMap remain separately installed external tools.

## Build

WinPool requires Windows, PowerShell, the SDK pinned by `global.json`, and the
Windows App SDK dependencies restored by .NET.

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
```

The reproducible self-contained staging command is documented in
[Development](docs/Development.md).

## Documentation

- [Product](docs/Product.md): long-term purpose, boundaries, and roadmap.
- [Development](docs/Development.md): architecture, environment, build, staging,
  version, and documentation workflow.
- [Quality](docs/Quality.md): automatic, native, and human acceptance gates.
- [Changelog](docs/CHANGELOG.md): results that have actually occurred.
- [Archive](docs/Archive/README.md): frozen completed or superseded history,
  including the accepted V0.34 Plan.
- [Reference](docs/Reference/AI-Agent-Harness-项目管理架构参考.md):
  non-authoritative project-management reference.
- [Agent rules](AGENTS.md): operational, safety, authorization, and Git rules.

## Research background

Within the completed Windows 10 22H2 Storage Spaces tests, the current tested
recommendation is:

```text
64K interleave + 64K NTFS allocation unit size
```

Equivalent Windows 11 testing has not yet been completed.

## Rights

No license is granted by this repository. All rights are reserved.
