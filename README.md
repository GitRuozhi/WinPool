# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a third-party WinUI 3 desktop application for understanding, testing,
monitoring, and safely planning operations across Windows storage systems.

The current product version is **V0.41**. The version source is
`Directory.Build.props`. Final results are in the [changelog](docs/CHANGELOG.md).

## Capabilities

WinPool presents storage topology, a focused operation workspace, simulation
editing, registered-directory file tests, and monitoring. Delivery is an
unpackaged, self-contained Windows x64 portable application.

Real storage-structure mutation is not enabled. File tests stay inside an
explicitly registered directory. DiskSpd, fio, Dite, RoboCopy, and RAMMap remain
separately installed external tools. Product limits are defined in
[Product](docs/Product.md).

## Build

WinPool requires Windows, PowerShell, the SDK pinned by `global.json`, and the
Windows App SDK dependencies restored by .NET.

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\build\Rebuild-WinPool.ps1
```

That command cleans regenerable local output, rebuilds, and writes a `WinPool.lnk` shortcut. The local run tree is `artifacts\$(Configuration)\`. Staging and process layout are documented in [Development](docs/Development.md).

## Documentation

- [Product](docs/Product.md): long-term purpose, boundaries, and roadmap.
- [Development](docs/Development.md): architecture, environment, build, staging,
  and version rules.
- [Quality](docs/Quality.md): test and acceptance rules.
- [Changelog](docs/CHANGELOG.md): important final results.
- [Archive](docs/Archive/README.md): frozen historical plans and state.
- [Agent rules](AGENTS.md): operational, safety, authorization, and Git rules.

Reference files under `docs/Reference` are not current requirements.

## Research background

Within the completed Windows 10 22H2 Storage Spaces tests, the current tested
recommendation is:

```text
64K interleave + 64K NTFS allocation unit size
```

Equivalent Windows 11 testing has not yet been completed.

## Rights

No license is granted by this repository. All rights are reserved.
