# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a third-party WinUI 3 desktop application for understanding, testing,
monitoring, and safely planning operations across Windows storage systems.

## Current version

V0.41 is the current product version and begins the visual-polish and basic-
interaction phase on the completed V0.3 foundation. IPC remains protocol 3 and
the Agent-owned SQLite contract is schema 13.

V0.41 is the current local implementation version. Its reproducible automatic
baseline is 549 Release tests passed, with no failed or skipped tests, a
warning-free Release build, and no known vulnerable packages. Native UI, tray,
UAC, device, external-tool, and data-location cases remain `unverified`; no
documentation treats them as passed. V0.39 remains the tagged and released V0.3
record.

Portable, unpackaged Windows x64 delivery is the only currently implemented
installation mode. The MSIX and Microsoft Store route is part of
[Product](docs/Product.md), not a separate installation document.

The [V0.41 Plan](docs/Archive/V0.41/Plan.md) is approved and implemented. It is not
released, deployed, committed, or pushed by that approval.

The V0.39 architecture-hardening pass ran before V0.4. It removed
confirmed dead code and separates concentrated Agent and page responsibilities
without adding product functions, changing IPC/schema contracts, or weakening
safety boundaries. It is now frozen in the
[V0.39 architecture-hardening archive](docs/Archive/V0.39-architecture-hardening/README.md).
That pass increased the current full automatic gate to 530 passed, 0 failed, and
0 skipped; its targeted native navigation result is recorded separately from
the still-`unverified` device and side-effect cases.

## Safety boundary

The current V0.4 line does not implement or authorize real storage-structure
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
- [V0.41 Plan](docs/Archive/V0.41/Plan.md): approved startup, welcome, monitoring, persistence,
  tray, and basic-interaction plan; implementation is complete.
- [V0.39 final-correction archive](docs/Archive/V0.39-final-correction/README.md):
  the implemented final minimal V0.3 correction and its evidence.
- [V0.39 architecture-hardening archive](docs/Archive/V0.39-architecture-hardening/README.md):
  the completed pre-V0.4 cleanup and boundary-hardening pass.
- [Changelog](docs/CHANGELOG.md): results that have actually occurred.
- [Archive](docs/Archive/README.md): frozen completed or superseded history,
  including the implemented V0.39 Plan.
- [Reference](docs/Reference/AI-Agent-Harness-项目管理架构参考.md):
  non-authoritative project-management reference.
- [V0.8–V0.9 technical-debt reference](docs/Reference/V0.8-V0.9-技术债务参考.md):
  deferred observations that are not current requirements.
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
