# WinPool Unified Topology Layout Engine Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed and installed as the active Plan; amended
  2026-09-05; implementation not started; execution awaits the developer's
  explicit request
- **Created:** 2026-09-03
- **Updated:** 2026-09-04 (installed; units clarified as DIP; viewport
  isolation, mapper mapping, and XAML-migration deletion preconditions added);
  2026-09-05 (first amendment: engine invariants promoted to §1.0;
  single-weight distribution model; §7.2/§7.8 disambiguation; capacity
  fence; stepped native verification);
  2026-09-05 (second amendment: distribution made opt-in with the current
  Manage behavior as the unchanged default; three-stage Edit-upper strip
  growth — equal minimum, equal growth to the no-wrap width, then capacity
  distribution; all layout thresholds as named variables; width-adaptive
  compact disk header)
- **Baseline commit:** `563768efddbca17bdd6f831c11daaed573556ba3`
  (implementation baseline; the original installation baseline was `0ef6d22`)
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

The pitfall record §7 lists five prerequisites for a new attempt. The
amendments fulfill them as follows:

1. Unit-thought principles written as controlling invariants — §1.0.
2. §6.2/§6.8 disambiguation — §7.2 and §7.8.
3. §4.2 fence around capacity allocation — §2.4–§2.5 and §5.
4. Algorithm boundaries confirmed by the developer — the insufficient-width
   rule is stage 1 of §2.4; the no-wrap rule is §4.4 with the W2 boundary of
   §2.4; the compact header is width-adaptive (§6). The threshold values
   (equal-growth `t`, header single-line width) are confirmed with a
   screenshot during implementation.
5. Stepped execution with per-step native verification — §11.

The second amendment (2026-09-05) records four further developer decisions:

6. Same-row width differences are opt-in; the default is the current Manage
   behavior, unchanged — §2.1.
7. Edit-upper strips grow in three stages: equal minimum widths; equal
   growth to the no-wrap width; then capacity distribution — §2.4.
8. All layout thresholds are named variables; existing inline literals are
   listed for elimination — §2.7.
9. The Edit-upper disk header collapses to one line only when the assigned
   width is sufficient; the standard multi-line presentation remains
   otherwise — §6.

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

This section records the developer-confirmed design for final child-width
allocation (2026-09-05 conversations). It replaces any reading of "capacity
allocation" as a new universal or default engine mode.

### 2.1 Opt-in, not default

Same-row width differences through distribution metadata are **optional**.
The default, when a node declares no distribution metadata, is the current
Manage-page behavior, preserved bit-exactly for every layout kind:

- Flow children keep the current equal fill (equal widths within a row).
- WeightedFlow rows keep the current unit-proportional stretch
  (`WeightedPoolPanel.AllocateRows` arithmetic, unchanged).
- Stack children are unaffected.
- No byte-capacity weight exists anywhere by default.

Developer clarification recorded 2026-09-05: within today's Manage behavior,
Flow children are equal-width while WeightedFlow pool rows stretch in
proportion to unit widths. Both are "current behavior"; neither changes in
this stage. Making pool rows strictly equal-width would be a Manage behavior
change outside this Plan.

The engine performs declared-weight distribution only where distribution
metadata is present (§2.2–§2.3), and only on the Edit-upper leaf strips
(§2.5).

### 2.2 Weight sources

- A node may declare an optional **capacity weight** as layout input metadata.
- Declared weights activate the three-stage allocation (§2.4) for that strip.
- No other weight source exists. There is no distribution-mode enum and no
  page-name branch in the engine.
- Manage compatibility: Manage declares nothing; its rows render exactly as
  today, so existing regression expectations remain valid.

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

### 2.4 Three-stage strip allocation (normative)

For one opt-in strip row with `n` visible children, spacing `s` (6 DIP), leaf
minimum `m` (112 DIP), equal-growth threshold `t` (a named variable, §2.7),
and declared weights `w_i`:

```text
W1 = n × m + s × (n − 1)     row minimum width
W2 = n × t + s × (n − 1)     row no-wrap width (equal-growth boundary)
```

**Stage 1 — `available ≤ W1` (insufficient width):**

```text
width_i = m
```

Every child keeps the minimum width. No negative adjustment occurs; the strip
overflows the viewport (§4.4).

**Stage 2 — `W1 < available ≤ W2` (equal growth):**

all children grow together by the same absolute amount:

```text
g = (available − W1) / n
width_i = m + g
```

With equal minimums, every child in the row has the same width.

**Stage 3 — `available > W2` (capacity distribution):**

every child first reaches `t`, then the remaining width is distributed by
capacity:

```text
width_i = t + (available − W2) × w_i / Σw        for children 1 … n−1
width_n = available − s × (n − 1) − Σ_{i<n} width_i, clamped ≥ t
```

Required properties:

- **Continuity:** at `available = W1` stages 1→2 agree (`g = 0`); at
  `available = W2` stages 2→3 agree (every child equals `t`). Crossing a
  threshold causes no visual jump.
- The row's **last child deterministically absorbs the rounding residual** in
  stages 2 and 3.
- Zero-weight children stop at `m` (stages 1–2) or `t` (stage 3).
- If `Σw ≤ 0`, stage 3 distributes the remaining width equally by count.
- `t` is input data (a named constant, §2.7), never a value measured during
  layout. Labels may still wrap visually inside a tile; exact label fit is a
  presentation concern confirmed with a screenshot, not a layout input.

Worked example (indicative, assuming `t = 200 DIP`; recompute with the
confirmed value):

```text
n = 2, m = 112, s = 6, t = 200, weights 100 GiB : 200 GiB
W1 = 230, W2 = 406

available = 320  → stage 2: both children 112 + 45 = 157 DIP
available = 563  → stage 3: A = 200 + 157 × 100/300 = 252.33 DIP
                              B = 563 − 6 − 252.33   = 304.67 DIP
```

### 2.5 Fence

Declared-capacity distribution is legal only on the Edit-upper leaf
partition/unallocated strips. The engine contains no byte or capacity
concept. This stage may not generalize capacity weighting to pools, tiers, or
any other node, may not introduce a second distribution formula, and may not
make same-row width differences the default anywhere.

### 2.6 Resize behavior

Resizing is a sequence of independent single passes: width change → one
engine run → `ApplyLayout` → panels arrange by the new slots. The engine is a
pure function over immutable input records plus a width; it never touches a
UIElement and never measures a child to derive a decision. The `ApplyLayout`
write-back invalidates arrangement only and must not write any width-affecting
property. Width changes smaller than 1 DIP do not re-plan (idempotence guard,
§1.0 item 5). Stage transitions are continuous (§2.4), so dragging across
`W1`/`W2` produces no jump.

### 2.7 Named threshold variables

All layout thresholds are named variables with exactly one owner. Panels and
pages contain no width literals.

| Variable | Owner | Value |
| --- | --- | --- |
| Leaf minimum `m` | `TopologyLayoutEngine` | 112 DIP (unchanged) |
| Sibling spacing `s` | `TopologyLayoutEngine` | 6 DIP (unchanged) |
| Ancestor chrome | `TopologyLayoutEngine` | 26 DIP (unchanged) |
| Equal-growth threshold `t` | `TopologyLayoutEngine` | confirmed at implementation with a screenshot; indicative 200 DIP |
| Compact-header single-line threshold | `TopologyNodeControl` presentation | confirmed at implementation with a screenshot |
| Per-surface fallback viewport width | each surface's own state | named per surface; replaces the inline 1400 |

Current inline literals this stage eliminates: `1400` (panel fallback and
ViewModel default viewport), `320` and `20` (page `SizeChanged` handlers),
`150` (default parameters of `EqualFillFlowLayout` / `WeightedPoolLayout`).
They become named variables or disappear with their owners. The existing
engine constants (112/6/26) do not change value in this stage.

Named variables are code constants with a single owner, not user-facing
settings; settings remain out of scope (§12).

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
- when the available width is at or below the row minimum `W1`, children keep
  minimum widths and the row becomes horizontally wider than the viewport; the
  existing topology scroll surface handles the overflow (stage 1, §2.4);
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

## 5. Edit-upper strip width allocation

All widths in this Plan are WinUI device-independent units (DIP), not physical
pixels.

The allocation itself is defined normatively in §2.4. This section records its
scope and acceptance cases.

### 5.1 Minimum width

Every visible partition or unallocated child starts with the topology leaf
minimum width: `112 DIP`.

This Plan does not change the Manage minimum width.

### 5.2 Scope

The three-stage allocation (§2.4) applies only to the Edit upper disk's
partition/unallocated strips, and only because those strips declare capacity
weights.

It does not replace Manage's existing layout rules (§2.1) and does not change
Edit lower pool weighting.

### 5.3 Acceptance cases

Values below assume the indicative `t = 200 DIP`; recompute with the confirmed
value. `m = 112`, `s = 6`.

#### Case A1 — stage 1 (insufficient width)

```text
n = 2, available = 200 (< W1 = 230)
expected: both children 112 DIP; row overflows the viewport
```

#### Case A2 — stage 2 (equal growth)

```text
n = 2, available = 320, W1 = 230, W2 = 406
expected: both children 112 + 45 = 157 DIP (equal widths)
```

#### Case A3 — stage 3 (capacity distribution)

```text
n = 2, weights 100 GiB : 200 GiB, available = 563, W2 = 406
expected: A = 200 + 157 × 100/300 = 252.33 DIP
          B = 563 − 6 − 252.33   = 304.67 DIP (absorbs residual)
```

#### Case A4 — continuity

At `available = W2` both stage 2 and stage 3 formulas produce `t` for every
child.

#### Case B — three children including unallocated (stage 3)

```text
100 GiB partition, 50 GiB unallocated, 200 GiB partition
```

The width above `t` is distributed in the ratio `100 : 50 : 200`; the last
child absorbs the residual.

#### Case C — hidden gap

A hidden unallocated gap below the configured threshold receives no layout
slot and no visible capacity share.

### 5.4 Determinism

Any final rounding is deterministic; the row's last child absorbs residual
differences so the planned row width remains consistent (§2.4).

---

## 6. Edit upper disk presentation (width-adaptive)

The Edit-upper disk header is **width-adaptive**:

- when the disk node's engine-assigned width reaches the named single-line
  threshold (§2.7), the approximately three text lines of the normal disk
  card merge into one compact horizontal line;
- when narrower, the current standard multi-line presentation remains.

Conceptually (wide case):

```text
Disk 0    Samsung SSD ...    GPT    1.82 TiB
```

The result must:

- be driven by the engine-assigned slot width (deterministic render-time
  input), not by measured text;
- use a named threshold variable (§2.7), not a literal;
- preserve the information required to identify the disk;
- preserve accessibility names;
- preserve selection and interaction behavior;
- remain the shared `TopologyNodeControl` with an explicit presentation mode;
  do not create a second disk-control tree.

Manage topology cards remain the reference presentation and are not changed by
this requirement.

Edit lower cards remain on the normal topology presentation unless separately
specified.

The exact single-line arrangement, the threshold value, and the equal-growth
`t` are confirmed together with a screenshot during implementation.

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
- final child width assignment, preserving current default behavior (§2.1)
  and implementing the three-stage declared-strip allocation (§2.4);
- no-wrap horizontal strip behavior required by Edit upper (§4.4);
- the named threshold variables (§2.7).

Existing Manage calculations remain the baseline; the unit-planning phase is
not redesigned, and default-row output must remain bit-identical to today.

If required, add minimal layout-input metadata for:

- the optional capacity weight (§2.2–§2.3);
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
- choose shrink priority;
- contain width literals (§2.7).

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
- the actual partition/gap byte size as the declared capacity weight
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
layout metadata needed by the unified engine, including the optional capacity
weight.

The current App-side conversion lives in
`src/WinPool.App/Services/TopologyLayoutMapper.cs`; updating that mapping to
carry engine layout metadata is part of this stage.

`TopologyLayoutInput` gains the optional capacity weight; absent weight keeps
the current default behavior inside the engine (§2.1).

Do not create separate Manage/Edit topology ViewModel hierarchies.

### 7.7 Edit upper adaptive header

Primary owners:

- `src/WinPool.App/Controls/TopologyNodeControl.xaml`
- `src/WinPool.App/Controls/TopologyNodeControl.xaml.cs`

Add the smallest explicit presentation mode necessary for the single-line
Edit-upper disk header, switching on the engine-assigned width against the
named threshold (§6, §2.7).

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
requires it, but each surface must use its own host width and its own named
fallback value (§2.7); the inline `1400` disappears.

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
| TL1 | Refactor `TopologyLayoutEngine`: engine-owned final row and slot-width allocation preserving current default behavior bit-exactly; three-stage declared-strip allocation (§2.4); named threshold variables (§2.7) |
| TL2 | Route all three surfaces through one root adapter each; nested panels consume stored slots; preserve Manage output |
| TL3 | Remove Edit-page dependence on the shared Manage viewport-width state; per-surface host width and named fallback; eliminate inline width literals (§2.7); automatic viewport-isolation regression test |
| TL4 | Refine Edit-upper projection: eligible disks only, two visible levels, vertical disks through the engine Stack channel, no-wrap horizontal partition strips |
| TL5 | Add Edit-upper declared capacity weights (§2.3) activating the three-stage allocation (§2.4) |
| TL6 | Cover Edit-lower projection with tests: primordial disks without partitions, plus-pool ordered last (behavior already present) |
| TL7 | Add the width-adaptive Edit-upper disk header in the shared node control; confirm the single-line arrangement, header threshold, and `t` with a screenshot |
| TL8 | Add targeted layout, projection, distribution, and architecture regression tests |
| TL9 | After developer acceptance of implementation, record the important final result in CHANGELOG; version remains V0.45 |

TL0 (documentation rollover) was completed 2026-09-04; the 2026-09-05
amendments are separate documentation work items.

Commit split after execution:

1. documentation (amendments, already separate work items);
2. equivalent layout-engine refactor (TL1–TL2);
3. Edit projection / layout behavior (TL3–TL6);
4. adaptive visual presentation (TL7);
5. final accepted documentation result if required.

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
  (default rows are bit-compatible with today's panel arithmetic);
- rows without declared metadata produce **no** weight-based width differences
  (§2.1);
- Stack children remain vertical;
- Flow behavior still respects existing Manage expectations (equal fill);
- WeightedFlow behavior still respects current Manage expectations;
- no-wrap horizontal strip does not create a second row;
- stage 1 keeps minimum widths with no negative adjustment when available
  width is at or below `W1`;
- stage 2 grows all children by the same absolute amount;
- stage 3 distributes width above `t` by declared weight, with the last child
  absorbing the rounding residual;
- continuity: at `available = W1` and `available = W2` adjacent stages agree;
- zero-weight children stop at `m` (stages 1–2) or `t` (stage 3);
  non-positive total weight falls back to equal split in stage 3;
- entering or leaving Edit, or resizing either Edit half, does not alter
  Manage layout inputs (shared-viewport isolation).

### 9.2 Distribution acceptance cases

The deterministic cases of §5.3 (A1, A2, A3, A4, B, C), recomputed with the
confirmed `t` value.

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

- Edit upper selecting the single-line presentation when the assigned width
  reaches the named threshold, and the standard presentation when narrower;
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
7. Widening Edit upper shows the three stages in order: all children at equal
   minimum width; equal growth; then capacity-proportional differences, with
   no visual jump at the stage boundaries.
8. Unallocated space participates proportionally in stage 3.
9. Many partitions cause horizontal overflow rather than a second partition
   row.
10. Edit-upper disk headers collapse to one line when wide enough and expand
    to the standard presentation when narrower.
11. Edit lower primordial disks do not expose partitions.
12. Edit lower plus-pool appears after the real pools.
13. Existing Edit drag, draft, Execute, modify, and dissolve simulation
    behavior still works.
14. Dragging the Edit splitter continuously produces no crash, no layout
    flicker loop, and stable idle CPU after release (§2.6).
15. The screenshot confirming the single-line header, the header threshold,
    and `t` is recorded (TL7).

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
- making pool rows equal-width or otherwise changing Manage distribution;
- capacity-proportional pool widths;
- changing storage-pool simulation semantics;
- changing partition operation semantics;
- new partition eligibility rules;
- new storage operations;
- real storage mutation;
- database schema changes;
- IPC protocol changes;
- new persistence;
- new settings other than preserving the existing unallocated-gap setting
  (named threshold variables are code constants, not settings);
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
- rows without declared distribution metadata render exactly as today; no
  weight-based width differences appear anywhere by default;
- the engine implements the three-stage declared-strip allocation (§2.4) with
  continuity at `W1` and `W2`;
- stage 1 keeps minimum widths and overflows without negative adjustment;
  stage 2 grows all children equally; stage 3 distributes width above `t` by
  declared capacity weights, with the last child absorbing the rounding
  residual;
- no Edit-specific panel independently performs equal-fill, weighted-fill, or
  capacity-fill layout decisions;
- Manage behavior remains equivalent;
- Edit lower primordial disks do not display partitions;
- Edit lower plus-pool is the final pool item;
- Edit upper contains only eligible disk → partition/unallocated topology;
- Edit upper disks are vertically stacked through the engine Stack channel;
- partitions inside one disk form one horizontal no-wrap strip;
- the equal-growth threshold `t` and the header single-line threshold are
  named variables with confirmed values;
- no width literals remain in panels or pages (§2.7);
- Edit upper disk cards use the single-line presentation when their assigned
  width reaches the named threshold, and the standard presentation when
  narrower;
- shared viewport state no longer allows Edit upper/lower resizing to mutate
  Manage layout;
- targeted automatic tests are `passed`;
- every implemented TL item has its native verification evidence recorded;
- native visual click-through remains `unverified` until actually performed.

After implementation reaches this state, do not start formal acceptance
automatically. Ask the developer whether to enter formal testing.
