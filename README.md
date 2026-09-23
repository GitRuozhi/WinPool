# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a Windows desktop application for viewing storage topology, monitoring devices, and editing simulated storage systems.

The current implementation is **V0.55**. Real storage-structure changes are not enabled.

Manage and Hardware first display the last saved local inventory. On startup, the Agent collects storage first, then full hardware, and reports each result to the App. Both automatic collection and manual refresh show in-window progress, success or failure notifications; failed collection preserves the previous data. Loading history is not reported as a successful collection. Internal formats are document 3 / core SQLite 17 / monitoring SQLite 1 / IPC 11.

## What you can use

- View local storage through read-only discovery and inspect pools, tiers, disks, partitions, and related information.
- Enable Developer mode in Settings to show Hardware, Test, and Development. Hardware appears before Manage and provides a read-only ten-section report, source details, local refresh, and complete system JSON export.
- Edit simulated systems on the Storage structure and Disk/partition pages.
- Export storage systems as JSON. On the Disk/partition page, Quick format and Full format are mutually exclusive simulation modes; neither writes to a real disk.
- Monitor supported devices with persistent session duration; problems use the shared notification cards and current-run message history.
- Record new monitoring samples in a separate database, rotating at 1 GiB and archiving with the bundled 7-Zip; Settings accepts an optional custom 7Z path.
- Use English or Simplified Chinese, themes, and keyboard navigation.

Current inventory documents store source facts; older inventory formats are rejected without migration or deletion.

Monitoring archives are not automatically deleted. The application does not browse historical monitoring data; CSV export covers only records available in the current active monitoring database. Rotation buffers samples in bounded memory while disk writes pause briefly; a crash or exhausted buffer can still leave a reported recording gap.

Simulation output is not proof that Windows can execute a configuration. See the [current limitations and changes](docs/CHANGELOG.md).

Normal simulated data partitions can be extended or shrunk to a 1 MiB-aligned total target capacity; system partitions remain protected from deletion and formatting. Extend supports NTFS/ReFS/RAW; shrink supports NTFS/RAW. The restored implementation and automated regression checks have passed; native resize verification remains deferred.

Test remains a roadmap notice. Development has a Logs area for the latest 200 messages from the current App run; double-click an entry to view and copy its details. It also shows a copyable diagnostics directory path. Messages stay in memory and are cleared when the App exits; the application does not read log contents or maintain a complete operation history. The other areas are reserved for future work, including an AI entry. Full testing and development workspaces are planned for 2.0.

Transient messages use fixed-size bottom-right cards without merging separate repeated events. Cards move right as they disappear. Click a normal message to dismiss it or an error to open its message dialog. Both expire automatically, with errors staying longer. Detailed records are available on Development.

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

No license is granted for WinPool's own code by this repository. All rights are reserved. Third-party components retain their respective licenses; the bundled 7-Zip component's [license](assets/ThirdParty/7zip/26.03/License.txt) and [source notice](assets/ThirdParty/7zip/26.03/NOTICE.txt) are provided separately.
