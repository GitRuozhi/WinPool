# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a native Windows desktop application for inspecting Windows Storage Spaces and the storage topology around them. `V0.1` is a public, read-only test preview built with C#, WinUI 3, .NET 9, and the Windows App SDK.

## What it does

- Scans Windows storage inventory through a fixed read-only PowerShell script.
- Correlates storage subsystems, pools, tiers, physical disks, virtual disks, partitions, and mapped network disks.
- Presents object details in an upper operation workspace and the complete nested topology in a lower logic workspace.
- Includes a fixed simulation system so the interface remains useful on computers without a complex Storage Spaces configuration.
- Supports Chinese and English, light/dark/system themes, Windows or preset accent colors, and single-instance activation.
- Masks disk serial numbers before data reaches the interface, clipboard, or default exports.

The Manage and Settings tabs are implemented. Create, Test, Monitor, and Development are placeholders for later milestones.

## Safety boundary

`V0.1` does not contain storage-pool creation, disk initialization, formatting, removal, resize, repair, or any other mutating storage operation.

Every normal launch starts in Simulation. The Real switch currently demonstrates privilege handling and interface state only; it does not enable storage modification. The inventory provider invokes only the repository's fixed read-only script and does not accept user-supplied PowerShell parameters.

## Requirements

- Windows 10 version 1809 or later, or Windows 11
- x64 processor and operating system
- Administrator rights are optional and are not required for normal read-only use

The portable test build is self-contained and does not require a separate .NET or Windows App SDK installation.

## Portable test build

Download `WinPool_V0.1_Test_x64.7z` from [GitHub Releases](https://github.com/GitRuozhi/WinPool/releases), extract the `WinPool` folder, and run `WinPool.App.exe`.

Settings are stored in:

```text
%LOCALAPPDATA%\WinPool\settings.json
```

WinPool does not include an installer or an in-app updater. The Settings page opens the GitHub Releases page in the system browser.

## Build from source

The repository uses an unpackaged, self-contained x64 deployment model.

```powershell
dotnet test WinPool.slnx -c Release
dotnet build src\WinPool.App\WinPool.App.csproj -c Release -p:Platform=x64
dotnet publish src\WinPool.App\WinPool.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true
```

The pinned SDK is declared in `global.json`. Visual Studio with the Windows App SDK C# workload is recommended for XAML development.

## Repository layout

```text
Docs/                              Current product and engineering documents
src/WinPool.App/                   WinUI shell, pages, controls, and view models
src/WinPool.Core/                  Domain models, topology, selection, and layout rules
src/WinPool.Infrastructure.Windows/ Read-only Windows inventory and local services
tests/                             Core and Windows infrastructure tests
```

## Known limitations

- This is a test preview, not a production storage-management tool.
- Only x64 portable publishing is supported in this release.
- Network-drive discovery reflects mappings visible to the current Windows user session.
- Hardware and Storage Spaces association quality depends on information exposed by Windows.
- Real storage operations, Dite integration, reporting, and monitoring are not implemented.

## Research background

WinPool grows out of an evidence-backed Storage Spaces research project. The current tested recommendation is 64K interleave with a 64K NTFS allocation unit.

Read the [WinPool Tiered Storage research and V10 guide](https://github.com/GitRuozhi/WinPool-Tiered-Storage).

## Feedback

Use [GitHub Issues](https://github.com/GitRuozhi/WinPool/issues) for reproducible bugs and interface feedback. Do not post unmasked disk serial numbers or private diagnostic material.

## Rights

No license is granted for this repository. All rights are reserved.
