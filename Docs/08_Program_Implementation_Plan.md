# WinPool Program Implementation Plan, Edition 3 / 程序编制计划（第三版）

Status: **Planning baseline; the implemented `V0.1` scope is read-only**

## 1. Goals and current boundary

The future application remains under `Program\WinPool` and targets an unpackaged C# WinUI 3 desktop application.

This edition freezes:

- one window without outer navigation;
- an upper operation area and lower logic area;
- a title-bar Simulation/Real selector with an administrator gate;
- an administrator suffix in the application title instead of a separate workspace status;
- a full-workspace Settings page;
- System/Light/Dark and Chinese/English settings;
- the complete block-enclosure storage topology without relationship lines;
- the text content and first-milestone commands for every horizontal category;
- typed, reviewable storage-pool creation.

The implemented `V0.1` scaffold contains the WinUI shell, XAML, read-only inventory code, tests, and portable x64 publication workflow. Historical implementation diaries are archived in the parent project's root `Old` directory. Real disk operations remain forbidden.

## 2. Future solution layout

```text
Program\WinPool\
├─ Docs\
├─ src\
│  ├─ WinPool.App\
│  ├─ WinPool.Core\
│  ├─ WinPool.Infrastructure.Windows\
│  └─ WinPool.Executor\
└─ tests\
   ├─ WinPool.Core.Tests\
   └─ WinPool.Infrastructure.Tests\
```

- `WinPool.App`: one window, operation area, logic area, settings, localization, accessibility, and view models.
- `WinPool.Core`: domain models, selection state, topology projection, plans, validation, results, and audit.
- `WinPool.Infrastructure.Windows`: read-only discovery, object correlation, privilege detection, and typed Storage cmdlet integration.
- `WinPool.Executor`: allowlisted operation execution. It is callable only when the whole WinPool process is already elevated and the effective mode is Real.

The UI and view models never accept or execute arbitrary PowerShell.

## 3. Window and workspace structure

```text
Title bar       WinPool [Administrator]   [Simulation | Real] [Settings]
───────────────────────────────────────────────────────────────────────
Upper operation area
  System | Pool | Tier | Disk | Logical volume
  Vertical objects | object information, warnings, CommandBar
════════════════ horizontal GridSplitter ═════════════════════════
Lower logic area
  Complete nested storage topology
```

- Default heights: 40% operation area and 60% logic area.
- The splitter enforces minimum heights and cannot fully collapse either area.
- Each area owns its own scrolling.
- No `NavigationView`, outer page menu, or separate Operations/History/Settings page is created.
- The Simulation/Real selector and Settings button are title-bar actions and do not consume operation-area space.
- When elevated, append ` [Administrator]` / ` [管理员]` to the application title. Standard-user mode has no suffix.
- Do not show a separate privilege label in the operation area or another status row.
- Settings replaces the complete two-area workspace until the user returns; it is not an outer-navigation destination.
- Operation history opens from the System operation area.

## 4. Execution modes

```text
ExecutionMode = Simulation | Real
PrivilegeState = StandardUser | Administrator
```

Rules:

- Normal launches start in Simulation.
- Real is not stored in preferences.
- A standard-user process can select Real and confirm a UAC restart.
- An approved one-time elevated successor enters Real directly; cancellation leaves the original process in Simulation.
- A manual administrator launch starts in Simulation and requires an explicit switch.
- Switching mode invalidates every unexecuted plan and confirmation.
- Returning to Simulation is always allowed.
- Privilege never bypasses preflight, review, confirmation, plan-hash validation, result verification, or audit.

Simulation and Real use the same typed plan:

- Simulation completes scan, plan construction, checks, command representation, confirmation preview, and audit without launching a mutating process.
- Its result is `Simulated`, not `Succeeded`.
- Real can invoke an allowlisted operation only when the process is elevated, the effective mode is Real, the plan hash is unchanged, the second preflight passes, and confirmation is valid.

## 5. Settings

Settings replaces the entire workspace below the title bar. While it is open, the category tabs, object selector, operation area, splitter, and logic topology are not shown. It contains only:

- Theme: System, Light, or Dark; System is the default.
- Language: Simplified Chinese or English; Chinese is the default.

Theme and language apply immediately and persist. System theme follows Windows changes. High contrast overrides decorative choices. A language reload preserves the selected object and topology position; user-provided labels, models, IDs, and names remain unchanged.

Execution mode stays in the title bar and is not a setting. Settings is not a popup, `ContentDialog`, flyout, drawer, or overlay.

## 6. Selection and synchronization

```text
WorkspaceSelection =
  HorizontalCategory
  + VerticalObject
  + ActiveStorageUnit
```

- Horizontal categories are System, Pool, Tier, Disk, and Logical volume in that order.
- Each category remembers its last valid object by stable ID.
- Category change rebuilds the vertical selector and updates operation content atomically.
- The lower graph remains complete; a category change only focuses/highlights matching objects.
- Clicking any lower node selects the same storage unit above and never runs a command.
- Virtual disks map to Pool and appear as indented pool children.
- Partitions map to Disk, retain their parent disk in the selector, and become the active unit in the operation heading.
- A disappeared object yields an explicit state and no automatically selected command.

## 7. Operation-area definitions

Every operation view contains identity, health, last scan, primary properties, relationships, warnings/blocking reasons, and one native `CommandBar`. Unimplemented commands are absent. Execution mode and privilege are not repeated here because their state is represented in the title bar.

### System

Vertical object:

- Current computer.

Information:

- computer and Windows version;
- current user;
- Storage Spaces subsystem;
- counts of physical disks, pools, tiers, virtual disks, and volumes;
- system, boot, page-file, and crash-dump disks;
- global health, abnormal-object count, and last scan.

Commands:

- Rescan;
- Export system snapshot;
- Copy diagnostic summary;
- View operation records.

No storage mutation appears here.

### Pool

Vertical objects:

- Unpooled;
- each storage pool;
- each pool's virtual disks as indented children.

Unpooled information:

- eligible disk count and capacity;
- rejected disks and blocking reasons;
- selected candidates;
- available Storage Spaces subsystem.

Unpooled commands:

- Rescan;
- Select candidate disks;
- Clear selection;
- Create storage pool.

Storage-pool information:

- name, stable ID, health, operational state;
- total, allocated, and free capacity;
- subsystem, member disks, tiers, virtual disks, and volumes.

Storage-pool commands:

- Rescan;
- Export pool information;
- Copy pool summary;
- Create new storage pool.

Virtual-disk information:

- name, stable ID, health, configuration state;
- resiliency, columns, interleave, provisioning, size, footprint;
- parent pool, tiers, and volumes.

Virtual-disk commands:

- Rescan;
- View parent pool;
- View related volumes;
- Export virtual-disk information.

Rename, add, optimize, resize, repair, detach, remove, and delete commands are not shown.

### Tier

Vertical objects:

- performance and capacity tiers grouped by parent pool;
- an explicit empty state when none exist.

Information:

- name, stable ID, role, media type, resiliency;
- columns, interleave, fault domain;
- supported, allocated, and footprint sizes;
- parent pool, disks, and virtual disks.

Commands:

- Rescan;
- View parent pool;
- Locate member disks;
- Export tier information;
- Copy tier summary.

No tier mutation is shown.

### Disk

Vertical objects:

- all physical disks labeled with number and friendly name;
- state indicators for protected, pooled, eligible, and unhealthy disks.

Information:

- number, model, friendly name, masked serial, stable ID;
- bus/media type, capacity, sector size;
- health, operational state, `CanPool`, and blocking reasons;
- system/boot/page-file/crash-dump protection;
- pool, tier, partitions, mount points, and volumes.

Common commands:

- Rescan;
- Copy disk identity;
- View parent pool;
- View volumes.

Eligible-disk commands:

- Add to candidate selection;
- Remove from candidate selection.

When two or more eligible disks are selected:

- Create storage pool from selected disks.

Protected and ineligible disks show their reasons and never show candidate-selection commands. Initialization, clear, offline, format, and removal are not shown.

### Logical volume

Vertical objects:

- drive-letter volumes first;
- unmounted volumes identified by label or stable ID.

Information:

- drive letter, label, stable ID;
- file system, allocation-unit size;
- total, used, and available capacity;
- health and mount points;
- partition, physical disk, virtual disk, pool, and tier relations.

Commands:

- Rescan;
- Open in File Explorer when mounted;
- Copy path;
- Copy volume information;
- View physical disk;
- View virtual disk;
- View pool.

Format, drive-letter change, grow, shrink, repair, and remove commands are not shown.

## 8. Complete logic area

```text
System
├─ Unpooled
│  └─ Physical disk
│     └─ Partition
│        └─ Logical volume
└─ Storage pool
   ├─ Storage tier
   │  └─ Physical disk reference
   └─ Virtual disk
      └─ Partition
         └─ Logical volume
```

A pool without tiers may show member physical disks directly.

- Preserve the legacy frontend's meaningful nested-container effect.
- Represent relationships through physical enclosure: system contains unpooled/pool blocks, pools contain tier/virtual-disk blocks, tiers contain disk-reference blocks, disks or virtual disks contain partitions, and partitions contain logical volumes.
- Do not draw connector, branch, leader, callout, or relationship lines. Containment is the relationship language.
- Do not migrate its colors, cut corners, button silhouettes, or branded chrome yet.
- Support select, containment-path highlight, expand, and collapse.
- Do not place execution buttons in the logic area.
- Duplicate references share the same stable ID and selected state.
- Do not imply that a Storage Spaces volume belongs directly to one member physical disk.

## 9. Storage-pool creation

```text
Scan
→ select eligible disks
→ enter pool name
→ select a uniquely identified subsystem
→ build typed plan
→ preflight
→ review disks, capacity, command, and impact
→ confirm pool name and disk count
→ handle according to effective execution mode
```

Simulation:

- uses the same typed plan and checks as Real;
- never starts a mutating PowerShell process;
- changes no disk or storage state;
- produces an explicit simulated result and audit record.

Real:

- requires the whole WinPool process to be elevated;
- re-scans and re-resolves every stable disk ID;
- blocks on changed plan hash, identity, eligibility, protection, subsystem, pool name, or snapshot;
- invokes only typed, allowlisted `New-StoragePool`;
- re-scans and verifies the new pool and expected members;
- records command representation, output, errors, exit code, before/after snapshots, and verification.

## 10. Core interfaces

Freeze conceptually:

- `ExecutionMode { Simulation, Real }`
- `PrivilegeState { StandardUser, Administrator }`
- `ThemePreference { System, Light, Dark }`
- `LanguagePreference { ZhCn, EnUs }`
- `StorageUnitKind`
- `StorageUnitRef`
- `WorkspaceSelection`
- `StorageSubsystem`
- `PhysicalDisk`
- `Partition`
- `StoragePool`
- `StorageTier`
- `VirtualDisk`
- `Volume`
- `IStorageInventoryProvider`
- `IOperationPlanner`
- `IStorageOperationExecutor`
- `IPrivilegeService`
- `IUserPreferencesService`

`OperationPlan` includes requested mode, privilege at creation, stable targets, source snapshot, typed operation data, impact, checks, confirmation, and hash.

`AuditRecord` includes requested/effective mode, privilege, mode-change time, plan hash, confirmation, snapshots, command evidence, result, and whether a mutating process started.

No public API, database schema, or C#/Python wire format is frozen in this phase.

## 11. Implementation sequence

1. Install the missing WinUI workload and verify the template only after an explicit scaffold request.
2. Create the unpackaged solution under `Program\WinPool`.
3. Implement Core models, fake inventory, preference service, privilege service, and selection state.
4. Implement read-only Windows discovery and stable identity correlation.
5. Implement the single-window two-area shell, title-bar controls, administrator title suffix, and full-workspace settings replacement.
6. Implement category/object synchronization and complete topology interaction.
7. Implement pool-creation plan, preflight, review, confirmation, simulation, and audit.
8. Implement the guarded Real branch and allowlisted executor without using it on the current machine.
9. Validate on disposable disks only after the user supplies and explicitly approves that environment.
10. Migrate selected legacy visual tokens only after structure, resize, keyboard, theme, and language behavior are stable.
11. Integrate Dite, analysis, and reports only after management is stable.

## 12. Verification

Allowed now:

- documentation and static review;
- later build/analyzer checks;
- pure unit tests with fake graphs;
- mocked standard/admin privilege states;
- mode-switch and plan-invalidation tests;
- serialization and stable-ID tests;
- command-review snapshots;
- simulation success, block, cancel, expiry, and verification-failure cases;
- theme/language preference tests;
- topology/category/vertical-selector synchronization tests.

Forbidden on the current machine:

- any `New-StoragePool` invocation;
- any elevated storage-operation integration test;
- using an attached disk as a candidate and cancelling later;
- initialization, format, partition, pool, tier, virtual-disk, or volume mutation.

## 13. Acceptance gates

Documentation gate:

- this edition is internally consistent;
- old UI images are archived under root `Old`;
- current Edition 3 showcase images are clearly marked as non-authoritative references;
- no WinUI code exists.

Scaffold gate:

- separate explicit authorization;
- WinUI toolchain present;
- unpackaged project builds and opens one responsive window.

Read-only UI gate:

- two-area layout, settings, themes, languages, selection synchronization, complete topology, keyboard access, and high contrast verified.

Pool-creation code gate:

- Simulation cannot launch a mutating process;
- Real requires an already elevated WinPool process;
- typed plan, second preflight, confirmation, hash, verification, and audit are complete;
- real-hardware validation remains pending.
