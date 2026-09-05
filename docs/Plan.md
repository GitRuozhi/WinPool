# WinPool Unified Topology Layout Engine Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed and installed as the active Plan; amended
  2026-09-05; implementation not started; execution awaits the developer's
  explicit request
- **Created:** 2026-09-03
- **Updated:** 2026-09-04 (installed; units clarified as DIP; viewport
  isolation, mapper mapping, and XAML-migration deletion preconditions added);
  2026-09-05 (first amendment: engine invariants §1.0; single-weight
  distribution model; §7.2/§7.8-era disambiguation; capacity fence; stepped
  native verification);
  2026-09-05 (second amendment: opt-in distribution with current Manage
  behavior as default; three-stage Edit-upper strip growth; named threshold
  variables; width-adaptive compact disk header);
  2026-09-05 (third amendment: "equal width" clarified as minimal-child equal
  width; width-adaptive presentation generalized to all storage-object
  levels; constants allowed, magic numbers not; three-stage rule gated on
  explicit developer confirmation; distribution requires an explicit enable
  option; plus-pool single-draft rule; Edit-lower structure-modifiability
  indicator and guardrails);
  2026-09-05 (fourth amendment: Edit-lower projection aligned with Manage —
  tier cards render only from snapshot tiers with members; tier-uncovered
  pool members gain the Manage-equivalent Unallocated group)
- **Baseline commit:** `563768efddbca17bdd6f831c11daaed573556ba3`
  (implementation baseline; the original installation baseline was `0ef6d22`)
- **Working branch:** `main`
- **Current product version:** V0.45
- **Target product version:** V0.45
- **Stage type:** topology-layout refactor and Edit-page presentation
  refinement, plus Edit-lower modifiability indication; simulation behavior
  remains simulation-only

This Plan records the developer's current decisions for unifying the three
logical topology regions around the Manage-page layout behavior, and for the
Edit-page presentation and guardrail rules recorded below.

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

### Amendment status and open confirmations

Fulfilled prerequisites (pitfall record §7):

1. Unit-thought principles written as controlling invariants — §1.0.
2. Old §6.2/§6.8 disambiguation — §8.2 and §8.9.
3. Capacity-allocation fence — §2.4–§2.5 and §5.
4. Stepped execution with per-step native verification — §12.

Third-amendment decisions (2026-09-05):

5. "Equal width" means minimal child objects are equal-width — the current
   engine logic; no Manage change — §2.1.
6. Distribution requires an explicit enable option; capacity data alone never
   activates weights — §2.2.
7. Named constants with hardcoded values are allowed; magic numbers are not —
   §2.7.
8. The width-adaptive (single-line collapse) presentation is a capability of
   storage-object nodes at every level, enabled per level; this stage enables
   it only for the Edit-upper disk level — §6.
9. Plus-pool single-draft rule — §3.4.
10. Edit-lower structure-modifiability indicator and guardrails — §7.

Fourth-amendment decisions (2026-09-05):

11. Edit-lower tier cards are snapshot-driven like Manage; no fixed
    performance/capacity placeholder cards; a draft pool's pre-created tier
    records stay parameter carriers and render only once they hold member
    disks — §3.3.
12. Pool members not covered by any tier render in the Manage-equivalent
    Unallocated group, restoring visibility, selection, and drag — §3.3.
    The fixed three-tier calls and the missing direct-member branch were the
    verified projection gaps behind "visible in Manage, missing in Edit
    lower".

**Open confirmation required before coding (not yet given):**

- The three-stage allocation rule (§2.4): the developer has not yet confirmed
  understanding and agreement. TL5 must not start until the developer
  explicitly confirms §2.4 (the worked "what you see" table is provided
  there for that purpose).
- The single-line header arrangement, header threshold, and equal-growth `t`
  values — confirmed with a screenshot during implementation (§6, §2.7).

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

Existing Manage rules for system root, pools, tiers, physical disks, virtual
disks, partitions, network and other groups, Stack / Flow / WeightedFlow
relationships, sibling packing, row-height relaxation, topology selection,
expansion, and minimum node width remain the reference behavior.

This stage may refactor the internal implementation used to produce that
result, but must not deliberately redesign the Manage topology.

Manage-layout regression tests are therefore the compatibility reference for
the unified engine.

---

## 2. Distribution model

This section records the developer-confirmed design for final child-width
allocation. It replaces any reading of "capacity allocation" as a new
universal or default engine mode.

### 2.1 Opt-in, not default

Same-row width differences through distribution metadata are **optional**.
The default, when a strip declares no distribution option, is the current
Manage-page behavior, preserved bit-exactly for every layout kind:

- Flow children keep the current equal fill; minimal child objects are
  equal-width (the developer's "equal width" statement, 2026-09-05, refers to
  exactly this and matches current engine logic).
- WeightedFlow rows keep the current unit-proportional stretch
  (`WeightedPoolPanel.AllocateRows` arithmetic, unchanged).
- Stack children are unaffected.
- No byte-capacity weight affects anything by default.

The engine performs declared-weight distribution only where the strip both
enables it and declares weights (§2.2), and only on the Edit-upper leaf strips
(§2.5).

### 2.2 Explicit enable, explicit weights

- Layout input may carry, per strip: an explicit **capacity-distribution
  enable option**, and per-child **declared capacity weights** (§2.3).
- Distribution activates only when the enable option **and** the weights are
  both present. Declared weights alone — or the mere existence of capacity
  data on a storage object — never activates distribution.
- The projection layer controls both fields. The engine contains no page-name
  logic, no distribution-mode enum, and no automatic inference from capacity
  data.
- Manage compatibility: Manage declares nothing; its rows render exactly as
  today, so existing regression expectations remain valid.

### 2.3 Capacity weight conversion

For an enabled Edit-upper partition strip:

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

### 2.4 Three-stage strip allocation

> **Confirmation gate:** the developer has not yet confirmed this section.
> TL5 must not start until the developer explicitly confirms it. The "what
> you see" table below exists for that confirmation.

Plain-language behavior (this is exactly the developer's 2026-09-05
description, made precise):

1. When narrow, every child sits at the same minimum width (112 DIP); the row
   may overflow the viewport.
2. As width grows, **all children grow together by the same amount**, so they
   stay equal-width, until each reaches the comfortable width `t`.
3. Beyond that, further width is distributed **by capacity**: larger
   partitions start growing faster than smaller ones.

Definitions for one enabled strip row with `n` visible children, spacing `s`
(6 DIP), leaf minimum `m` (112 DIP), equal-growth threshold `t` (§2.7), and
declared weights `w_i`:

```text
W1 = n × m + s × (n − 1)     row minimum width
W2 = n × t + s × (n − 1)     row equal-growth boundary
```

**Stage 1 — `available ≤ W1`:** `width_i = m`. No negative adjustment; the
strip overflows the viewport (§4.4).

**Stage 2 — `W1 < available ≤ W2`:**

```text
g = (available − W1) / n
width_i = m + g
```

**Stage 3 — `available > W2`:**

```text
width_i = t + (available − W2) × w_i / Σw        for children 1 … n−1
width_n = available − s × (n − 1) − Σ_{i<n} width_i, clamped ≥ t
```

Required properties:

- **Continuity:** at `W1`, stages 1→2 agree (`g = 0`); at `W2`, stages 2→3
  agree (every child equals `t`). Crossing a boundary causes no visual jump.
- The row's **last child deterministically absorbs the rounding residual** in
  stages 2 and 3.
- Zero-weight children stop at `m` (stages 1–2) or `t` (stage 3).
- If `Σw ≤ 0`, stage 3 distributes equally by count.
- `t` is input data (a named constant, §2.7), never a value measured during
  layout.

**"What you see" table** (two partitions, 100 GiB and 200 GiB; `t = 200 DIP`
indicative; `W1 = 230`, `W2 = 406`):

| Available width | You see | Stage |
| --- | --- | --- |
| 200 | `[112][112]`, row overflows | 1 — equal minimums |
| 320 | `[157][157]` — equal | 2 — equal growth |
| 406 | `[200][200]` — equal, reached `t` | 2 → 3 boundary |
| 563 | `[252.33][304.67]` — larger partition pulls ahead | 3 — capacity distribution |

### 2.5 Fence

Enabled capacity distribution is legal only on the Edit-upper leaf
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

### 2.7 Constants, not magic numbers

Hardcoded values inside named constants are **allowed**. Magic numbers are
not: every width threshold that participates in layout must appear as a named
constant with exactly one owner; inline numeric literals in layout code are
forbidden.

| Constant | Owner | Value |
| --- | --- | --- |
| Leaf minimum `m` | `TopologyLayoutEngine` | 112 DIP (unchanged) |
| Sibling spacing `s` | `TopologyLayoutEngine` | 6 DIP (unchanged) |
| Ancestor chrome | `TopologyLayoutEngine` | 26 DIP (unchanged) |
| Equal-growth threshold `t` | `TopologyLayoutEngine` | confirmed at implementation with a screenshot; indicative 200 DIP |
| Single-line header threshold | `TopologyNodeControl` presentation | confirmed at implementation with a screenshot |
| Per-surface fallback viewport width | each surface's own state | named per surface; replaces the inline 1400 |

Current inline magic numbers this stage eliminates: `1400` (panel fallback
and ViewModel default viewport), `320` and `20` (page `SizeChanged`
handlers), `150` (default parameters of `EqualFillFlowLayout` /
`WeightedPoolLayout`). Each becomes a named constant or disappears with its
owner.

Named constants are code constants, not user-facing settings; settings remain
out of scope (§13).

---

## 3. Edit lower topology

### 3.1 Structure

The Edit lower topology uses the same pool-oriented structure and layout
behavior as Manage wherever no Edit-specific rule is listed below.

It shows internal pools only, including the primordial pool.

The existing Edit working-copy, drag, simulation Execute, modify, and
dissolve behavior is retained except where this Plan explicitly amends it
(§3.4 plus-pool rule; §7 modifiability guardrails).

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

Non-primordial pools render the same logical content as the Manage
projection, subject to the Edit-lower rules of this Plan. The developer
confirmed on 2026-09-05 that the Edit-lower projection must be aligned with
Manage:

- **Tier cards are snapshot-driven.** A pool shows the tiers that exist in
  the snapshot **and have at least one member disk**, ordered like Manage.
  There are no fixed performance/capacity placeholder cards; a tier with no
  member disks renders no card. Non-SSD/SCM/HDD media types render as-is.
- **Draft pools behave the same.** `InsertDraftPool` keeps pre-creating the
  SSD/HDD (and SCM when present) tier records as parameter carriers for the
  pool form; they are not render placeholders. The performance or capacity
  tier card appears only after a disk of that media type has been dragged
  into the draft pool — `MoveDiskToPool` already attaches disks to (or
  creates) the matching tier, so no simulation change is required.
- **Tier-uncovered members are visible.** Pool members not covered by any
  tier render in a DirectDiskGroup "Unallocated" group exactly like Manage
  (same kind, same `group:direct:{pool}` stable-id convention,
  non-selectable group, selectable and draggable disk children). This
  restores the objects that the previous Edit-lower projection silently
  dropped.
- Existing virtual-disk representation (including the "Not created"
  placeholder) and virtual-disk partition strips remain available.

This Plan does not change pool-editing semantics or simulation operations
beyond §3.4 and §7; the alignment above changes projection only.

### 3.4 Synthetic plus pool (single draft)

A synthetic plus-pool remains the Edit lower affordance for adding a pool,
with the single-draft rule (developer decision 2026-09-05):

- the plus-pool is present **only while no draft pool exists**;
- clicking it creates one draft pool and the plus-pool **disappears**;
- at most one draft pool exists at any time — simultaneous multiple drafts
  are not allowed;
- the plus-pool reappears after the draft is executed or discarded.

Conceptually:

```text
no draft:   Primordial | Pool A | Pool B | +
drafting:   Primordial | Pool A | Pool B | draft: Pool C   (no +)
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

The Edit upper topology does not show system, storage pool, storage tier,
pool group, direct-disk group, virtual-disk group, network group, or other
topology grouping containers.

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

- leading, interior, and trailing gaps are separate nodes;
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
partition/unallocated strips, and only because those strips both enable
distribution and declare capacity weights (§2.2).

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

## 6. Width-adaptive node presentation

Single-line collapse is a **capability of storage-object nodes at every
level**, not a disk-only feature (developer decision 2026-09-05):

- the shared `TopologyNodeControl` supports a presentation mode in which the
  node's multi-line header collapses to one horizontal line when its
  engine-assigned width reaches the named single-line threshold (§2.7), and
  expands back to the standard presentation when narrower;
- the capability is enabled per node (or per level) through explicit
  presentation metadata — it can be on or off for each level;
- switching is driven by the engine-assigned slot width (deterministic
  render-time input), never by measured text;
- the threshold is a named constant (§2.7).

This stage enables the capability **only for the Edit-upper disk level**.
Manage and Edit lower keep the standard presentation for every node.

Conceptually (wide Edit-upper disk):

```text
Disk 0    Samsung SSD ...    GPT    1.82 TiB
```

Requirements:

- preserve the information required to identify the object;
- preserve accessibility names;
- preserve selection and interaction behavior;
- remain the shared `TopologyNodeControl`; do not create a second control
  tree.

The exact single-line arrangement and the threshold value are confirmed with
a screenshot during implementation, together with `t`.

---

## 7. Edit-lower structure-modifiability indicator and guardrails

Developer decision 2026-09-05. All rules in this section affect the
Edit-lower simulation workflow only.

### 7.1 Indicator

Storage-object cards in Edit lower show a top-right icon with two states:
**supports structure modification** / **does not support structure
modification**. The icon carries an accessibility name and is presented by
the shared `TopologyNodeControl`.

This stage defines the state for physical disks (primordial-pool members)
and storage pools. Other object kinds show no icon.

### 7.2 State rules

- A physical disk in the primordial pool **supports** modification when it
  has no partitions, or all its partitions hold no stored data; otherwise
  **not supported**.
- A storage pool (non-primordial) **supports** modification when all its
  virtual disks have no partitions, or their partitions hold no stored data;
  otherwise **not supported**.
- Data presence derives from the read-only inventory snapshot. When it cannot
  be determined for an object, the object is treated as **not supported**
  (conservative default).

### 7.3 Behavior

- **Unsupported disk:** the disk may still be rearranged in the working copy
  (dragged between pools); **Execute is blocked**.
- **Unsupported pool:** member physical disks **may be dragged into** the
  pool but **may not be dragged out**; the drag-out is refused. Execute is
  also blocked.
- **Blocked Execute:** clicking Execute while any involved object is
  unsupported opens a dialog that names each problematic object, its problem
  (has partitions / partitions contain data), and what to do (for example:
  back up and empty the volumes, then retry). WinPool performs no clearing
  and offers no destructive action in this dialog.

### 7.4 Scope

The indicator and guardrails are simulation-workflow presentation and
validation. They do not authorize real mutation, real data clearing, or any
new storage operation (§13, §14).

---

## 8. Implementation mapping

This section maps the confirmed requirements to the current implementation. It
does not add product scope.

### 8.1 Application layout engine

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
  and implementing the three-stage enabled-strip allocation (§2.4);
- no-wrap horizontal strip behavior required by Edit upper (§4.4);
- the named constants (§2.7).

Existing Manage calculations remain the baseline; the unit-planning phase is
not redesigned, and default-row output must remain bit-identical to today.

If required, add minimal layout-input metadata for:

- the capacity-distribution enable option and weights (§2.2–§2.3);
- wrap versus no-wrap child rows.

Do not encode page names into `TopologyLayoutEngine`.

### 8.2 Layout result

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

### 8.3 WinUI topology panels

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
- contain magic numbers (§2.7).

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

### 8.4 Manage projection

Primary owner:

`src/WinPool.Application/Topology.cs`

Do not redesign `TopologyProjector.Project`.

Any changes required to supply the unified engine with layout metadata must
preserve the projected Manage topology and current ordering.

### 8.5 Edit projections

Primary owner:

`src/WinPool.Application/EditWorkspace.cs`

#### Upper

`ProjectPartitionWorkspace` remains responsible for selecting partitionable
disks and building visible disk, partition, and unallocated nodes.

Change its layout metadata so the unified engine receives:

- a vertical disk collection (Stack);
- one no-wrap horizontal partition strip per disk;
- the distribution enable option and the actual partition/gap byte sizes as
  declared weights (§2.2–§2.3).

The only arithmetic permitted in `EditWorkspace` is assigning
`weight = byte size`; all distribution arithmetic belongs to the engine.

#### Lower

`ProjectPoolWorkspace` / `ProjectPoolWorkspaceRoot` remain responsible for the
Edit-lower pool subset and the synthetic plus-pool, with:

- primordial physical disks without partition children (keep and cover with
  tests);
- snapshot-driven tier cards per §3.3: tiers with at least one member, Manage
  ordering, no fixed placeholder cards;
- a Manage-equivalent DirectDiskGroup "Unallocated" group for tier-uncovered
  pool members (§3.3);
- the single-draft plus-pool rule of §3.4 (plus-pool node present only while
  no draft exists);
- the structure-modifiability state of §7.2 computed from the read-only
  snapshot and attached to disk and pool nodes as projection data;
- the tree using the same weighted layout semantics consumed by Manage.

### 8.6 ViewModel conversion

`TopologyNodeViewModel` remains the shared presentation model.

The mapping from application layout nodes to ViewModels must preserve the
layout and presentation metadata needed by the unified engine: the optional
distribution enable and weights, the adaptive-presentation enable, and the
modifiability state.

The current App-side conversion lives in
`src/WinPool.App/Services/TopologyLayoutMapper.cs`; updating that mapping is
part of this stage.

Do not create separate Manage/Edit topology ViewModel hierarchies.

### 8.7 Width-adaptive presentation

Primary owners:

- `src/WinPool.App/Controls/TopologyNodeControl.xaml`
- `src/WinPool.App/Controls/TopologyNodeControl.xaml.cs`

Add the smallest explicit presentation mode necessary for single-line
collapse as a general capability (§6), switching on the engine-assigned width
against the named threshold. Enable it per level through presentation
metadata; this stage enables it only for Edit-upper disks.

The same control continues to own selection, expansion, keyboard interaction,
accessibility, drag/drop hooks, and normal topology-card rendering.

Do not duplicate those behaviors into an Edit-only control.

### 8.8 Modifiability indicator and guardrails

Primary owners:

- projection state: `src/WinPool.Application/EditWorkspace.cs` (§7.2 rules);
- icon: `TopologyNodeControl` (top-right, two states, accessible name);
- drag rules: the Edit working-copy editing path — drag-out of a member
  physical disk from an unsupported pool is refused; drag-in remains allowed;
  unsupported disks remain draggable;
- Execute guard: the simulation Execute path refuses to run while any involved
  object is unsupported and raises the explanatory dialog of §7.3, listing
  each problematic object, its problem, and the recommended action. Dialog
  text is bilingual.

No guardrail may mutate real storage or clear data.

### 8.9 Viewport ownership

"Surface" means exactly three regions: the Manage topology, the Edit upper
topology, and the Edit lower topology. Nested panels inside a surface are not
surfaces.

Edit upper and Edit lower must not overwrite a single shared global topology
viewport width that can affect Manage layout.

Prefer the actual width supplied to the WinUI layout panel during
`MeasureOverride` / `ArrangeOverride`.

Fallback viewport state may remain where WinUI infinite-width measurement
requires it, but each surface must use its own host width and its own named
fallback constant (§2.7); the inline `1400` disappears.

Edit upper, Edit lower, and Manage therefore must not race to set one shared
`TopologyViewportWidth`; the shared state is removed and replaced by
per-surface state, with the automatic viewport-isolation regression test.

---

## 9. Work items after approval

Do not start these until the developer explicitly requests execution.

Execute one item at a time, in order. Each item completes only after its
§12 verification. Do not batch multiple items before verification.

**TL5 precondition:** the developer has explicitly confirmed §2.4 (the
three-stage rule). Until then, skip nothing — simply stop before TL5 and
obtain the confirmation.

| Id | Work |
| --- | --- |
| TL1 | Refactor `TopologyLayoutEngine`: engine-owned final row and slot-width allocation preserving current default behavior bit-exactly; named constants (§2.7) |
| TL2 | Route all three surfaces through one root adapter each; nested panels consume stored slots; preserve Manage output |
| TL3 | Remove Edit-page dependence on the shared Manage viewport-width state; per-surface host width and named fallback; eliminate inline magic numbers (§2.7); automatic viewport-isolation regression test |
| TL4 | Refine Edit-upper projection: eligible disks only, two visible levels, vertical disks through the engine Stack channel, no-wrap horizontal partition strips, enable + weights metadata |
| TL5 | Three-stage strip allocation (§2.4) — **requires prior developer confirmation of §2.4** |
| TL6 | Edit-lower projection alignment: snapshot-driven tier cards, tier-uncovered member Unallocated group, plus-pool single-draft rule (§3.3, §3.4) with projection tests |
| TL7 | Edit-lower modifiability state, icon, drag guardrails, and Execute-blocking dialog (§7) |
| TL8 | Width-adaptive presentation capability; enable for Edit-upper disks; screenshot confirmation of the single-line arrangement, header threshold, and `t` |
| TL9 | Add targeted layout, projection, distribution, presentation, and architecture regression tests |
| TL10 | After developer acceptance of implementation, record the important final result in CHANGELOG; version remains V0.45 |

TL0 (documentation rollover) was completed 2026-09-04; the 2026-09-05
amendments are separate documentation work items.

Commit split after execution:

1. documentation (amendments, already separate work items);
2. equivalent layout-engine refactor (TL1–TL2);
3. Edit projection / layout behavior (TL3–TL5);
4. Edit-lower behavior and guardrails (TL6–TL7);
5. adaptive visual presentation (TL8);
6. final accepted documentation result if required.

This repository remains direct-to-`main`; do not create a feature branch or PR.

Do not push unless the developer explicitly asks.

---

## 10. Required targeted tests

This is ordinary feature/refactor work, not an automatic full-quality-gate
run.

Run the smallest directly related automatic tests during implementation.

### 10.1 Unified layout engine

Extend the existing `TopologyLayoutEngineTests`.

Required cases include:

- current Manage reference cases continue to produce equivalent row packing;
- current Manage weighted siblings retain their existing width behavior
  (default rows are bit-compatible with today's panel arithmetic);
- rows without the enable option produce **no** weight-based width
  differences, even if capacity data exists (§2.2);
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

### 10.2 Distribution acceptance cases

The deterministic cases of §5.3 (A1, A2, A3, A4, B, C), recomputed with the
confirmed `t` value.

### 10.3 Edit upper projection

Extend `EditWorkspaceTests` to prove:

- only partitionable disks are shown;
- physical members of non-primordial pools are excluded from the raw
  partitionable-disk list;
- eligible virtual disks remain available;
- network drives are absent;
- disk ordering remains deterministic;
- partitions and gaps stay in offset order;
- leading, interior, and trailing visible gaps remain distinct;
- upper topology has only visible disk and partition/unallocated levels;
- strips carry the distribution enable and byte-size weights.

### 10.4 Edit lower projection

Tests must prove:

- primordial pool is present;
- primordial physical disks have no partition children in Edit lower;
- non-primordial pool structure remains available;
- plus-pool: present exactly once while no draft exists; absent while a draft
  exists; reappears after execute or discard; a second draft cannot be
  created;
- tier cards follow the snapshot: a tier with members renders; a tier without
  members renders no card; a non-standard media tier renders as-is;
- a draft pool shows no tier cards until a disk is dragged in, then shows
  exactly the tiers that gained members;
- pool members not covered by any tier appear in the Unallocated group, are
  selectable and draggable, and can be dragged to another pool;
- modifiability state: disk without partitions → supported; disk with empty
  partitions → supported; disk with data → unsupported; pool whose virtual
  disks have no partitions or hold no data → supported; otherwise
  unsupported; undeterminable data presence → unsupported.

### 10.5 Guardrails

Tests must prove:

- an unsupported pool refuses member drag-out and accepts drag-in;
- an unsupported disk remains draggable;
- Execute with any unsupported involved object is refused and produces the
  explanatory dialog data (object, problem, recommended action);
- no guardrail path performs real storage mutation or data clearing.

### 10.6 Manage non-regression

Existing Manage topology and layout tests must pass without rewriting their
expected behavior merely to accommodate the refactor.

A failing Manage expectation must be treated as a regression unless the
developer explicitly changes the requirement.

### 10.7 Presentation structure

Add the smallest architecture/presentation test practical for:

- Edit upper selecting the single-line presentation when the assigned width
  reaches the named threshold, and the standard presentation when narrower;
- the adaptive capability being disabled for every non-Edit-upper-disk level
  in this stage;
- Manage and Edit lower remaining on the standard presentation mode;
- all levels still using the shared `TopologyNodeControl`;
- Edit-lower disk and pool nodes exposing the modifiability icon state.

Do not claim visual appearance passed from an automatic structure test.

---

## 11. Manual verification after implementation

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
    to the standard presentation when narrower; no other level collapses.
11. Edit lower primordial disks do not expose partitions.
12. Edit lower plus-pool: clicking it creates one draft and the plus
    disappears; it returns after execute or discard; a second draft cannot be
    created.
13. Edit lower modifiability icon matches §7.2 for prepared scenarios (disk
    with data; disk without partitions; pool with populated virtual disks;
    pool with empty virtual disks).
14. Unsupported disk: dragging still works; Execute is blocked with a dialog
    naming the disk, the problem, and the recommended action.
15. Unsupported pool: member disks cannot be dragged out but can be dragged
    in; Execute is blocked with the dialog.
16. Existing Edit drag, draft, Execute, modify, and dissolve simulation
    behavior still works for supported objects.
17. Dragging the Edit splitter continuously produces no crash, no layout
    flicker loop, and stable idle CPU after release (§2.6).
18. The screenshot confirming the single-line header, the header threshold,
    and `t` is recorded (TL8).
19. A pool whose members are not fully covered by tiers shows the
    Unallocated group with those disks in Edit lower, matching Manage, and
    the disks can be dragged out.
20. Draft and newly created pools show performance/capacity tier cards only
    after disks of the matching media type are dragged in; no empty
    "0 physical disks" tier card appears anywhere.

Until a human/native run is actually performed, this evidence is
`unverified`.

A full Quality gate is `not_required` for ordinary implementation of this Plan
unless the developer explicitly requests formal testing.

---

## 12. Execution and verification discipline

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
- **Unconfirmed rules are not implemented.** TL5 waits for explicit §2.4
  confirmation.
- **After a crash**, read the crash log and Windows event log (1000/1026)
  before changing code; prefer instrumentation over guessing; report the root
  cause and wait for approval before fixing.
- **Scripted code edits** must be confirmed to compile before any launch used
  for verification.

---

## 13. Out of scope

This stage does not include:

- changing the Manage logical topology design;
- redesigning Manage cards;
- making pool rows equal-width or otherwise changing Manage distribution;
- capacity-proportional pool widths;
- enabling distribution without the explicit strip option (§2.2);
- enabling the adaptive single-line presentation for levels other than the
  Edit-upper disk level;
- multiple simultaneous draft pools;
- changing `InsertDraftPool` or `MoveDiskToPool` simulation semantics
  (pre-created draft tier records remain parameter carriers);
- real data clearing, backup execution, or any destructive recovery
  operation (the Execute dialog only explains what to do);
- new storage operations;
- changing storage-pool simulation semantics beyond §3.4 and §7;
- changing partition operation semantics;
- new partition eligibility rules;
- real storage mutation;
- database schema changes;
- IPC protocol changes;
- new persistence;
- new settings other than preserving the existing unallocated-gap setting
  (named constants are code constants, not settings);
- network/external pool editing;
- packaging;
- deployment;
- unrelated visual redesign;
- public layout APIs or plug-in architecture.

If implementation appears to require one of these, stop that expansion and
return to the developer instead of silently broadening this Plan.

---

## 14. Safety

The existing deny-by-default execution boundary remains unchanged.

- All storage-structure changes in this stage remain simulation-only.
- Real disk, partition, volume, pool, tier, or virtual-disk mutation is not
  authorized.
- UAC elevation or Real mode is not authorization.
- The modifiability indicator and guardrails are presentation and validation
  only; they never clear data and never mutate real storage.
- Hardware data remains behind the existing redaction boundary.
- Inventory PowerShell remains embedded and read-only.
- No real hardware mutation is an accepted test method under this Plan.

---

## 15. Acceptance gate

Implementation is complete only when:

- all three topology regions use `TopologyLayoutEngine` as the single layout
  algorithm authority;
- each surface runs exactly one root-level engine layout; no nested panel
  invokes the engine or re-plans a budget;
- rows without the distribution enable option render exactly as today; no
  weight-based width differences appear anywhere by default;
- the engine implements the three-stage enabled-strip allocation (§2.4) —
  after the developer's explicit confirmation of §2.4 — with continuity at
  `W1` and `W2`;
- stage 1 keeps minimum widths and overflows without negative adjustment;
  stage 2 grows all children equally; stage 3 distributes width above `t` by
  declared capacity weights, with the last child absorbing the rounding
  residual;
- no Edit-specific panel independently performs equal-fill, weighted-fill, or
  capacity-fill layout decisions;
- Manage behavior remains equivalent;
- Edit lower primordial disks do not display partitions;
- Edit lower tier cards are snapshot-driven: tiers without member disks
  render no card, and non-standard media tiers render like Manage (§3.3);
- Edit lower shows the Unallocated group for tier-uncovered pool members,
  equivalent to Manage;
- Edit lower plus-pool follows the single-draft rule (§3.4);
- Edit lower disk and pool cards show the modifiability icon per §7.2, with
  the conservative default for undeterminable data presence;
- unsupported disks remain draggable with Execute blocked; unsupported pools
  refuse member drag-out, accept drag-in, and block Execute; the blocked
  Execute dialog names each object, problem, and recommended action;
- Edit upper contains only eligible disk → partition/unallocated topology;
- Edit upper disks are vertically stacked through the engine Stack channel;
- partitions inside one disk form one horizontal no-wrap strip;
- `t` and the single-line header threshold are named constants with confirmed
  values;
- no magic numbers remain in layout code (named constants with hardcoded
  values are allowed);
- the width-adaptive presentation exists as a per-level capability and is
  enabled only for Edit-upper disks in this stage;
- shared viewport state no longer allows Edit upper/lower resizing to mutate
  Manage layout;
- targeted automatic tests are `passed`;
- every implemented TL item has its native verification evidence recorded;
- native visual click-through remains `unverified` until actually performed.

After implementation reaches this state, do not start formal acceptance
automatically. Ask the developer whether to enter formal testing.
