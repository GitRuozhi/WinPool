# WinPool Unified Topology Layout Engine Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed and installed as the active Plan; amended
  2026-09-05; implementation not started; execution awaits the developer's
  explicit request
- **Created:** 2026-09-03
- **Updated:** 2026-09-04 (installed; units clarified as DIP; viewport
  isolation, mapper mapping, and XAML-migration deletion preconditions added);
  2026-09-05 (amendment after the reverted execution attempt: engine
  invariants promoted to §1.0; the single-weight distribution model of §2
  confirmed; §7.2/§7.8 ambiguity removed; capacity allocation fenced;
  stepped native verification mandated)
- **Baseline commit:** `563768efddbca17bdd6f831c11daaed573556ba3`
  (amendment baseline; original installation baseline was `0ef6d22`)
- **Working branch:** `main`
- **Current product version:** V0.45
- **Target product version:** V0.45
- **Stage type:** topology-layout refactor and Edit-page presentation
  refinement; simulation behavior remains unchanged

This Plan records the developer's current decisions for unifying the three
logical topology regions around the Manage-page layout behavior.

The Manage page is the baseline. The Edit upper and Edit lower regions are not
independent topology implementations. They are restricted projections of the
same topology model rendered through the same layout engine, with only the
explicit differences recorded below.

This Plan does **not** authorize real storage mutation, push, tag, GitHub
Release, binary upload, deployment, schema changes, IPC changes, or unrelated
product work.

The implemented Edit-page Plan was frozen under `docs/Archive/V0.45` with its
developer-accepted final state on 2026-09-04. The Archive index and matching
Chinese reading copies were updated in the same documentation work item.

No implementation begins until the developer explicitly requests execution of
this Plan.

Before any execution, read the execution pitfall record
[`Reference/20260905_统一拓扑布局引擎执行踩坑记录.md`](Reference/20260905_统一拓扑布局引擎执行踩坑记录.md).
A previous execution attempt (2026-09-04/05) was fully reverted to
`origin/main`; the record explains the failure and lists the plan amendments
and confirmations required before a new attempt.

### Amendment status against the pitfall-record prerequisites

The pitfall record §7 lists five prerequisites for a new attempt. This
amendment fulfills them as follows:

1. Unit-thought principles written as controlling invariants — §1.0 below.
2. §6.2/§6.8 disambiguation — §7.2 and §7.8 below.
3. §4.2 fence around capacity allocation — §2.4–§2.5 and §5 below.
4. Three algorithm boundaries confirmed by the developer — the distribution
   model, the no-wrap rule, and the insufficient-width rule were settled in
   the developer's 2026-09-05 design conversation and are recorded in §2 and
   §4. The exact compact-header arrangement remains a presentation detail to
   be confirmed with a screenshot during implementation (§6).
5. Stepped execution with per-step native verification — §11 below.

---

## 1. Controlling decisions

These decisions control this stage and outrank the existing Edit-page layout
implementation.

### 1.0 Engine invariants

These invariants state why the engine is designed the way it is. They are
normative: an implementation that satisfies every other section while breaking
one of these invariants fails this Plan.

1. **Structure decisions live only in the unit layer.** Row formation, column
   budgets, shrink decisions, row-height relaxation, and minimum unit widths —
   including the two-unit floor for complex pool siblings — are decided in
   integer Wunit/Hunit space. They are resolution-independent invariants of
   the layout plan.
2. **One root layout per surface.** Each surface (Manage, Edit upper, Edit
   lower) runs the engine exactly once per layout pass, at its root adapter,
   with that surface's own host width. Nested panels never invoke the engine
   and never re-plan an ancestor or sibling budget.
3. **Panels are mechanical consumers.** Panels obtain the width WinUI
   supplies, with a per-surface fallback when WinUI measures at infinity;
   convert the node ViewModel tree to engine input at the root; and perform
   Measure/Arrange using engine-assigned slot widths. Panels never decide how
   many children fit, equal-fill versus weighted-fill, capacity proportions,
   row packing, or shrink priority.
4. **Pixels only stretch.** Pixel values pixelize the finished unit plan.
   Nothing measured in pixels feeds back into a unit-layer decision.
5. **Layout purity and idempotence.** Only deterministic input data — static
   snapshot data and the width supplied by WinUI — may enter layout. No value
   derived from measuring child elements may influence a layout decision
   (re-entrant measurement is the known `LayoutCycleException` trigger).
   Identical inputs produce identical outputs; width changes smaller than
   1 DIP do not re-plan.

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
- `AdaptiveFlowPanel`, `WeightedPoolPanel`, or replacement panels may remain
  as WinUI measure/arrange adapters, but they must not implement competing
  layout algorithms.
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

Its projection continues to show the complete logical topology according to
the current `TopologyProjector`.

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

This stage may refactor the internal implementation used to produce that
result, but must not deliberately redesign the Manage topology.

Manage-layout regression tests are therefore the compatibility reference for
the unified engine.

---

## 2. Distribution model

This section is the developer-confirmed design (2026-09-05) for final
child-width allocation. It replaces any reading of "capacity allocation" as a
new engine mode.

### 2.1 One distribution rule

The engine owns final slot-width allocation through exactly one rule. Each
child in a row receives its natural minimum width plus a proportional share of
the row's remaining width:

```text
width_i = minimum_i + extra × weight_i / Σ weight
```

The weight is resolved per node (§2.2). There is no other weight source, no
distribution-mode enum, and no page-name branch in the engine.

### 2.2 Weight sources and fallback

- A node may declare an optional **distribution weight** as layout input
  metadata.
- When no weight is declared, the engine uses the node's measured
  **UnitWidth**.
- Manage compatibility: Manage nodes declare no weight. The UnitWidth
  fallback must reproduce the arithmetic currently performed by
  `WeightedPoolPanel.AllocateRows` exactly, so Manage output is unchanged and
  existing regression expectations remain valid.
- Edit-upper partition strips declare the capacity weight (§2.3).

### 2.3 Capacity weight conversion

For the Edit-upper partition strip only:

```text
weight_i = byte size of child i, as a double
```

- No normalization and no rounding of weights. `double` represents integers
  exactly below 2^53, larger than any real partition size, so the weight
  proportions equal the byte ratios exactly.
- If the weight field ends up integral, GCD reduction is permitted; `double`
  is the default choice and requires no conversion at all.
- Both partitions and visible unallocated regions use their actual byte size.
- Hidden gaps below the configured threshold are not projected, receive no
  layout slot, and carry no weight.

The conversion is a pure function of snapshot data and runs in the projection
layer, outside any layout pass.

### 2.4 Normative row allocation

For one row with `n` visible children, spacing `s` (6 DIP), and per-child
minimum widths `minimum_i` (leaf minimum 112 DIP):

```text
reserved = Σ minimum_i + s × (n − 1)
extra    = available width − reserved
```

- If `extra ≤ 0`: every child keeps `minimum_i`. No negative adjustment
  occurs; the strip overflows the viewport when necessary.
- If `extra > 0`: children `1 … n−1` receive
  `minimum_i + extra × weight_i / Σ weight`. Fractional DIP widths are
  allowed; an integer variant floors.
- The row's **last child deterministically absorbs the rounding residual**:

```text
width_n = available width − s × (n − 1) − Σ_{i<n} width_i, clamped ≥ minimum_n
```

- A zero-weight child stays at its minimum width.
- If `Σ weight ≤ 0` (no positive weights), distribute the extra equally by
  count instead.
- The absorbing child must remain "the row's last child"; do not switch the
  residual to the largest child, which would jump between recomputations.

### 2.5 Fence

Capacity weights are legal only on the Edit-upper leaf partition/unallocated
strips. The engine contains no byte or capacity concept. This stage may not
generalize capacity weighting to pools, tiers, or any other node, and may not
introduce a second distribution formula.

### 2.6 Resize behavior

Resizing is a sequence of independent single passes: width change → one
engine run → `ApplyLayout` → panels arrange by the new slots. The engine is a
pure function over immutable input records plus a width; it never touches a
UIElement and never measures a child to derive a decision. The `ApplyLayout`
write-back invalidates arrangement only and must not write any width-affecting
property. Width changes smaller than 1 DIP do not re-plan (idempotence guard,
§1.0 item 5).

Worked example (also the acceptance case, §14):

```text
Partition A = 100 GiB, Partition B = 200 GiB, remaining width = 333 DIP
A = 112 + 333 × 100/300 = 223 DIP
B = 112 + 333 × 200/300 = 334 DIP
```

---

## 3. Edit lower topology

### 3.1 Structure

The Edit lower topology uses the same pool-oriented structure and layout
behavior as Manage wherever no Edit-specific rule is listed below.

It shows internal pools only, including the primordial pool.

The existing Edit working-copy, drag, simulation Execute, create, modify, and
dissolve behavior is not redesigned by this stage.

### 3.2 Primordial pool

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

### 3.3 Existing pools

Non-primordial pools retain the current Edit lower logical content unless this
Plan explicitly changes it.

Existing tier, physical-disk, virtual-disk, and virtual-disk partition
representation remains available.

This Plan does not change pool-editing semantics or simulation operations.

### 3.4 Synthetic plus pool

A synthetic plus-pool remains the Edit lower affordance for adding a pool.

It is:

- visually a pool peer;
- logically synthetic;
- always placed after the real pools;
- therefore the rightmost pool when the pool row fits on one row;
- ordered after all real and draft pools when packing requires more than one
  row.

Conceptually:

```text
Primordial | Pool A | Pool B | +
```

The plus-pool continues to create only the existing local draft-pool working
state. It does not create real storage.

### 3.5 Layout

Edit lower uses the same weighted pool-layout rules as Manage unless an
Edit-lower rule above changes the projected children.

There is no separate Edit-lower packing algorithm.

---

## 4. Edit upper topology

### 4.1 Two levels only

The Edit upper topology contains exactly the disk/partition workspace needed
for partition management.

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

### 4.2 Eligible disks only

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

### 4.3 Disk arrangement

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
than relying on an unrelated page-specific layout algorithm (the current XAML
`ItemsPanel` StackPanel is replaced by the engine Stack channel).

### 4.4 Partition arrangement

Partitions and visible unallocated regions inside one disk are arranged in one
horizontal strip in disk-offset order.

Conceptually:

```text
Disk 0
[Partition 1][Unallocated][Partition 2][Partition 3]
```

The children of one disk do not wrap onto a second logical partition row.

The no-wrap rule (developer-confirmed 2026-09-05):

- the strip is always a single row;
- each visible child keeps at least its minimum width;
- when the minimum total width exceeds the available topology width, the row
  keeps the minimum widths and becomes horizontally wider than the viewport;
  the existing topology scroll surface handles the overflow (§2.4
  `extra ≤ 0` branch);
- the strip's target width is input data only; nothing measured during layout
  may become the target width.

This keeps the disk map spatially coherent instead of moving later partitions
onto an unrelated second row.

### 4.5 Unallocated regions

Existing Edit behavior for unallocated regions remains:

- leading gaps are separate nodes;
- interior gaps are separate nodes;
- trailing gaps are separate nodes;
- ordering follows disk offset;
- the configured partition-gap ignore threshold still determines which gaps
  are visible.

A visible unallocated node participates in width allocation using the size of
that exact gap as its capacity weight (§2.3).

Hidden gaps below the configured threshold do not receive a visible layout
slot.

---

## 5. Capacity-proportional partition widths

All widths in this Plan are WinUI device-independent units (DIP), not physical
pixels.

The allocation itself is defined normatively in §2. This section records its
scope and acceptance numbers.

### 5.1 Minimum width

Every visible partition or unallocated child starts with the topology leaf
minimum width: `112 DIP`.

This Plan does not change the Manage minimum width.

### 5.2 Scope

Capacity-proportional remaining-width allocation applies only to the Edit
upper disk's partition/unallocated row.

It does not replace Manage's existing weighted topology rules (§2.2 fallback)
and does not change Edit lower pool weighting.

The distribution is supplied as layout input (declared weights), never as a
separate panel or engine mode (§2.5).

### 5.3 Acceptance example

```text
Partition A = 100 GiB
Partition B = 200 GiB

minimum: A = 112 DIP, B = 112 DIP
remaining width = 333 DIP

A extra = 333 × 100 / 300 = 111 DIP
B extra = 333 × 200 / 300 = 222 DIP

final: A = 223 DIP, B = 334 DIP
```

The implementation may use floating-point layout widths. Any final rounding
must be deterministic, and the final child must absorb any residual rounding
difference so the planned row width remains consistent (§2.4).

### 5.4 No remaining width

If:

```text
available width ≤ minimum widths + spacing
```

no negative proportional adjustment occurs.

Each child keeps at least its minimum width and the horizontal strip overflows
the viewport when necessary.

---

## 6. Edit upper disk presentation

The visible Edit-upper disk node uses a compact horizontal header instead of
the standard multi-line topology card header.

The information currently spread over the normal disk card's approximately
three text lines is arranged horizontally where practical.

Conceptually:

```text
Disk 0    Samsung SSD ...    GPT    1.82 TiB
```

Exact separators and truncation are presentation details, to be confirmed with
a screenshot during implementation (pitfall-record §7 item 4). The result must:

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

## 7. Implementation mapping

This section maps the confirmed requirements to the current implementation. It
does not add product scope.

### 7.1 Application layout engine

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
- final child width assignment through the single distribution rule (§2.1);
- extra-space distribution by declared weight with UnitWidth fallback (§2.2);
- no-wrap horizontal strip behavior required by Edit upper (§4.4).

Existing Manage calculations remain the baseline; the unit-planning phase is
not redesigned, and the UnitWidth fallback must reproduce the current
`WeightedPoolPanel.AllocateRows` arithmetic exactly.

If required, add minimal layout-input metadata for:

- the optional distribution weight (§2.2);
- wrap versus no-wrap child rows.

Do not encode page names into `TopologyLayoutEngine`.

### 7.2 Layout result

The engine result must carry the final row membership and the final allocated
width of each child.

"Final allocated width" means the slot widths pixelized from the single
root-level layout result of that surface (§1.0 item 2). It does **not** mean
widths computed independently by each nested panel.

Do not leave final weighted or capacity-based stretch calculation in a specific
WinUI panel.

The final result may extend the existing `TopologyLayoutResult` with explicit
row-slot width information or an equivalent representation.

The exact data type is an implementation choice; the ownership boundary is not.

### 7.3 WinUI topology panels

Current relevant controls include:

- `AdaptiveFlowPanel`
- `WeightedPoolPanel`
- `TopologyNodeControl`

After this stage, any retained layout panels are thin adapters around
`TopologyLayoutEngine`.

Each surface has one root adapter that calls the engine once with the
surface's host width. Root adapters may:

- obtain the actual available WinUI width;
- convert the node ViewModel tree to engine input at the root;
- call the engine;
- measure children using engine-assigned widths;
- arrange children according to engine rows and slots.

Nested panels below the root adapter only consume the stored slots.

They may not:

- call `TopologyLayoutEngine`;
- walk the visual tree to find an owner and re-plan;
- decide how many children fit;
- choose equal-fill versus weighted-fill;
- choose capacity proportions;
- choose row packing policy;
- choose shrink priority.

If a single common topology panel cleanly replaces both `AdaptiveFlowPanel`
and `WeightedPoolPanel`, that replacement is allowed.

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

### 7.4 Manage projection

Primary owner:

`src/WinPool.Application/Topology.cs`

Do not redesign `TopologyProjector.Project`.

Any changes required to supply the unified engine with layout metadata must
preserve the projected Manage topology and current ordering.

### 7.5 Edit projections

Primary owner:

`src/WinPool.Application/EditWorkspace.cs`

#### Upper

`ProjectPartitionWorkspace` remains responsible for selecting partitionable
disks and building visible disk, partition, and unallocated nodes.

Change its layout metadata so the unified engine receives:

- a vertical disk collection (Stack);
- one no-wrap horizontal partition strip per disk;
- the actual partition/gap byte size as the declared distribution weight
  (§2.3).

The only arithmetic permitted in `EditWorkspace` is assigning
`weight = byte size`; all distribution arithmetic belongs to the engine.

#### Lower

`ProjectPoolWorkspace` / `ProjectPoolWorkspaceRoot` remain responsible for the
Edit-lower pool subset and the synthetic plus-pool.

Ensure:

- primordial physical disks have no partition children in the lower projection
  (already true in the current implementation; keep and cover with tests);
- real pools retain the intended existing content;
- the plus-pool is ordered last (already true; keep and cover with tests);
- the tree uses the same weighted layout semantics consumed by Manage.

### 7.6 ViewModel conversion

`TopologyNodeViewModel` remains the shared presentation model.

The mapping from application layout nodes to ViewModels must preserve the
layout metadata needed by the unified engine, including the optional
distribution weight.

The current App-side conversion lives in
`src/WinPool.App/Services/TopologyLayoutMapper.cs`; updating that mapping to
carry engine layout metadata is part of this stage.

`TopologyLayoutInput` gains the optional distribution weight; absent weight
falls back to UnitWidth inside the engine.

Do not create separate Manage/Edit topology ViewModel hierarchies.

### 7.7 Edit upper compact header

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

### 7.8 Viewport ownership

"Surface" means exactly three regions: the Manage topology, the Edit upper
topology, and the Edit lower topology. Nested panels inside a surface are not
surfaces.

Edit upper and Edit lower must not overwrite a single shared global topology
viewport width that can affect Manage layout.

Prefer the actual width supplied to the WinUI layout panel during
`MeasureOverride` / `ArrangeOverride`.

Fallback viewport state may remain where WinUI infinite-width measurement
requires it, but each surface must use its own host width and its own
fallback value.

Edit upper, Edit lower, and Manage therefore must not race to set one shared
`TopologyViewportWidth`; the shared state is removed and replaced by
per-surface state, with the automatic viewport-isolation regression test.

---

## 8. Work items after approval

Do not start these until the developer explicitly requests execution.

Execute one item at a time, in order. Each item completes only after its
§11 verification. Do not batch multiple items before verification.

| Id | Work |
| --- | --- |
| TL1 | Refactor `TopologyLayoutEngine`: engine-owned final row and slot-width allocation through the single distribution rule (§2), UnitWidth fallback reproducing current Manage arithmetic |
| TL2 | Route all three surfaces through one root adapter each; nested panels consume stored slots; preserve Manage output |
| TL3 | Remove Edit-page dependence on the shared Manage viewport-width state; per-surface host width and fallback; automatic viewport-isolation regression test |
| TL4 | Refine Edit-upper projection: eligible disks only, two visible levels, vertical disks through the engine Stack channel, no-wrap horizontal partition strips |
| TL5 | Add Edit-upper capacity weights (declared byte-size weights, §2.3) and no-wrap allocation (§2.4) |
| TL6 | Cover Edit-lower projection with tests: primordial disks without partitions, plus-pool ordered last (behavior already present) |
| TL7 | Add compact horizontal Edit-upper disk presentation in the shared node control; confirm exact form with a screenshot |
| TL8 | Add targeted layout, projection, distribution, and architecture regression tests |
| TL9 | After developer acceptance of implementation, record the important final result in CHANGELOG; version remains V0.45 |

TL0 (documentation rollover: archive the previous Plan, install this Plan and
its Chinese reading copy) was completed 2026-09-04; this 2026-09-05 amendment
is a separate documentation work item.

Commit split after execution:

1. documentation (this amendment, already a separate work item);
2. equivalent layout-engine refactor (TL1–TL2);
3. Edit projection / layout behavior (TL3–TL6);
4. compact visual presentation (TL7);
5. final accepted documentation result if required (TL8 evidence belongs with
   its items; TL9 is its own documentation commit).

This repository remains direct-to-`main`; do not create a feature branch or PR.

Do not push unless the developer explicitly asks.

---

## 9. Required targeted tests

This is ordinary feature/refactor work, not an automatic full-quality-gate
run.

Run the smallest directly related automatic tests during implementation.

### 9.1 Unified layout engine

Extend the existing `TopologyLayoutEngineTests`.

Required cases include:

- current Manage reference cases continue to produce equivalent row packing;
- current Manage weighted siblings retain their existing width behavior
  (UnitWidth fallback is bit-compatible with the current panel arithmetic);
- Stack children remain vertical;
- Flow behavior still respects existing Manage expectations;
- WeightedFlow behavior still respects current Manage expectations;
- no-wrap horizontal strip does not create a second row;
- when the no-wrap minimum width exceeds the viewport, minimum widths are
  retained and no negative adjustment occurs;
- declared-weight distribution follows `min + extra × w/Σw` exactly, with the
  last child absorbing the rounding residual;
- zero-weight children stay at minimum; non-positive total weight falls back
  to equal split;
- entering or leaving Edit, or resizing either Edit half, does not alter
  Manage layout inputs (shared-viewport isolation).

### 9.2 Distribution acceptance cases

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

A hidden unallocated gap below the configured threshold receives no layout
slot and no visible capacity share.

### 9.3 Edit upper projection

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

### 9.4 Edit lower projection

Tests must prove:

- primordial pool is present;
- primordial physical disks have no partition children in Edit lower;
- non-primordial pool structure remains available;
- the synthetic plus-pool is present exactly once;
- the synthetic plus-pool is ordered after all real/draft pools.

### 9.5 Manage non-regression

Existing Manage topology and layout tests must pass without rewriting their
expected behavior merely to accommodate the refactor.

A failing Manage expectation must be treated as a regression unless the
developer explicitly changes the requirement.

### 9.6 Presentation structure

Add the smallest architecture/presentation test practical for:

- Edit upper selecting the compact disk presentation mode;
- Manage remaining on the standard presentation mode;
- Edit lower remaining on the standard presentation mode;
- all three still using the shared `TopologyNodeControl`.

Do not claim visual appearance passed from an automatic structure test.

---

## 10. Manual verification after implementation

Native visual evidence remains separate from automatic tests.

A local simulation-only Edit click-through should check:

1. Manage topology before and after entering Edit has equivalent packing.
2. Resize Manage and confirm existing topology behavior is unchanged.
3. Resize Edit upper and confirm Edit lower/Manage layout does not change
   because of a shared viewport variable.
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
13. Existing Edit drag, draft, Execute, modify, and dissolve simulation
    behavior still works.
14. Dragging the Edit splitter continuously produces no crash, no layout
    flicker loop, and stable idle CPU after release (§2.6).

Until a human/native run is actually performed, this evidence is
`unverified`.

A full Quality gate is `not_required` for ordinary implementation of this Plan
unless the developer explicitly requests formal testing.

---

## 11. Execution and verification discipline

Mandated by the pitfall record §7 item 5 and the developer's 2026-09-05
instructions.

- **One TL item per step.** Build, run the targeted automatic tests, and
  natively verify before starting the next item. Native verification means:
  launch the application, open the affected page, confirm the process stays
  alive, and confirm no new crash-log entries. Batching several items into one
  final verification is prohibited.
- **Managed tests never claim a UI-behavior fix.** Only a native run may.
- **If a requirement cannot be met, stop and report.** Substituting a
  different behavior (for example a fixed width constant replacing the real
  no-wrap width) is prohibited.
- **Interactive questions without a received answer are not consent.** Re-ask
  in plain text before deciding.
- **After a crash**, read the crash log and Windows event log (1000/1026)
  before changing code; prefer instrumentation over guessing; report the root
  cause and wait for approval before fixing.
- **Scripted code edits** must be confirmed to compile before any launch used
  for verification.

---

## 12. Out of scope

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

## 13. Safety

The existing deny-by-default execution boundary remains unchanged.

- All storage-structure changes in this stage remain simulation-only.
- Real disk, partition, volume, pool, tier, or virtual-disk mutation is not
  authorized.
- UAC elevation or Real mode is not authorization.
- Hardware data remains behind the existing redaction boundary.
- Inventory PowerShell remains embedded and read-only.
- No real hardware mutation is an accepted test method under this Plan.

---

## 14. Acceptance gate

Implementation is complete only when:

- all three topology regions use `TopologyLayoutEngine` as the single layout
  algorithm authority;
- each surface runs exactly one root-level engine layout; no nested panel
  invokes the engine or re-plans a budget;
- the engine has exactly one distribution rule with declared-weight fallback
  to UnitWidth, and contains no byte or capacity concept;
- no Edit-specific panel independently performs equal-fill, weighted-fill, or
  capacity-fill layout decisions;
- Manage behavior remains equivalent;
- Edit lower primordial disks do not display partitions;
- Edit lower plus-pool is the final pool item;
- Edit upper contains only eligible disk → partition/unallocated topology;
- Edit upper disks are vertically stacked through the engine Stack channel;
- partitions inside one disk form one horizontal no-wrap strip;
- spare horizontal width is distributed by declared capacity weights;
- the 100 GiB / 200 GiB / 333 DIP example produces 223 DIP / 334 DIP;
- the row's last child deterministically absorbs the rounding residual;
- when remaining width is insufficient, children keep minimum widths and the
  strip overflows the viewport without negative adjustment;
- Edit upper disk cards use the compact horizontal presentation;
- shared viewport state no longer allows Edit upper/lower resizing to mutate
  Manage layout;
- targeted automatic tests are `passed`;
- every implemented TL item has its native verification evidence recorded;
- native visual click-through remains `unverified` until actually performed.

After implementation reaches this state, do not start formal acceptance
automatically. Ask the developer whether to enter formal testing.
