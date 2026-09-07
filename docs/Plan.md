# WinPool V0.47 storage-structure editor controls

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed and installed as the active Plan; implementation
  not started; execution awaits the developer's explicit request
- **Created:** 2026-09-07
- **Baseline commit:** `918679f21d48d6db7034430ddf6bcbf70c547864`
- **Working branch:** `main`
- **Current product version:** V0.47
- **Target product version:** V0.47
- **Stage type:** storage-structure editor controls on the already split pages

The Edit-page split is closed and frozen under
[Archive/V0.47-editor-pages](Archive/V0.47-editor-pages/Plan.md). This Plan is
the former temp control spec, now the only active Plan.

Product decisions:
[Storage-Structure-Product-Decisions.md](Storage-Structure-Product-Decisions.md).
Accepted editor design (historical):
[Archive/V0.47-standalone-pool-editor](Archive/V0.47-standalone-pool-editor/Storage-Pool-Editor-Design.md).

This Plan does **not** authorize real storage mutation, push, tag, GitHub
Release, binary upload, deployment, schema changes, IPC changes, or unrelated
product work. All mutation in this stage is **simulation only**.

No implementation begins until the developer explicitly requests execution of
this Plan.

## 1. Page

Left: topology. Upper right: structure operations. Lower right: properties.
No bottom draft bar. Simulation only: when the current system is not a
simulation, upper-right and lower-right controls are disabled.

Listed controls stay visible except a real-tier field group, which appears only
when that tier exists. Disable what cannot run. Computed values are read-only
and gray.

## 2. Upper right

Always visible. Wrap by group: buttons in a group left to right; groups top to
bottom.

```text
Undo    Redo    Discard all    Apply all
Create pool    Dissolve pool
Retire disk    Hot-spare disk
Create virtual disk    Delete virtual disk
```

| Button | Disabled when | Action |
| --- | --- | --- |
| Undo | no previous draft step | Undo one draft step (topology, properties, structure buttons) |
| Redo | nothing to redo | Redo one step |
| Discard all | no unapplied changes | Working copy returns to last applied snapshot |
| Apply all | no unapplied changes, or not a simulation | Write the draft to the simulation document. Confirm first for 256K stripe, ReFS, wiping data on join, deleting a virtual disk that holds data, dissolving a committed pool |
| Create pool | a draft pool already exists, or not a simulation | Insert an empty draft pool. Disks dragged in join as data disks and create real tiers by media |
| Dissolve pool | no non-primordial pool, or not a simulation | Draft pool: discard it. Committed pool: confirm, then dissolve on Apply all |
| Retire disk | no in-pool physical disk selected, already retired, system/boot, or not a simulation | Disk enters the retired simulated layer and turns on Show retired. Page-file/crash-dump: confirm dropping that role first |
| Hot-spare disk | no in-pool physical disk selected, already hot spare, system/boot, or not a simulation | Disk enters the hot-spare simulated layer and turns on Show hot spare |
| Create virtual disk | the current pool already has a virtual disk, or not a simulation | Create the one virtual disk from lower-right properties |
| Delete virtual disk | the current pool has no virtual disk, or not a simulation | Delete the selected virtual disk, including the last one. Confirm if it holds data |

No Execute modify, Confirm properties, Add tier, Remove tier, Optimize, or Repair on this page. Optimize and repair stay on Manage.

## 3. Lower right

Each row is label then value. Gaps between the pool group, each real-tier group,
and disk-and-partition. Last row is a full-width button **Save pool properties**,
which writes the property form for the current pool. Membership, create,
dissolve, and delete still go through Apply all.

### Pool

| Field | Control | Default | Gray / disabled |
| --- | --- | --- | --- |
| Pool name | text | PoolNN | |
| Virtual disk name | text | same as pool | still shown before the disk exists |
| Volume name | text | same as virtual disk | disabled when auto-create partition is off |
| Auto-create partition | switch | on | disabled when a virtual disk already exists |
| Show hot-spare layer | switch | off | cannot turn off while that layer has disks |
| Show retired layer | switch | off | cannot turn off while that layer has disks |

On: topology draws that simulated layer even if empty, as a drop target. Drag
matches the Retire / Hot-spare buttons.

### Performance / capacity / dedicated

One field set. Draw a group only when that tier exists (SSD data disks /
HDD data disks / SCM data disks). First matching data disk creates the group;
last disk leaving removes it.

| Field | Control | Default | Gray / disabled |
| --- | --- | --- | --- |
| Size | GB text | Max for that tier’s data disks | may shrink, not above Max |
| Provisioning | Fixed / Thin | Fixed | read-only gray Fixed while real tiers exist |
| Resiliency | Simple / Mirror / Parity | Performance/dedicated: Mirror if ≥2 disks, Simple if 1. Capacity: Parity | disabled when stored data exists |
| Data copies | number | 2 for Mirror | read-only gray 1 for Simple; disabled when stored data exists |
| Fault tolerance | number | copies−1 for Mirror; 1 for Parity | gray when computed; editable for Parity; disabled when stored data exists |
| Physical disk count | number | current data disks in the tier | read-only gray |
| Columns | number | Mirror from copies and disk count; capacity n or n−1 | Mirror read-only gray by default; disabled when stored data exists |
| Stripe size | 16K / 32K / 64K / 128K / 256K | 64K | disabled when stored data exists; 256K requires confirm on apply |

### Disk and partition

| Field | Control | Default | Gray / disabled |
| --- | --- | --- | --- |
| Partition table | GPT / MBR | GPT | read-only gray after the virtual disk is initialized |
| File system | NTFS / ReFS | NTFS | disabled when auto-create partition is off; ReFS disabled if the SKU cannot create it; read-only gray when stored data exists; ReFS requires confirm on apply |
| Allocation unit | 4K / 8K / 16K / 32K / 64K | 64K | disabled when auto-create partition is off; read-only gray when stored data exists |

More than one user partition: file system and allocation unit read-only gray.

## 4. Topology

- Drag into a pool = data disk; real tier by media.
- Draw a real tier only when it has data disks.
- Simulated hot-spare and retired layers: draw only when the switch is on;
  draw even if empty.
- Undo/redo/discard/apply cover topology drags.

## 5. Work items

| ID | Work | Check |
| --- | --- | --- |
| SC1 | Upper-right buttons in the four wrapped rows, including undo/redo/discard-all/apply-all | All ten buttons visible; disable rules match §2 |
| SC2 | Lower-right label-value groups, gaps, Save pool properties full row; real-tier groups only when the tier exists | Layout matches §3 |
| SC3 | Show hot-spare / retired switches; drag into those simulated layers | Switch off hides an empty simulated layer; drag sets the state |
| SC4 | Create virtual disk only when none exist; delete allowed for the last disk | Create disabled with one disk; last disk can be deleted |
| SC5 | Tests for the new controls; native open of Storage structure | Named tests pass; native: start → page → process alive → no new crash log |

Reuse the topology layout engine. Before changing it, read
[Reference/20260905_统一拓扑布局引擎执行踩坑记录.md](Reference/20260905_统一拓扑布局引擎执行踩坑记录.md).

## 6. Out of this Plan

- Creating a second virtual disk
- Manual allocation, Journal disks
- Optimize and repair on this page
- Cluster / S2D / WAC
- Real storage-structure mutation
- Push, tag, Release, binaries

## 7. Version

Product version stays **V0.47**. Still the V0.4 line. Iteration `c=7`.
