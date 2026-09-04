# WinPool Unified Topology Layout Engine Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed and installed as the active Plan; implementation
  not started; execution awaits the developer's explicit request
- **Created:** 2026-09-03
- **Updated:** 2026-09-04 (installed; baseline re-anchored; units clarified
  as DIP; viewport-isolation automatic test, `TopologyLayoutMapper` mapping,
  and the XAML-migration deletion precondition added)
- **Baseline commit:** `0ef6d22b1803f3886a831fa34fbfb1dc4bc68f94`
  (installation baseline; originally authored against `7bd21f2`)
- **Working branch:** `main`
- **Current product version:** V0.45
- **Target product version:** V0.45
- **Stage type:** topology-layout refactor and Edit-page presentation refinement; simulation behavior remains unchanged

This Plan records the developer's current decisions for unifying the three logical
topology regions around the Manage-page layout behavior.

The Manage page is the baseline. The Edit upper and Edit lower regions are not
independent topology implementations. They are restricted projections of the
same topology model rendered through the same layout engine, with only the
explicit differences recorded below.

This Plan does **not** authorize real storage mutation, push, tag, GitHub Release,
binary upload, deployment, schema changes, IPC changes, or unrelated product
work.

The implemented Edit-page Plan was frozen under `docs/Archive/V0.45` with its
developer-accepted final state on 2026-09-04. The Archive index and matching
Chinese reading copies were updated in the same documentation work item.

No implementation begins until the developer explicitly requests execution of
this Plan.

---

## 1. Controlling decisions

These decisions control this stage and outrank the existing Edit-page layout
implementation.

### 1.1 One topology layout engine

WinPool shall have one topology layout engine:

`TopologyLayoutEngine`

Manage, Edit upper, and Edit lower shall all use this engine for topology
measurement, row formation, minimum-width calculation, available-width
handling, child-width allocation, and final layout planning.

The three regions may provide different topology trees and small layout or
presentation options, but they shall not own independent layout algorithms.

In particular:

- Manage remains the behavioral and visual baseline.
- Edit upper is a restricted disk/partition projection.
- Edit lower is a restricted pool projection.
- `AdaptiveFlowPanel`, `WeightedPoolPanel`, or replacement panels may remain as
  WinUI measure/arrange adapters, but they must not implement competing layout
  algorithms.
- Equal-fill, weighted-fill, capacity-fill, wrapping, and minimum-width rules
  belong to `TopologyLayoutEngine`.
- Panels consume engine results and perform mechanical WinUI measurement and
  arrangement only.
- Do not introduce a second Edit-specific layout engine.
- Do not introduce page-name checks such as `if (Manage)` or `if (EditUpper)`
  inside the core algorithm. Required differences are explicit layout input or
  policy data.

The implementation must remain the minimum closed loop needed for these three
regions. Do not build a generic public layout framework.

### 1.2 Manage page is the reference behavior

The Manage topology remains functionally and visually equivalent to the current
Manage implementation.

Its projection continues to show the complete logical topology according to the
current `TopologyProjector`.

Existing Manage rules for:

- system root;
- pools;
- tiers;
- physical disks;
- virtual disks;
- partitions;
- network and other groups;
- Stack / Flow / WeightedFlow relationships;
- sibling packing;
- row-height relaxation;
- topology selection;
- expansion;
- minimum node width;

remain the reference behavior.

This stage may refactor the internal implementation used to produce that result,
but must not deliberately redesign the Manage topology.

Manage-layout regression tests are therefore the compatibility reference for the
unified engine.

---

## 2. Edit lower topology

### 2.1 Structure

The Edit lower topology uses the same pool-oriented structure and layout
behavior as Manage wherever no Edit-specific rule is listed below.

It shows internal pools only, including the primordial pool.

The existing Edit working-copy, drag, simulation Execute, create, modify, and
dissolve behavior is not redesigned by this stage.

### 2.2 Primordial pool

The primordial pool shows its physical disks.

Physical disks shown inside the primordial pool do **not** show partitions in
the Edit lower topology.

This is an Edit-lower projection rule only. It does not alter the Manage
projection.

Conceptually:

```text
Primordial
├─ Physical Disk
├─ Physical Disk
└─ Physical Disk
```

not:

```text
Primordial
└─ Physical Disk
   └─ Partition
```

### 2.3 Existing pools

Non-primordial pools retain the current Edit lower logical content unless this
Plan explicitly changes it.

Existing tier, physical-disk, virtual-disk, and virtual-disk partition
representation remains available.

This Plan does not change pool-editing semantics or simulation operations.

### 2.4 Synthetic plus pool

A synthetic plus-pool remains the Edit lower affordance for adding a pool.

It is:

- visually a pool peer;
- logically synthetic;
- always placed after the real pools;
- therefore the rightmost pool when the pool row fits on one row;
- ordered after all real and draft pools when packing requires more than one row.

Conceptually:

```text
Primordial | Pool A | Pool B | +
```

The plus-pool continues to create only the existing local draft-pool working
state. It does not create real storage.

### 2.5 Layout

Edit lower uses the same weighted pool-layout rules as Manage unless an
Edit-lower rule above changes the projected children.

There is no separate Edit-lower packing algorithm.

---

## 3. Edit upper topology

### 3.1 Two levels only

The Edit upper topology contains exactly the disk/partition workspace needed for
partition management.

Visible logical levels are:

```text
Disk
└─ Partition / Unallocated
```

The Edit upper topology does not show:

- System;
- Storage Pool;
- Storage Tier;
- pool group;
- direct-disk group;
- virtual-disk group;
- network group;
- other topology grouping containers.

Any invisible root required to feed the unified engine is layout-only and does
not constitute a visible third level.

### 3.2 Eligible disks only

Only disks that can be managed through the existing partition-management
policy are projected.

Continue to use the existing partitionability policy rather than duplicating
eligibility rules in the page.

The current product behavior remains:

- eligible system/boot disks may be shown;
- existing operation restrictions still apply;
- virtual disks that are partitionable may be shown;
- physical disks consumed by a non-primordial pool are not separately shown as
  partitionable raw disks;
- network mapped drives are not shown.

This Plan does not widen the set of disks on which operations are permitted.

### 3.3 Disk arrangement

All Edit-upper disks are arranged vertically.

Conceptually:

```text
Disk 0
Disk 1
Disk 2
Disk 3
```

A disk does not share its horizontal row with another disk.

The unified engine shall represent this as a vertical/Stack relationship rather
than relying on an unrelated page-specific layout algorithm.

### 3.4 Partition arrangement

Partitions and visible unallocated regions inside one disk are arranged in one
horizontal strip in disk-offset order.

Conceptually:

```text
Disk 0
[Partition 1][Unallocated][Partition 2][Partition 3]
```

The children of one disk do not wrap onto a second logical partition row.

When their minimum total width exceeds the available topology width, the row
keeps the minimum child widths and becomes horizontally wider than the
viewport. The existing topology scroll surface handles the overflow.

This keeps the disk map spatially coherent instead of moving later partitions
onto an unrelated second row.

### 3.5 Unallocated regions

Existing Edit behavior for unallocated regions remains:

- leading gaps are separate nodes;
- interior gaps are separate nodes;
- trailing gaps are separate nodes;
- ordering follows disk offset;
- the configured partition-gap ignore threshold still determines which gaps are
  visible.

A visible unallocated node participates in capacity-based width allocation using
the size of that exact gap.

Hidden gaps below the configured threshold do not receive a visible layout slot.

---

## 4. Capacity-proportional partition widths

All widths in this Plan are WinUI device-independent units (DIP), not physical
pixels.

### 4.1 Minimum width

Every visible partition or unallocated child starts with the topology leaf
minimum width:

`112 DIP`

unless the existing shared engine minimum is intentionally changed in a
separately approved stage.

This Plan does not change the Manage minimum width.

### 4.2 Remaining-width allocation

For the Edit upper partition strip only, width remaining after all minimum
widths and sibling spacing have been reserved is distributed according to the
storage size represented by each visible child.

For child `i`:

```text
assigned width
    = minimum width
    + remaining width × child size / total visible child size
```

Both partitions and visible unallocated regions use their actual byte size as
the capacity weight.

The allocation is therefore spatial rather than equal-fill.

Example:

```text
Partition A = 100 GiB
Partition B = 200 GiB

minimum:
A = 112 DIP
B = 112 DIP

remaining width = 333 DIP

A extra = 333 × 100 / 300 = 111 DIP
B extra = 333 × 200 / 300 = 222 DIP

final:
A = 223 DIP
B = 334 DIP
```

The implementation may use floating-point layout widths. Any final rounding
must be deterministic and the final child must absorb any residual rounding
difference so the planned row width remains consistent.

### 4.3 No remaining width

If:

```text
available width <= minimum widths + spacing
```

no negative proportional adjustment occurs.

Each child keeps at least its minimum width and the horizontal strip overflows
the viewport when necessary.

### 4.4 Scope of proportional sizing

Capacity-proportional remaining-width allocation applies only to the Edit upper
disk's partition/unallocated row.

It does not replace Manage's existing weighted topology rules and does not
change Edit lower pool weighting.

The unified engine therefore supports the different extra-space distributions,
but the selected distribution is supplied as layout input rather than
implemented by a separate panel or engine.

---

## 5. Edit upper disk presentation

The visible Edit-upper disk node uses a compact horizontal header instead of the
standard multi-line topology card header.

The information currently spread over the normal disk card's approximately
three text lines is arranged horizontally where practical.

Conceptually:

```text
Disk 0    Samsung SSD ...    GPT    1.82 TiB
```

Exact separators and truncation are presentation details, but the result must:

- remain one compact disk header;
- preserve the information required to identify the disk;
- preserve accessibility names;
- preserve selection and interaction behavior;
- allow long device names to trim or wrap only when the available width makes
  that unavoidable.

This compact presentation applies to Edit upper disk nodes only.

Manage topology cards remain the reference presentation and are not changed by
this requirement.

Edit lower cards remain on the normal topology presentation unless separately
specified.

The implementation should extend the shared `TopologyNodeControl` with a small
explicit presentation variant rather than create a second disk-control tree.

---

## 6. Implementation mapping

This section maps the confirmed requirements to the current implementation. It
does not add product scope.

### 6.1 Application layout engine

Primary owner:

`src/WinPool.Application/TopologyLayoutEngine.cs`

Refactor the current engine so its result is sufficient for panels to perform
final arrangement without inventing another width-allocation algorithm.

The engine owns:

- natural subtree measurement;
- minimum leaf width;
- ancestor chrome;
- sibling spacing;
- Stack layout;
- Flow layout;
- WeightedFlow layout;
- sibling row formation;
- shrink decisions;
- row-height relaxation;
- final child width assignment;
- extra-space distribution;
- no-wrap horizontal strip behavior required by Edit upper.

Existing Manage calculations remain the baseline.

If required, add minimal layout-input metadata to distinguish:

- normal existing extra-space allocation;
- capacity-proportional extra-space allocation;
- wrap versus no-wrap child rows.

Do not encode page names into `TopologyLayoutEngine`.

### 6.2 Layout result

The engine result must carry enough information for the WinUI panel to know the
final row membership and final allocated width of each child.

Do not leave final weighted or capacity-based stretch calculation in a specific
WinUI panel.

The final result may extend the existing `TopologyLayoutResult` with explicit
row-slot width information or an equivalent representation.

The exact data type is an implementation choice; the ownership boundary is not.

### 6.3 WinUI topology panels

Current relevant controls include:

- `AdaptiveFlowPanel`
- `WeightedPoolPanel`
- `TopologyNodeControl`

After this stage, any retained layout panels are thin adapters around
`TopologyLayoutEngine`.

They may:

- obtain the actual available WinUI width;
- convert the node ViewModel to engine input;
- call the engine;
- measure children using engine-assigned widths;
- arrange children according to engine rows and slots.

They may not independently decide:

- how many children fit;
- equal-fill versus weighted-fill;
- capacity proportions;
- row packing policy;
- shrink priority.

If a single common topology panel cleanly replaces both
`AdaptiveFlowPanel` and `WeightedPoolPanel`, that replacement is allowed.

The exact obsolete candidates authorized for removal by this Plan are:

- `src/WinPool.App/Controls/AdaptiveFlowPanel.cs`
- `src/WinPool.App/Controls/WeightedPoolPanel.cs`

They may be deleted only after:

1. the common replacement is present;
2. all code references have been migrated;
3. all XAML usage sites (including `MainPage.xaml` and `EditPage.xaml`) no
   longer reference the removed panel types;
4. Manage regression tests pass;
5. Edit-upper and Edit-lower targeted layout tests pass.

Deletion is not required merely for architectural neatness. Keeping thin
adapters is acceptable if it is the smaller closed-loop change.

### 6.4 Manage projection

Primary owner:

`src/WinPool.Application/Topology.cs`

Do not redesign `TopologyProjector.Project`.

Any changes required to supply the unified engine with layout metadata must
preserve the projected Manage topology and current ordering.

### 6.5 Edit projections

Primary owner:

`src/WinPool.Application/EditWorkspace.cs`

#### Upper

`ProjectPartitionWorkspace` remains responsible for selecting partitionable
disks and building visible disk, partition, and unallocated nodes.

Change its layout metadata so the unified engine receives:

- a vertical disk collection;
- one no-wrap horizontal partition strip per disk;
- actual partition/gap size as the capacity stretch weight.

Do not reproduce layout arithmetic inside `EditWorkspace`.

#### Lower

`ProjectPoolWorkspace` / `ProjectPoolWorkspaceRoot` remain responsible for the
Edit-lower pool subset and the synthetic plus-pool.

Ensure:

- primordial physical disks have no partition children in the lower projection;
- real pools retain the intended existing content;
- the plus-pool is ordered last;
- the tree uses the same weighted layout semantics consumed by Manage.

### 6.6 ViewModel conversion

`TopologyNodeViewModel` remains the shared presentation model.

The mapping from application layout nodes to ViewModels must preserve the layout
metadata needed by the unified engine.

The current App-side conversion lives in
`src/WinPool.App/Services/TopologyLayoutMapper.cs`; updating that mapping to
carry engine layout metadata is part of this stage.

Do not create separate Manage/Edit topology ViewModel hierarchies.

### 6.7 Edit upper compact header

Primary owners:

- `src/WinPool.App/Controls/TopologyNodeControl.xaml`
- `src/WinPool.App/Controls/TopologyNodeControl.xaml.cs`

Add the smallest explicit presentation mode necessary for the compact
Edit-upper disk header.

The same control continues to own:

- selection;
- expansion where applicable;
- keyboard interaction;
- accessibility;
- drag/drop hooks;
- normal topology-card rendering.

Do not duplicate those behaviors into an Edit-only disk control.

### 6.8 Viewport ownership

Edit upper and Edit lower must not overwrite a single shared global topology
viewport width that can affect Manage layout.

Prefer the actual width supplied to the WinUI layout panel during
`MeasureOverride` / `ArrangeOverride`.

Fallback viewport state may remain where WinUI infinite-width measurement
requires it, but each topology surface must use its own host width.

Edit upper, Edit lower, and Manage therefore must not race to set one shared
`TopologyViewportWidth`.

---

## 7. Work items after approval

Do not start these until the developer explicitly requests execution.

| Id | Work |
| --- | --- |
| TL0 | Documentation rollover: archive the completed previous Plan and reading copy, update Archive index/copy, install this Plan and matching `Plan.zh-CN.md` (completed 2026-09-04) |
| TL1 | Refactor `TopologyLayoutEngine` so final row and child-width allocation are engine-owned |
| TL2 | Route normal Flow and WeightedFlow panel arrangement through the unified engine; preserve Manage output |
| TL3 | Remove Edit-page dependence on a shared Manage viewport-width state, including the automatic viewport-isolation regression test |
| TL4 | Refine Edit-upper projection: eligible disks only, two visible levels, vertical disks, no-wrap horizontal partition strips |
| TL5 | Add Edit-upper capacity-proportional remaining-width allocation for partitions and visible unallocated gaps |
| TL6 | Refine Edit-lower projection: primordial disks without partitions and synthetic plus-pool ordered last |
| TL7 | Add compact horizontal Edit-upper disk presentation in the shared node control |
| TL8 | Add targeted layout, projection, and architecture regression tests |
| TL9 | After developer acceptance of implementation, record the important final result in CHANGELOG; version remains V0.45 |

Commit split after execution:

1. documentation rollover;
2. equivalent layout-engine refactor;
3. Edit projection / layout behavior;
4. compact visual presentation;
5. final accepted documentation result if required.

This repository remains direct-to-`main`; do not create a feature branch or PR.

Do not push unless the developer explicitly asks.

---

## 8. Required targeted tests

This is ordinary feature/refactor work, not an automatic full-quality-gate run.

Run the smallest directly related automatic tests during implementation.

### 8.1 Unified layout engine

Extend the existing `TopologyLayoutEngineTests`.

Required cases include:

- current Manage reference cases continue to produce equivalent row packing;
- current Manage weighted siblings retain their existing width behavior;
- Stack children remain vertical;
- Flow behavior still respects existing Manage expectations;
- WeightedFlow behavior still respects current Manage expectations;
- no-wrap horizontal strip does not create a second row;
- when the no-wrap minimum width exceeds the viewport, minimum widths are
  retained;
- entering or leaving Edit, or resizing either Edit half, does not alter Manage
  layout inputs (shared-viewport isolation).

### 8.2 Capacity allocation

Required deterministic cases:

#### Case A

```text
sizes: 100 GiB, 200 GiB
minimum width: 112 DIP each
remaining width: 333 DIP
expected final widths: 223 DIP, 334 DIP
```

#### Case B

Three visible children including unallocated:

```text
100 GiB partition
50 GiB unallocated
200 GiB partition
```

Remaining width is distributed in the ratio:

```text
100 : 50 : 200
```

#### Case C

No extra width:

all children remain at minimum width and the row may exceed the viewport.

#### Case D

A hidden unallocated gap below the configured threshold receives no layout slot
and no visible capacity share.

### 8.3 Edit upper projection

Extend `EditWorkspaceTests` to prove:

- only partitionable disks are shown;
- physical members of non-primordial pools are excluded from the raw
  partitionable-disk list;
- eligible virtual disks remain available;
- network drives are absent;
- disk ordering remains deterministic;
- partitions and gaps stay in offset order;
- leading, interior, and trailing visible gaps remain distinct;
- upper topology has only visible disk and partition/unallocated levels.

### 8.4 Edit lower projection

Tests must prove:

- primordial pool is present;
- primordial physical disks have no partition children in Edit lower;
- non-primordial pool structure remains available;
- the synthetic plus-pool is present exactly once;
- the synthetic plus-pool is ordered after all real/draft pools.

### 8.5 Manage non-regression

Existing Manage topology and layout tests must pass without rewriting their
expected behavior merely to accommodate the refactor.

A failing Manage expectation must be treated as a regression unless the
developer explicitly changes the requirement.

### 8.6 Presentation structure

Add the smallest architecture/presentation test practical for:

- Edit upper selecting the compact disk presentation mode;
- Manage remaining on the standard presentation mode;
- Edit lower remaining on the standard presentation mode;
- all three still using the shared `TopologyNodeControl`.

Do not claim visual appearance passed from an automatic structure test.

---

## 9. Manual verification after implementation

Native visual evidence remains separate from automatic tests.

A local simulation-only Edit click-through should check:

1. Manage topology before and after entering Edit has equivalent packing.
2. Resize Manage and confirm existing topology behavior is unchanged.
3. Resize Edit upper and confirm Edit lower/Manage layout does not change because
   of a shared viewport variable.
4. Edit upper shows only eligible disks.
5. Edit upper disks are vertically stacked.
6. Each disk's partition map remains a single horizontal strip.
7. Large and small partitions visibly receive proportionally different extra
   widths.
8. Unallocated space participates proportionally.
9. Many partitions cause horizontal overflow rather than a second partition
   row.
10. Edit-upper disk identification is shown as a compact horizontal header.
11. Edit lower primordial disks do not expose partitions.
12. Edit lower plus-pool appears after the real pools.
13. Existing Edit drag, draft, Execute, modify, and dissolve simulation behavior
    still works.

Until a human/native run is actually performed, this evidence is
`unverified`.

A full Quality gate is `not_required` for ordinary implementation of this Plan
unless the developer explicitly requests formal testing.

---

## 10. Out of scope

This stage does not include:

- changing the Manage logical topology design;
- redesigning Manage cards;
- capacity-proportional pool widths;
- changing storage-pool simulation semantics;
- changing partition operation semantics;
- new partition eligibility rules;
- new storage operations;
- real storage mutation;
- database schema changes;
- IPC protocol changes;
- new persistence;
- new settings other than preserving the existing unallocated-gap setting;
- network/external pool editing;
- packaging;
- deployment;
- unrelated visual redesign;
- public layout APIs or plug-in architecture.

If implementation appears to require one of these, stop that expansion and
return to the developer instead of silently broadening this Plan.

---

## 11. Safety

The existing deny-by-default execution boundary remains unchanged.

- All storage-structure changes in this stage remain simulation-only.
- Real disk, partition, volume, pool, tier, or virtual-disk mutation is not
  authorized.
- UAC elevation or Real mode is not authorization.
- Hardware data remains behind the existing redaction boundary.
- Inventory PowerShell remains embedded and read-only.
- No real hardware mutation is an accepted test method under this Plan.

---

## 12. Acceptance gate

Implementation is complete only when:

- all three topology regions use `TopologyLayoutEngine` as the single layout
  algorithm authority;
- no Edit-specific panel independently performs equal-fill, weighted-fill, or
  capacity-fill layout decisions;
- Manage behavior remains equivalent;
- Edit lower primordial disks do not display partitions;
- Edit lower plus-pool is the final pool item;
- Edit upper contains only eligible disk → partition/unallocated topology;
- Edit upper disks are vertically stacked;
- partitions inside one disk form one horizontal no-wrap strip;
- spare horizontal width is distributed by partition/unallocated capacity;
- the 100 GiB / 200 GiB / 333 DIP example produces 223 DIP / 334 DIP;
- Edit upper disk cards use the compact horizontal presentation;
- shared viewport state no longer allows Edit upper/lower resizing to mutate
  Manage layout;
- targeted automatic tests are `passed`;
- native visual click-through remains `unverified` until actually performed.

After implementation reaches this state, do not start formal acceptance
automatically. Ask the developer whether to enter formal testing.
