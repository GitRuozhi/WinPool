# WinPool V0.47 storage-structure and disk-partition editors

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed and installed as the active Plan; implementation
  not started; execution awaits the developer's explicit request
- **Created:** 2026-09-07
- **Baseline commit:** `f60bbc5c871e97e3e78f39a10e91b27a74a217c3`
- **Working branch:** `main`
- **Current product version:** V0.47
- **Target product version:** V0.47
- **Stage type:** replace the Edit page with two simulation editors that
  implement the accepted standalone storage-pool design

This Plan exists because the developer accepted the standalone storage-pool
editor design, asked to archive it, and asked to write a new `docs/Plan.md`
with version `+0.01`.

The accepted product design is frozen at
[Archive/V0.47-standalone-pool-editor/Storage-Pool-Editor-Design.md](Archive/V0.47-standalone-pool-editor/Storage-Pool-Editor-Design.md).
Before any execution, read that design. It outranks the current Edit page. It
does not outrank the real-mutation safety boundary.

This Plan does **not** authorize real storage mutation, push, tag, GitHub
Release, binary upload, deployment, schema changes, IPC changes, or unrelated
product work. All mutation in this stage is **simulation only**.

No implementation begins until the developer explicitly requests execution of
this Plan.

## 1. Controlling decisions

These are copied from the accepted design so the Plan can be executed without
re-opening product debate.

1. Retire the current Edit page. Former lower half → **Storage structure
   editor**. Former upper half → **Disk/partition editor**.
2. Storage structure editor: left topology; upper-right structure operations;
   lower-right pool / virtual-disk / partition properties. No bottom draft
   bar. Undo, redo, discard-all, and apply-all live in the upper right.
3. Disk/partition editor: one control group for the disk, one for the
   partition (or unallocated gap).
4. One working copy. Upper-right apply-all writes it. No second submit
   button for properties.
5. Real tiers follow disks; there is no Add-tier button. Empty real tiers
   are not drawn. Hot spare and retired are optional simulated layers
   shown by pool switches.
6. Hide the PowerShell template-tier ritual: spec on the pool, size on the
   virtual disk.
7. **One virtual disk is the 1.0 contract.** Do not create a second. A pool
   that already has several may be reduced to one (delete extras; confirm if
   they hold data; no silent merge). Other cases wait for the Development
   command line and are outside 1.0.
8. Create/modify on the structure page supports one user partition. Checkbox
   **Create a partition when creating the disk**, default on. Extra partitions
   go to Disk/partition editor.
9. NTFS and ReFS are both supported. Prefill NTFS 64K. Disable ReFS creation
   when the SKU forbids it.
10. Prefill `64K` interleave, HDD Parity with columns `n` or `n−1`, SSD Mirror
    (Simple if one disk). Warn on 256K combinations. These defaults are
    changeable.
11. Columns, stripe size, media type, resiliency, and copies are pool
    property fields. Journal and Manual allocation are out of this product
    path. Optimize and repair stay on Manage; they are not structure
    operations.
12. Structure controls are always visible. Disable what cannot run; show
    computed values read-only in gray. Do not hide a control because
    nothing is selected.
13. Workstation and standalone server share the editor. No cluster / S2D / WAC.
14. Simulation only. Real mutation stays denied.

## 2. Closed loop

When this Plan is complete, a user can:

- Open Storage structure editor and Disk/partition editor instead of Edit.
- Create one simulated pool with media tiers, one virtual disk, and optionally
  one NTFS or ReFS volume, using the research defaults unless they change them.
- Modify membership, add a tier to an all-Unallocated pool, evict to
  Unallocated, and apply once without the draft snapping back.
- Reduce an existing multi-virtual-disk pool to one disk.
- Partition unpooled disks on the Disk/partition page with separate disk and
  partition controls.

That is the minimum closed loop. Do not add a second virtual-disk create path,
cluster objects, free-form commands, or real mutation.

## 3. Work items

Execute in order. Do not start the next item until the named check for the
current item has passed, unless the developer changes the order.

| ID | Work | Check |
| --- | --- | --- |
| PE1 | Navigation: remove Edit; add Storage structure editor and Disk/partition editor; bilingual labels; last-page restore still works | App starts; both pages open; Edit is gone; process stays alive |
| PE2 | Disk/partition editor: move the former Edit-upper topology; split disk vs partition control groups | Selecting a disk enables only disk actions; selecting a partition enables only partition actions |
| PE3 | Storage structure chrome: left topology; upper-right operations in the order undo/redo/discard-all/apply-all, create/dissolve pool, retire/hot-spare disk, create/delete virtual disk; lower-right properties as named in the control spec; all listed controls visible | Layout matches §1; empty real tiers are not drawn |
| PE4 | Create composition: select disks, correct media type, roles, tiers, one virtual disk, auto-partition checkbox, NTFS/ReFS, research prefills | Simulated create yields one pool, one virtual disk, optional one user volume; 256K warns |
| PE5 | Modify: join/evict/unallocated→tier, add-tier without drawing an empty strip, one apply for structure and parameters | All-Unallocated re-tier persists; forms do not silently revert |
| PE6 | Reduce several virtual disks to one; refuse creating a second | No create-second control; extras can be removed with confirmation when they hold data |
| PE7 | Apply-all from the upper right writes the simulation only | Real systems stay read-only; dangerous cases still use a confirmation dialog |
| PE8 | Tests for the new pages' projection and simulation operations; native open of both pages | Named tests pass; native: start → each new page → process alive → no new crash log |

Reuse the topology layout engine. Do not invent a second layout system. Before
changing the engine, read
[Reference/20260905_统一拓扑布局引擎执行踩坑记录.md](Reference/20260905_统一拓扑布局引擎执行踩坑记录.md).

## 4. Verification

Ordinary PE items use the smallest related test plus the native open in PE8
when UI chrome moved. Completing the Plan does not start full acceptance. Ask
the developer before a formal gate.

Result vocabulary follows [Quality](Quality.md): `passed`, `failed`,
`unverified`, `not_required`, `deferred_by_user`.

Real hardware mutation is `not_required` and remains denied.

## 5. Explicitly out of this Plan

- Creating a second virtual disk
- Cluster / S2D / WAC
- Development-page command line (1.x tab stays a placeholder)
- Real storage-structure mutation
- Push, tag, Release, binaries
- Schema or IPC version changes unless an implementation item proves a
  document-format change is required, in which case stop and ask

## 6. Version

Product version is V0.47 (`Directory.Build.props` iteration 6 → 7). This is
still the V0.4 product line. Iteration `c=7`; remind at `c=8` or `c=9`. Never
`c=10`.
