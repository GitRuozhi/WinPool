# WinPool Edit-page topology workspace Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** draft; awaiting developer answers and explicit approval; **not confirmed**
- **Created:** 2026-09-02
- **Baseline commit:** `c471311460445d9b57fe790df2a00444bed7754a`
- **Working branch:** `main`
- **Current product version:** V0.45
- **Target product version:** undecided (proposed V0.46; see Q10)
- **Stage type:** Edit-page layout and simulated pool/partition workspace redesign; no real storage mutation

This file exists because the developer asked to write `docs/Plan.md` from the
Edit-page requirements below, then wait. It is **not** a confirmed plan.
Archive history must not be used to invent extra stages or extra features.

Writing this Plan does not authorize implementation, push, tag, GitHub Release,
binary upload, deployment, or real storage mutation. Implementation starts only
after the developer answers the blocking questions, explicitly approves this
Plan, and then explicitly asks to execute it.

## 1. Developer decisions already stated

These ten items are the controlling product intent for this stage. They outrank
the current Edit page. They do not outrank the real-mutation safety boundary.

1. The Edit page has two vertical halves of equal height. Each half scrolls by
   itself. The page must not scroll the two halves together.
2. Do not show titles for those two halves.
3. The upper half is two subregions: logical topology on the left, a control
   group on the right.
4. The lower half is two subregions: logical topology on the left, a control
   group on the right.
5. The upper topology reuses the Manage-page logical-topology visual structure,
   with these projection rules:
   - Show every locally partitionable disk.
   - Physical disks that are members of a non-primordial pool are not shown.
   - A virtual disk is shown if it can be partitioned.
   - Each disk shows its partitions.
   - Only two levels: disk and partition. Do not show system, pool, or tier.
   - Disks are stacked vertically. Partitions inside a disk are arranged
     horizontally.
6. The lower topology reuses the Manage-page logical-topology visual structure,
   with these projection rules:
   - Show every internal pool, including the primordial pool.
   - Do not show external pools (for example network pools).
   - The primordial pool is shown down to disks. It does not show partitions.
   - A storage pool shows tiers, disks, and virtual disks; virtual disks show
     partitions.
   - To the right of all pools, add a fake pool that shows a plus sign. Clicking
     it creates a new pool. A new pool defaults to a virtual disk, a performance
     tier, and a capacity tier. The virtual disk shows as not created. Both
     tiers are empty and have no disk members.
7. The lower-right control group is a two-column property grid: item name on
   the left, item value on the right. Rows, in this order:
   - Execute modify / create new pool
   - Pool name
   - Virtual disk name
   - Performance-tier resiliency
   - Performance-tier interleave
   - Performance-tier size
   - Capacity-tier resiliency
   - Capacity-tier interleave
   - Capacity-tier size
   - Partition file system
   - Partition cluster size
8. In the lower topology, physical disks can be dragged freely among pools.
9. A dragged physical disk enters the matching tier automatically: SSD into
   the performance tier, HDD into the capacity tier.
10. Unclear boundaries must be asked, not guessed into code.

## 2. Blocking questions

Implementation must not start while these are unanswered. Proposed defaults
below are **not** decisions.

### Q1. Upper-right control group (blocking)

The lower-right rows are specified. The upper-right control group is not.
The current Edit page puts these actions on the upper right: extend, shrink,
delete, format, new partition, initialize, offline, plus a selected-partition
summary.

What belongs in the upper-right control group after this redesign?

### Q2. When does a drop persist? (blocking)

Current Edit-page pool drops call `MovePhysicalDisk` immediately. The new
lower-right first row is “Execute modify / create new pool”.

Does dragging a disk among existing pools (including primordial) commit the
simulated move immediately, or only after Execute?

### Q3. What does clicking the plus create? (blocking)

Does the plus:

- insert a **local draft** pool in the Edit workspace only, persisted when
  Execute succeeds; or
- immediately create an empty simulated pool in the document?

Current `CreateStoragePool` requires at least one primordial member disk and
creates no tiers and no virtual disk.

### Q4. Does Execute modify existing pools in this stage? (blocking)

“Execute modify / create new pool” can mean:

- A. Create is in scope; modifying names, resiliency, interleave, sizes, and
  partition format of an **existing** pool is also in scope.
- B. This stage only creates. Selecting an existing pool fills the grid as
  read-only, or hides Execute.

Current simulation can create a pool, create a virtual disk, move a disk, and
edit partitions. It cannot create storage tiers, cannot move a disk that is
already a tier member, and cannot change existing pool/tier/virtual-disk
parameters.

### Q5. Primordial “显示到层级到磁盘” (blocking)

Two readings:

- A. Show the primordial pool down to the disk level; no tiers; no partitions.
- B. Show primordial tiers and disks; still no partitions.

This Plan uses reading A unless told otherwise. A primordial pool in Windows
Storage Spaces does not have Storage Spaces tiers.

### Q6. Upper-region disk set (blocking)

`PartitionableDiskPolicy` currently shows RAW/GPT/MBR OS disks with size > 0,
including boot and system disks. Confirm the upper list:

- Keep boot/system disks if they are partitionable?
- Hide physical members of non-primordial pools (stated)?
- Show virtual disks that project an OS disk (stated)?
- Show “other” OS disks with no physical/virtual link?
- Hide network disks (assumed)?

### Q7. Unallocated space in the upper topology (blocking)

The current partition bar shows unallocated space as a clickable segment.
The new two-level topology is disk → partitions. Should unallocated space
appear as a child node of the disk, or only in the upper-right controls?

### Q8. Media types other than SSD/HDD (blocking)

SSD → performance, HDD → capacity is stated. Confirm:

- SCM: performance (matches current finding rules) or something else?
- Unknown / Unspecified: refuse the drop, leave the disk in a pool-level
  unallocated group, or force one of the two tiers?

### Q9. Drop target (blocking)

May the user drop onto a specific tier, or only onto a pool, with auto-routing
always choosing the tier? If a disk is dropped onto the wrong-media tier,
reject, or still auto-correct onto the matching tier?

### Q10. Product version (blocking)

Stay on V0.45 for this stage, or set the product version to V0.46 after the
work is accepted? This Plan does not change `Directory.Build.props` until that
is decided.

### Q11. Create defaults and Execute payload (blocking)

Current create path, after confirm, does: create pool → create virtual disk →
create partition → format NTFS. Proposed defaults, not decisions:

| Field | Proposed default |
| --- | --- |
| Performance resiliency | Simple (current single-resiliency default) |
| Capacity resiliency | Simple |
| Both interleaves | 64K |
| Both tier sizes | blank = member-disk derived |
| File system | NTFS |
| Cluster size | 64K |
| Empty new pool on Execute | refuse; at least one member disk |
| After create | still create one partition and format it using the form |

Also confirm whether the 64K + 64K research note remains as helper text under
the cluster-size row. It is not a half-title.

### Q12. Layout chrome (non-blocking if the proposed default is accepted)

Proposed defaults:

- Upper/lower height starts at 1:1. A row `GridSplitter` is allowed; it must
  not restore a single page-wide scroll.
- Inside each half, topology : controls starts at 2:1, matching the current
  Edit columns. A column splitter is allowed.
- Each of the four panes scrolls vertically by itself, so a long topology does
  not hide the form. This is stricter than “two halves scroll”, and is the
  intended reading unless told to share one scrollbar per half.
- No titles for the halves, and no extra “logical topology” / “control group”
  headings.

## 3. Current baseline that this stage must not silently break

- Product remains on the V0.4 line. Real storage-structure mutation stays
  denied. Simulation is the only edit execution path.
- Manage-page `TopologyProjector.Project` stays the complete system view
  (system, pools including primordial, tiers, disks, virtual disks,
  partitions, network group, other-OS group). This stage adds Edit-specific
  projections. It does not change Manage topology rules.
- Visual reuse means `TopologyNodeControl`, `AdaptiveFlowPanel`,
  `WeightedPoolPanel`, and the existing Stack / Flow / WeightedFlow layouts.
  It does not mean feeding Manage’s full tree into Edit.
- Current Edit upper list uses `PartitionableDiskPolicy` on `OsDisks`. Current
  lower list shows non-primordial pool tiles, a staging-plus tile, and a
  primordial disk `ListView`.
- Current `CreateStoragePool` moves primordial members into a new pool and
  creates no tiers. Current `CreateVirtualDisk` creates a RAW OS disk.
  Current `MovePhysicalDisk` refuses boot/system/page-file/crash-dump disks
  and refuses disks that already belong to a storage tier.
- There is no simulated `CreateStorageTier` path today. Auto-tier assignment
  therefore requires new simulation work if Q2/Q3/Q4 keep disk-and-tier
  membership in the document.
- Local systems remain read-only. Topology may still be shown. Drag, Execute,
  and other mutations stay disabled unless the active document is a
  simulation.
- Schema 14 and IPC protocol 4 are not changed unless a later approved answer
  proves a persistence or wire change is required. Snapshot already has
  `StorageTierInfo`.

## 4. Proposed shape after the blocking answers

This section is a mapping, not extra product scope.

### 4.1 Page chrome

Replace the current page-wide `ScrollViewer` + stacked titled cards with a
two-row grid, each row `Height="*"`, no half titles. Each half is
left topology / right controls.

### 4.2 Upper topology

New Application projection, for example
`TopologyProjector.ProjectPartitionWorkspace(snapshot)`:

- Eligible OS disks after Q6, stacked (`TopologyChildrenLayout.Stack`).
- No system / pool / tier / network / other-group wrapper that the user can
  see.
- Each disk’s children are its partitions in offset or partition-number
  order, using Flow so they sit in a horizontal row and wrap if needed.
- A virtual disk is shown as one disk node (the partitionable OS disk or the
  virtual disk itself), not as a pool member physical disk.

Bind that forest through existing `TopologyNodeControl`.

### 4.3 Lower topology

New Application projection, for example
`TopologyProjector.ProjectPoolWorkspace(snapshot)`:

- Internal pools only: primordial first, then named pools, then the fake plus
  node. No network group.
- Pool row uses Flow or WeightedFlow so the plus node sits to the right of
  the pools.
- Primordial: physical disks as direct children; `includeOsChildren: false`.
- Named pool children, top to bottom: virtual disk(s) with partitions,
  performance tier, capacity tier, then any unallocated members if Q8 leaves
  disks outside both tiers.
- Fake plus node: Edit-only presentation id, not a `StoragePoolInfo` in the
  snapshot. Clicking it follows Q3.

A newly created pool, once visible, shows:

- virtual disk node whose display name/state is “not created” until a real
  virtual disk exists;
- empty performance tier;
- empty capacity tier.

### 4.4 Drag and auto-tier

Physical-disk nodes in the lower Edit tree become drag sources. Manage-page
nodes do not. Drop targets are pools, the plus/draft pool, and (if Q9 allows)
tiers.

Auto-tier rule after a successful pool membership change:

- SSD → performance tier (`MediaType` SSD).
- HDD → capacity tier (`MediaType` HDD).
- SCM / Unknown follow Q8.

If the document is the authority (Q2 immediate, or Execute later),
`MovePhysicalDisk` must be extended so a disk can leave a tier in the source
pool and enter the matching tier in the target pool. Today that move is
refused.

Boot, system, page-file, and crash-dump disks stay non-movable, matching the
current simulation rule, unless the developer later overrides it.

### 4.5 Lower-right grid

Two columns, eleven rows, bilingual names. Values are `TextBox` / `ComboBox`
/ the Execute button. Selecting a pool or draft fills the grid. The first-row
button label is “Create new pool” for a draft/plus and “Execute modify” for an
existing pool if Q4 includes modify; otherwise only create is enabled.

Execute remains simulation-only, typed, and previewed. It does not call real
Storage Spaces cmdlets.

### 4.6 Upper-right group

Deferred to Q1. Do not invent a second command surface before that answer.
Until then, the existing partition operations are the only known candidate
set, not an approved set.

## 5. Work items after approval

Do not start these until the Plan is confirmed and execution is requested.

| Id | Work | Notes |
| --- | --- | --- |
| EP1 | Edit page chrome: 50/50 halves, no half titles, independent scroll, left/right subregions | `EditPage.xaml` |
| EP2 | Partition-workspace projection and upper `TopologyNodeControl` bind | Application projector + tests; Manage `Project` unchanged |
| EP3 | Upper-right control group | Q1 |
| EP4 | Pool-workspace projection, plus node, draft-pool visuals | Application + Edit page |
| EP5 | Disk drag among pools and auto-tier | Shared control must stay inert on Manage |
| EP6 | Simulation operations required by Q2–Q4 (likely `CreateStorageTier`, tier-aware move, create/modify Execute path) | `SimulationEditKind` / `StorageSystems`; no real mutation |
| EP7 | Lower two-column property grid and Execute | Simulation only; confirm dialog before commit |
| EP8 | Localization for new strings | `LocalizationService`; both languages |
| EP9 | Tests for the two projections, eligibility filter, auto-tier, and non-regression of Manage topology | Smallest related checks; not a full acceptance gate |
| EP10 | Version and CHANGELOG | Only after Q10 and after the developer accepts the implemented stage |

Commit split after execution: documentation, refactor, feature, visual. Do not
mix them.

## 6. Out of scope

- Real disk, partition, volume, pool, tier, or virtual-disk mutation.
- Changing Manage-page topology rules or the accepted visual baseline of that
  page except where Edit reuses the same control.
- Network / external pool editing.
- Test and Development workspace features.
- Schema or IPC version changes unless a confirmed answer proves they are
  required.
- Push, tag, Release, or binary upload.
- Revising or publishing the V10 article.
- Work outside `Program\WinPool`.

## 7. Safety

- Deny-by-default executor stays in force.
- UAC or Real mode is not authorization.
- Protected-machine policy continues to refuse `R4+` real structure mutation.
- Hardware data stays behind the redaction boundary.
- Inventory PowerShell stays embedded and read-only.
- A development Agent must not perform a real mutation under this Plan.

## 8. Verification when this Plan is later executed

Documentation-only edits of this file do not run code tests.

After a later approved implementation:

- Application tests for the two Edit projections and auto-tier assignment.
- Existing Manage topology tests remain green without rewriting Manage rules.
- Local simulation-only click-through of Edit: independent half scroll, no
  half titles, two-level upper tree, internal-pool lower tree with plus node,
  drag, Execute. Real hardware is not a verification method.
- Full quality gate and OS-matrix evidence stay `not_required` until the
  developer asks for formal testing.

## 9. Approval gate

This Plan is ready for developer review. It is not ready for implementation.

Reply with answers to Q1–Q11 (Q12 may be accepted as proposed). After that,
either confirm this Plan, or name the changes to make before confirmation.
Do not treat silence as approval.
