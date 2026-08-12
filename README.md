# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a third-party WinUI 3 desktop application for understanding, testing,
monitoring, and safely planning operations across Windows storage systems.

## Current version

V0.36 is the current local implementation version. It retains the multi-process
App/Agent/TestWorker/Broker architecture while closing schema-12 constraint
verification, connection disposal, watcher unsubscribe accounting, monotonic
worker persistence, historical Local identity, and malformed-connect handling.
IPC remains protocol 3 and the Agent-owned SQLite contract remains schema 12.

The V0.36 automatic Release, dependency-audit, and four-process staging results
are recorded in the changelog. Native UI, tray, UAC, device, external-tool, and
data-location cases remain `unverified`; no documentation treats them as passed.
The user authorized a local Git checkpoint only—not a push, tag, binary upload,
GitHub Release, or deployment.

## Safety boundary

The current V0.3 line does not implement or authorize real storage-structure
mutation. WinPool must not create, initialize, format, resize, repair, or remove
real disks, partitions, volumes, Storage Pools, Storage Tiers, or Virtual Disks
in this line.

V0.5 is the first planned phase that may add controlled real storage operations.
During development, the Agent must obtain the developer's approval immediately
before each exact operation. In the product, the user's explicit current-session
selection of the local real-mutation option authorizes controlled real
operations; elevation or Real mode alone is insufficient. Simulation remains the
default. File tests are
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
  including the implemented V0.36 Plan.
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
