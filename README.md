# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a Windows desktop application for viewing storage topology, monitoring devices, editing simulated systems, and performing supported local storage operations.

The current product version is **V0.58**. The single-disk real-edit stage is complete: H01–H11, the final GPT/NTFS MAX layout, normal restart with real mode off, and the final engineering gate passed. H10 verified creating a tiered virtual disk from an HDD template; H11 verified ReFS content retention after expansion. Multi-disk redundancy, mixed-media tiering, existing-VD/tier MAX expansion, and other unverified paths remain disabled. See the [stage record](docs/Plan.md) and [current limitations](docs/CHANGELOG.md).

Startup first displays the last page and system from an integrity-checked, read-only local preview; the Agent then verifies them. If that preview is unavailable, a loading state prevents a flash of the default system. Manage and Hardware first display the last saved local inventory. On startup, the Agent collects storage first, then full hardware, and reports each result to the App. Both automatic collection and manual refresh show in-window progress, success or failure notifications; failed collection preserves the previous data. Loading history is not reported as a successful collection. Internal formats are document 3 / core SQLite 18 / monitoring SQLite 1 / IPC 12. The core database has a checked migration from schema 17; older inventory documents remain unsupported.

## What you can use

- View local storage through read-only discovery and inspect pools, tiers, disks, partitions, and related information.
- Enable Developer mode in Settings to show Hardware, Test, and Development. Hardware appears before Manage and provides a read-only ten-section report, source details, local refresh, and complete system JSON export.
- Edit simulated systems on the Storage structure and Disk/partition pages.
- Both editing pages provide entry points for preparing single-disk real operations, confirming an Agent-frozen plan, and querying its OperationId. Real editing starts turned off. The verified scope covers partition, pool, tiered virtual-disk, naming, and ReFS paths on the authorized single disk.
- Create simulated GPT partitions on a 1 MiB grid. The creation field uses whole MiB, defaults to the largest aligned capacity, and offers MAX; one action button switches between Create partition and Format partition according to the selection.
- Export storage systems as JSON. Quick format and Full format are mutually exclusive. Simulated targets do not write to real disks; real changes are limited to the verified single-disk paths and require the controlled plan-confirmation flow.
- Monitor supported devices with persistent session duration; problems use the shared notification cards and current-run message history.
- Record new monitoring samples in a separate database, rotating at 1 GiB and archiving with the bundled 7-Zip; Settings accepts an optional custom 7Z path.
- Use English or Simplified Chinese, themes, and keyboard navigation.

Current inventory documents store source facts; older inventory formats are rejected without migration or deletion.

Monitoring archives are not automatically deleted. The application does not browse historical monitoring data; CSV export covers only records available in the current active monitoring database. Rotation buffers samples in bounded memory while disk writes pause briefly; a crash or exhausted buffer can still leave a reported recording gap.

Simulation output is not proof that Windows can execute a configuration. See the [current limitations and changes](docs/CHANGELOG.md).

Normal simulated data partitions can be extended or shrunk to a 1 MiB-aligned total target capacity; system partitions remain protected from deletion and formatting. Real RAW and NTFS data partitions resize within the live provider range; NTFS changes can run online. Data partitions support 64 KiB NTFS/exFAT quick or full format. ReFS supports 64 KiB quick format and expansion only when the applicable Workstations/SKU and live provider probe pass; H11 verified content retention. ReFS shrink/full format, exFAT resizing, and system/startup-volume resizing remain disabled.

Test remains a roadmap notice. Development shows a Message List with single-line entries for the latest 200 messages from the current App run; double-click an entry to open centered, copyable details, then click outside to dismiss. Messages stay in memory and are cleared when the App exits; the application does not read fault log contents or maintain a complete operation history. The three areas can be resized by dragging their dividers. Full testing and development workspaces are planned for 2.0.

Transient messages use fixed-width, content-height bottom-right cards without merging separate repeated events. Cards move right as they disappear. Click a normal message to dismiss it or an error to open its message dialog. Both expire automatically, with errors staying longer. Detailed records are available on Development.

Detailed interface and simulation behavior is indexed in the [design tables](docs/DesignTables/README.md).

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
