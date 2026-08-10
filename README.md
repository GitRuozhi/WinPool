# WinPool

[English](README.md) | [简体中文](README_CN.md)

WinPool is a third-party WinUI 3 desktop application for understanding, testing,
monitoring, and safely planning operations across Windows storage systems.

## Current checkpoint

V0.31 is the current V0.3 source integration checkpoint. It includes the
multi-process App/Agent/TestWorker/Broker architecture, Agent-owned SQLite,
typed named-pipe IPC, simulation editing, read-only local discovery, registered
file testing, monitoring, and reproducible four-process staging.

V0.31 is not a binary release or GitHub Release. V0.32 requires explicit user
confirmation after the native UI, tray, UAC, device, external-tool, lifecycle,
and data-location acceptance matrix is completed.

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
- [Current Plan](docs/Plan.md): the only active V0.3 stage and its evidence.
- [Changelog](docs/CHANGELOG.md): results that have actually occurred.
- [Archive](docs/Archive/README.md): frozen completed or superseded history.
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
