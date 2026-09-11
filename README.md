# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a Windows desktop application for viewing storage topology, monitoring devices, and editing simulated storage systems.

The current implementation is **V0.50**. Real storage-structure changes are not enabled.

## What you can use

- View local storage through read-only discovery and inspect pools, tiers, disks, partitions, and related information.
- Edit simulated systems on the Storage structure and Disk/partition pages.
- Monitor supported devices and configure application and background preferences.
- Use English or Simplified Chinese, themes, and keyboard navigation.

Simulation output is not proof that Windows can execute a configuration. See the [current limitations and changes](docs/CHANGELOG.md).

The Test and Development tabs are roadmap notices throughout 1.x. Their full workspaces are planned for 2.0.

## Requirements and running

Minimum supported: **Windows 10 22H2 x64**. Primary platforms: Windows 11 24H2 and 25H2 x64. Available storage features depend on the Windows edition and storage provider.

Delivery is currently an unpackaged, self-contained x64 portable directory. Keep the entire directory together and run `WinPool.App.exe`; the companion Agent runs in the user tray. Data defaults to `%LocalAppData%\WinPool`, with an explicitly selected writable `Data` directory beside the program also supported. Exit WinPool before replacing program files.

There is no released MSIX package or Microsoft Store listing.

## Building from source

On Windows with PowerShell and the SDK pinned by `global.json`:

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

Check that an existing WinPool process is not using the output directory before rebuilding. Contributor instructions start at [AGENTS](AGENTS.md); internal development documentation is maintained in Chinese. The [development guide](docs/Development.md) describes build and data ownership.

## Research background

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
Windows 11 has not yet received equivalent testing because current storage hardware prices and the author's practical budget do not allow a second full test platform.
```

These research results do not establish support or reliability for every Windows configuration.

## Rights

No license is granted by this repository. All rights are reserved.
