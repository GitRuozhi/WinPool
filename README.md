# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a Windows desktop application for viewing storage topology, monitoring devices, editing simulated systems, and performing supported local storage operations.

The current product version remains **V0.58, iteration 8**. H01–H11, the closing layout and engineering results of the earlier single-disk real-edit stage are recorded in the [historical archive](docs/Archive/20261007-real-edit-stage1/Plan.md). The current stage connects real operations to the existing editors. Native tests have passed ordinary pool creation, the first ordinary virtual disk, ordinary MAX automatic layout, and single-HDD tiered 32 GiB creation and rebuild. Single-HDD tiered MAX was still rejected by Windows for insufficient resources / unsupported configuration despite satisfying the joint pool and template provider ranges. No reliable read-only interface currently determines its actual executable maximum; the requested acceptance exception remains the outstanding decision, so the stage is not fully accepted. The authorized physical disk has been restored and independently checked as online GPT with a canonical 16 MiB MSR and one maximum whole-MiB NTFS/64 KiB data partition, labelled `WinPool_Test` at `E:`. Final reconciliation, restart and normal-exit checks passed. The Samsung comparison found no changes within its recorded storage-structure scope and no monitoring gaps; four shared Primordial adjustments are recorded separately. See the [active plan](docs/Plan.md). Multi-disk redundancy, mixed-media tiering, real MBR-to-GPT conversion and existing-VD/tier MAX expansion remain disabled.

Six native renames passed with actual 20 Hz monitoring samples from all three disks. For three matched rename samples on this machine, the median time from confirmation to the refreshed view decreased from 18.472 s to 15.437 s (16.43%); this excludes preparation and human waiting and is not a general performance guarantee. The final build was not retimed after a change limited to alert presentation; sampling and execution paths were unchanged. Baseline device-mapping and UI-readiness limits are recorded in [Development](docs/Development.md).

The latest Release engineering gate passed 1,772 tests with zero failures and three not executed across 12 projects; restore and build completed without warnings or errors. This does not resolve the tiered MAX acceptance decision.

Startup first displays the last page and system from an integrity-checked, read-only local preview; the Agent then verifies them. If that preview is unavailable, a loading state prevents a flash of the default system. Manage and Hardware first display the last saved local inventory. On startup, the Agent collects storage first, then full hardware, and reports each result to the App. Both automatic collection and manual refresh show in-window progress, success or failure notifications; failed collection preserves the previous data. Loading history is not reported as a successful collection. Internal formats are document 3 / core SQLite 18 / monitoring SQLite 2 / IPC 13. The core database has a checked migration from schema 17; older inventory documents remain unsupported.

## What you can use

- View local storage through read-only discovery and inspect pools, tiers, disks, partitions, and related information.
- Enable Developer mode in Settings to show Hardware, Test, and Development. Hardware appears before Manage and provides a read-only ten-section report, source details, local refresh, and complete system JSON export.
- Edit simulated systems on the Storage structure and Disk/partition pages.
- Both editing pages retain controlled real-operation entry points for the authorized single disk, with Agent-frozen plan confirmation and OperationId queries; real editing starts turned off. Storage structure uses the original form, topology draft and net-difference Apply; Disk/partition submits each action separately. Existing names are renamed independently with Enter.
- Storage structure defaults to ordinary layout / `MAX`; single-HDD tiering and explicit GiB are also available. Ordinary creation reads the exact pool provider range. Single-HDD tiered creation reads both the exact pool and unused HDD-template ranges: `MAX` takes the largest exact byte value in their intersection, and explicit bytes must satisfy both. These queries do not guarantee Windows creation success; single-HDD tiered MAX retains the native limitation above. Auto-create virtual disk (AutoVD) and auto-create partition (AutoPart) are independent saved preferences; changing either preference does not alter existing objects or drafts. A rebuild must be explicitly drafted as removal and recreation, with each destructive step shown in its confirmed plan. Single-HDD tiered 32 GiB creation, automatic layout and rebuild passed through the original UI; final stage acceptance remains open.
- On Disk/partition, the status control switches online/offline only when the exact source `IsOffline` fact is available. RAW can be initialized as GPT; GPT is shown as already initialized, and MBR-to-GPT conversion is available only in simulation. The separate Clear to RAW action clears simulated GPT/MBR disks under the existing simulation rules; real clear currently supports only online GPT disks with ordinary data/MSR partitions, or GPT disks whose complete current source facts prove they have zero partitions. Real MBR clear is disabled. Clearing does not zero, initialize, format, or create a pool. During automatic GPT layout, the saved MSR preference is carried forward: with AutoPart enabled, MSR-on places one 16 MiB MSR at 1 MiB and BasicData begins after it at 17 MiB; MSR-off omits it and starts BasicData at 1 MiB. Standalone initialization does not add BasicData, create a pool, or format a volume.
- Create simulated GPT partitions on a 1 MiB grid. The creation field uses whole MiB, defaults to the largest aligned capacity, and offers MAX; one action button switches between Create partition and Format partition according to the selection.
- Export storage systems as JSON. Quick format and Full format are mutually exclusive. Simulated targets do not write to real disks; real changes are limited to the supported single-disk paths and require the controlled plan-confirmation flow. Final stage acceptance remains open pending the tiered MAX decision.
- Monitor supported devices with persistent session duration; problems use the shared notification cards and current-run message history.
- Record new monitoring samples in a separate database, rotating at 1 GiB and archiving with the bundled 7-Zip; Settings accepts an optional custom 7Z path.
- Use English or Simplified Chinese, themes, and keyboard navigation.

Current inventory documents store source facts; older inventory formats are rejected without migration or deletion.

Monitoring archives are not automatically deleted. The application does not browse historical monitoring data; CSV export covers only records available in the current active monitoring database. Rotation buffers samples in bounded memory while disk writes pause briefly; a crash or exhausted buffer can still leave a reported recording gap.

After restart, a retained historical gap does not prevent sampling when fresh facts uniquely bind the supported device; recovery requires actual samples. Unknown operation outcomes remain blocked. With monitoring off, a historical pending gap with an unknown endpoint is retained in diagnostics and does not raise a current monitoring warning.

Simulation output is not proof that Windows can execute a configuration. See the [current limitations and changes](docs/CHANGELOG.md).

A reported provider error initially keeps the unknown-outcome barrier. Only strict read-only reconciliation proving no new object was created can close the operation as failed; that is not creation success. A changed target needs a newly confirmed exact plan. The software does not silently reduce MAX, probe capacity through trial writes, or replay an uncertain call.

Normal simulated data partitions can be extended or shrunk to a 1 MiB-aligned total target capacity; system partitions remain protected from deletion and formatting. Real RAW and NTFS data partitions resize within the live provider range; NTFS changes can run online. The dialog shows extension as `A + B = C` and shrink as `A − B = C`: current size, change, and target total size. The exact bytes target is authoritative, and the Agent rechecks the live range before preparing the operation. Real data-partition formatting defaults to 64 KiB NTFS quick format; other NTFS/exFAT and format-mode combinations are available only when the current provider capability and exact plan allow them. ReFS supports 64 KiB quick format and expansion only when the applicable Workstations/SKU and live provider probe pass; H11 verified content retention. ReFS shrink/full format, exFAT resizing, and system/startup-volume resizing remain disabled.

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
