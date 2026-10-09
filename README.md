# WinPool

[English](README.md) | [简体中文](README.zh-CN.md)

WinPool is a Windows desktop application for viewing storage topology, monitoring devices, editing simulated systems, and performing supported local storage operations.

The current product version remains **V0.58, iteration 8**; the [active plan](docs/Plan.md) is not complete or archived. Earlier H01–H11 results remain in the [historical archive](docs/Archive/20261007-real-edit-stage1/Plan.md).

`WINPOOL-MAX-GIB-1` computes `C = floor((A - 4,000,000 - 1) / 1,073,741,824)`. For real operations A is the smaller of the fresh provider maximum and physical-capacity budget; for simulation it is layout-adjusted logical headroom after known allocation. C is a starting candidate, not a promised physical maximum: simulation adopts each tier's C directly, while real MAX tests capacity in 1 GiB steps without passing Windows `UseMaximumSize`. Real tiered creation remains limited to the controlled typed backend for Simple/Fixed, one-column, 64 KiB tiers on distinct media. Each tier starts from an exact half-C seed and is searched separately to a whole-GiB result; the seed is only an initial structure size, and the final VD size is the sum of tier results. There is no multi-disk UI or real multi-tier MAX device result. The previous native `UseMaximumSize` attempts are historical; details are in [Quality](docs/Quality.md).

On the tested WDC/provider, the ordinary search started at 3,724 GiB, succeeded at 3,724 and 3,725 GiB, then received structured error 40000 at 3,726 GiB; the observed boundary is **3,725 GiB / 3,999,688,294,400 bytes**. The single-HDD tiered search received provider code 1 at 3,725 GiB and succeeded at **3,724 GiB / 3,998,614,552,576 bytes**, with the actual HDD tier, VD and OS disk matching and the automatic layout completed. The assertions passed 18/18 and 17/17 respectively: [ordinary](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Ordinary-Retry/native-assertions.json) and [HDD tiered](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Hdd/native-assertions.json). Independent reviews passed 22/22 ordinary checks and 23/23 HDD checks within their recorded scopes: [ordinary review](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/ordinary-native-independent-review/review.json) and [HDD review](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/hdd-native-independent-review/review.json). These are observed results for this disk and provider, not a capacity guarantee for other systems.

After the MAX runs, the WDC was restored to online GPT with a canonical 16 MiB MSR and `WinPool_Test` on `E:` as NTFS/64 KiB. The [final native assertions](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/final-native-verification.json) passed 11/11; the [independent final review](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/final-native-independent-review.json) passed 14/14 checks within its scope, and Samsung’s scoped comparison found 0 changes / 0 evidence gaps. The [operation audit](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/final-operation-verification-v2.json) passed 364 checks. A normal restart kept Real Off / Monitor Off at 20 Hz, matched the B0 preference baseline and preserved all 412 source-file hashes; there were no unfinished operations. The original tray Exit closed App and Agent with code 0. See the [restart audit](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/after-final-restart-readonly-audit.json), [restart UI](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Final/normal-restart-ui.txt) and [exit result](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Final/final-normal-restart-exit-result.json). Multi-disk redundancy UI, unrestricted mixed-media layout creation, real MBR-to-GPT conversion and expansion of an existing VD/tier to MAX remain unavailable.

Six native renames passed with actual 20 Hz monitoring samples from all three disks. For three matched rename samples on this machine, the median time from confirmation to the refreshed view decreased from 18.472 s to 15.437 s (16.43%); this excludes preparation and human waiting and is not a general performance guarantee. The later 2026-10-08 alert-presentation-only build was not retimed; that change left sampling and execution paths unchanged. These measurements predate the native MAX and scoped-membership corrections and have not been repeated on their build. Baseline device-mapping and UI-readiness limits are recorded in [Development](docs/Development.md).

The frozen current-source [full gate](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/20261009-203701/summary.json) covered 12 projects: **1,999 total / 1,996 passed / 0 failed / 3 not executed**; source hashes were unchanged, restore and Release build succeeded with 0 warnings and 0 errors, and the prior 23-project dependency audit was reused. The earlier 20:30 gate is retained in [its summary](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/20261009-203059/summary.json); three recovery failures were fixed and the [multi-recovery footprint regressions](artifacts/test-results/20261009-integer-gib-max-1af833540a184658a06799c8c24ffd3c/Engineering/multi-recovery-footprint-final/results.trx) passed 12/12. The three NotExecuted cases are existing archive/performance measurements, not passing tests.

The earlier [single-HDD capacity investigation](docs/Review/Single-Hdd-Tier-Capacity-20261009.md) verified an actual HDD tier at an explicit 3,997,809,246,208 bytes (3723.25 GiB) through independent pwsh and the original UI; that explicit-size result is not MAX. Its earlier Release gate predates `WINPOOL-MAX-GIB-1` and remains historical.

Startup first displays the last page and system from an integrity-checked, read-only local preview; the Agent then verifies them. If that preview is unavailable, a loading state prevents a flash of the default system. Manage and Hardware first display the last saved local inventory. On startup, the Agent collects storage first, then full hardware, and reports each result to the App. Both automatic collection and manual refresh show in-window progress, success or failure notifications; failed collection preserves the previous data. Loading history is not reported as a successful collection. Internal formats are document 3 / core SQLite 19 / monitoring SQLite 2 / IPC 13. The core database has checked migrations from schemas 17 and 18; older inventory documents remain unsupported.

## What you can use

- View local storage through read-only discovery and inspect pools, tiers, disks, partitions, and related information.
- Enable Developer mode in Settings to show Hardware, Test, and Development. Hardware appears before Manage and provides a read-only ten-section report, source details, local refresh, and complete system JSON export.
- Edit simulated systems on the Storage structure and Disk/partition pages.
- Both editing pages retain controlled real-operation entry points for the authorized single disk, with Agent-frozen plan confirmation and OperationId queries; real editing starts turned off. Storage structure uses the original form, topology draft and net-difference Apply; Disk/partition submits each action separately. Existing names are renamed independently with Enter.
- Storage structure defaults to ordinary layout / `MAX`; single-HDD tiering with explicit GiB is also available. MAX uses `WINPOOL-MAX-GIB-1`: with `G = 1,073,741,824` and `R = 4,000,000`, it calculates `C = floor((A - R - 1) / G)`. Simulation adopts each tier's C directly; real creation searches in 1 GiB steps without sending native `UseMaximumSize`. On the tested WDC/provider, ordinary MAX reached 3,725 GiB; single-HDD tiered MAX reached 3,724 GiB with an actual HDD tier. These are observed results for that system, not general capacity guarantees. Real tiered creation in the controlled backend is limited to supported Simple/Fixed, one-column, 64 KiB distinct-media tiers; each tier starts from an exact half-C seed and is searched separately to a whole-GiB result. The seed is not the final MAX. There is no multi-disk UI or real multi-tier MAX result. Explicit ordinary GiB uses the exact pool provider range; explicit single-HDD tier sizes use the exact unused HDD-template range without imposing the ordinary VD pool grid. Fresh identity, layout and actual capacity must be verified before subsequent automatic partition layout; a tiered target additionally requires the unique actual tier. Read queries do not reserve capacity or guarantee creation success. AutoVD and AutoPart are independent saved preferences; changing either does not alter existing objects or drafts. A rebuild must be explicitly drafted as removal and recreation, with destructive steps shown in the confirmed plan.
- On Disk/partition, the status control switches online/offline only when the exact source `IsOffline` fact is available. RAW can be initialized as GPT; GPT is shown as already initialized, and MBR-to-GPT conversion is available only in simulation. The separate Clear to RAW action clears simulated GPT/MBR disks under the existing simulation rules; real clear currently supports only online GPT disks with ordinary data/MSR partitions, or GPT disks whose complete current source facts prove they have zero partitions. Real MBR clear is disabled. Clearing does not zero, initialize, format, or create a pool. During automatic GPT layout, the saved MSR preference is carried forward: with AutoPart enabled, MSR-on places one 16 MiB MSR at 1 MiB and BasicData begins after it at 17 MiB; MSR-off omits it and starts BasicData at 1 MiB. Standalone initialization does not add BasicData, create a pool, or format a volume.
- Create simulated GPT partitions on a 1 MiB grid. The creation field uses whole MiB, defaults to the largest aligned capacity, and offers MAX; one action button switches between Create partition and Format partition according to the selection.
- Export storage systems as JSON. Quick format and Full format are mutually exclusive. Simulated targets do not write to real disks; real changes are limited to the supported single-disk paths and require the controlled plan-confirmation flow. The V0.59 stage remains open until its active Plan is closed; current MAX results are limited to the recorded WDC/provider and supported layouts.
- Monitor supported devices with persistent session duration; problems use the shared notification cards and current-run message history.
- Record new monitoring samples in a separate database, rotating at 1 GiB and archiving with the bundled 7-Zip; Settings accepts an optional custom 7Z path.
- Use English or Simplified Chinese, themes, and keyboard navigation.

Current inventory documents store source facts; older inventory formats are rejected without migration or deletion.

Monitoring archives are not automatically deleted. The application does not browse historical monitoring data; CSV export covers only records available in the current active monitoring database. Rotation buffers samples in bounded memory while disk writes pause briefly; a crash or exhausted buffer can still leave a reported recording gap.

After restart, a retained historical gap does not prevent sampling when fresh facts uniquely bind the supported device; recovery requires actual samples. Unknown operation outcomes remain blocked. With monitoring off, a historical pending gap with an unknown endpoint is retained in diagnostics and does not raise a current monitoring warning.

Simulation output is not proof that Windows can execute a configuration. See the [current limitations and changes](docs/CHANGELOG.md).

A provider error or an unverified postcondition retains the unknown-outcome barrier. Strict read-only reconciliation may close a proven no-effect result as Failed / ObservedNoEffect, or an accurately identified mismatched residual as Failed / ObservedUnexpectedEffect. The latter retains the residual for a separately confirmed cleanup; neither is successful creation. The current MAX workflow explicitly searches capacity candidates in 1 GiB steps; it does not change layout or replay an uncertain call.

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
