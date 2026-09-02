# WinPool Edit-page topology workspace Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** draft; answers recorded; remaining questions open; **not confirmed for implementation**
- **Created:** 2026-09-02
- **Updated:** 2026-09-02
- **Baseline commit:** `c471311460445d9b57fe790df2a00444bed7754a`
- **Working branch:** `main`
- **Current product version:** V0.45
- **Target product version:** V0.45
- **Stage type:** Edit-page layout and simulated pool/partition workspace redesign; no real storage mutation

This file exists because the developer asked to write `docs/Plan.md` from the
Edit-page requirements, then wait. It is **not** a confirmed plan.
Archive history must not be used to invent extra stages or extra features.

Writing or updating this Plan does not authorize implementation, push, tag,
GitHub Release, binary upload, deployment, or real storage mutation.
Implementation starts only after the developer answers the remaining questions,
explicitly approves this Plan, and then explicitly asks to execute it.

## 1. Controlling decisions

These outrank the current Edit page. They do not outrank the real-mutation
safety boundary. All mutation in this stage is **simulation only**.

### 1.1 Page chrome

- Two vertical halves, initial height 1:1, with a row splitter.
- Each half has **one** vertical scrollbar. The two halves do not share a
  page-wide scroll.
- No titles for the halves, and no “logical topology” / “control group”
  headings.
- Inside each half: topology on the left, control group on the right.
- The control group has a **fixed width**. Resizing the window changes the
  topology area, like the Manage command pane. Topology uses the remaining
  width.

### 1.2 Upper half

- Left: logical topology using the Manage visual controls
  (`TopologyNodeControl` and the existing Stack / Flow layouts).
- Right: keep the current partition actions — extend, shrink, delete, format,
  new partition, initialize, offline / online.
- Projection:
  - Two levels only: disk and partition. No system, pool, or tier.
  - Disks stacked vertically. Partitions inside a disk arranged horizontally.
  - Show partitionable disks. Physical members of a non-primordial pool are
    not shown. Virtual disks that can be partitioned are shown.
  - Unallocated space is a child node of the disk. If a partition splits
    unallocated space into two regions, those are **two** child nodes, using
    offset gaps, not one leftover total.
- Boot/system disks and network disks in this list are still **Q20 / Q21**.

### 1.3 Lower half — topology

- Internal pools only, including the primordial pool. No network / external
  pools.
- Primordial pool is shown down to disks. It has no tiers and no partitions.
- A storage pool shows tiers, disks, and virtual disks. Virtual disks show
  partitions.
- Pool row is left-to-right so a fake plus-pool sits to the right of all pools.
- Clicking plus inserts a **local draft** pool. It is not written to the
  simulation document until Execute succeeds.
- A new draft pool shows: virtual disk “not created”, performance tier empty,
  capacity tier empty.
- SSD → performance tier. HDD → capacity tier.
- SCM uses a **dedicated tier**. That tier is hidden unless at least one SCM
  disk is present. The default visible tiers are SSD and HDD only.
- Unknown / unspecified media: drop is refused.
- Drag recognizes **pools only**, not tiers. On release the disk enters the
  matching tier automatically.
- Drag updates the Edit working copy only. It is **not** written to the
  document until Execute.

### 1.4 Lower half — control grid

Two columns: item name | item value. Selecting a pool fills the grid from that
pool. A plus-created draft is filled with recommended parameters.

If the selected pool has **more than one virtual disk**: further modification
is not allowed, and a warning is shown:

> 不推荐在一个 Windows 存储空间内创建多个虚拟磁盘。如确有需要，推荐创建一个虚拟磁盘并创建多个分区。

English UI uses the same meaning.

Added rows, per tier: column count, number of data copies, number of tolerated
disk failures, together with the already named resiliency, interleave, and
size. A one-click **dissolve pool** button is added. Exact extra-row order and
dissolve placement are in §3, subject to Q22–Q29.

### 1.5 Execute and create defaults

- Product version stays **V0.45**.
- Execute and dissolve run only against a **simulated** system. Local real
  inventory stays read-only.
- Empty pool: Execute stays disabled.
- On Execute for a draft: create the pool, the virtual disk, and a partition.
  The partition uses NTFS + 64K cluster, which includes simulated format.
- Defaults from the V10 recommended configuration, adjusted by disk count:

| Field | Default |
| --- | --- |
| Performance resiliency | Mirror; **Simple** when that tier has one disk |
| Performance data copies | 2; **1** when Simple |
| Performance interleave | 64K |
| Performance columns | Windows auto (V10: do not pass `-NumberOfColumns` for the SSD tier). Control behavior is Q26. |
| Capacity resiliency | Parity; **Simple** when that tier has one disk |
| Capacity physical-disk redundancy | 1; **0** when Simple |
| Capacity columns | Follow member HDDs: equal capacity `N = n`, mixed capacity `N = n − 1`. Confirm Q23. |
| Capacity interleave | 64K |
| Partition file system | NTFS |
| Partition cluster | 64K |
| 64K + 64K research note | helper text under the cluster-size row, not a half title |

The one-disk Simple fallback is **per tier**, matching V10 (“Mirror when the
SSD tier has two drives and Simple when it has only one”).

### 1.6 Modify existing simulated pools

Selecting an existing simulated pool loads its parameters. The user may edit
them and Execute modify. This rewrites the simulation document. It does not
claim that Windows can apply the same change in place on real hardware.

## 2. Remaining questions

Answer with the question number. Proposed defaults are **not** decisions.

**Q20.** 上区要不要显示装着 Windows 的系统盘（C: 所在的那块）？  
A. 显示  
B. 不显示

**Q21.** 上区要不要显示网络映射盘？  
A. 不显示  
B. 显示

**Q22.** SCM 专用层默认复原？  
A. 镜像，2 副本（和性能层一样；一块 SCM 时回退 Simple）  
B. 其他（请写）

**Q23.** 容量层列数默认？  
A. 跟当前 HDD 块数走（同容量 `N = n`，混容量 `N = n − 1`，按 V10）  
B. 固定 5（V10 推荐硬件是 5 块 HDD）  
C. 其他（请写）

**Q24.** 选中有多个虚拟磁盘的池时，解散池按钮？  
A. 仍可用  
B. 禁用

**Q25.** 选中有多个虚拟磁盘的池时，拖盘？  
A. 仍可用  
B. 禁用

**Q26.** 性能层「列数」在控件里？  
A. 显示，只读（V10：SSD 层不要手动传 `NumberOfColumns`）  
B. 显示，可改  
C. 不显示

**Q27.** 「冗余副本数量」和「允许故障盘数」？  
A. 两个都能改  
B. 只改副本数，故障盘数自动算  
C. 只改故障盘数，副本数自动算

**Q28.** 选中原始池时，下区右侧控件？  
A. 空白  
B. 显示但全部禁用  
C. 其他（请写）

**Q29.** 点执行时提交范围？  
A. 当前选中池的参数 + 全部尚未提交的拖盘  
B. 只提交当前选中池自己的参数和进/出这块池的拖盘  
C. 其他（请写）

**Q30.** 解散已有模拟池时的效果？  
A. 删池、层、虚拟磁盘、分区；物理盘回原始池；先确认  
B. 其他（请写）

**Q31.** 解散按钮放在哪？  
A. 下区控件组里，执行按钮下面单独一行  
B. 其他（请写）

## 3. Proposed shape after the remaining answers

This section is a mapping, not extra product scope.

### 3.1 Working copy

While the active document is a simulation, Edit holds a working copy of the
snapshot. Dragging disks updates that copy and the lower topology immediately.
Execute commits the working copy through typed simulation operations.
Switching away from Edit without Execute discards the working copy.

### 3.2 Projections

- `ProjectPartitionWorkspace`: disk forest for the upper half. Unallocated
  child nodes from offset gaps, including leading, trailing, and holes.
- `ProjectPoolWorkspace`: primordial, named internal pools, optional SCM
  tier when SCM is present, then the plus node. Manage
  `TopologyProjector.Project` is unchanged.

### 3.3 Proposed lower-right row order

Subject to Q26, Q27, Q31, and SCM visibility:

1. Execute modify / create new pool
2. Dissolve pool (Q31)
3. Pool name
4. Virtual disk name
5. Performance: resiliency, interleave, size, columns, data copies, tolerated
   failures
6. Dedicated SCM (only if SCM is present): same field set
7. Capacity: resiliency, interleave, size, columns, data copies, tolerated
   failures
8. Partition file system
9. Partition cluster size
10. 64K + 64K helper text

### 3.4 Simulation operations that do not exist today

Current simulation can create a pool (members required, no tiers), create a
virtual disk, move a disk, and edit partitions. It cannot create storage
tiers, and it refuses to move a disk that already belongs to a tier.

This stage must add, still simulation-only:

- create pool with performance / capacity (and SCM when needed) tiers;
- create virtual disk + partition + format from Execute on a draft;
- tier-aware disk membership on Execute;
- rewrite pool / tier / virtual-disk parameters on Execute modify;
- dissolve pool back to primordial.

`StorageTierInfo` currently has `NumberOfColumns` and `Interleave` but not
data-copies or tolerated-failure fields. Those become optional snapshot
fields if Q27 needs them persisted. Schema 14 and IPC protocol 4 stay unless
a later answer proves a wire or database change.

Physical-disk drag sources exist only on the Edit lower tree. Manage nodes
stay non-draggable.

### 3.5 Upper-right actions

Keep the current simulation partition/disk actions, driven by the upper
topology selection (disk or partition or unallocated node). Unallocated-node
click uses the existing new-partition path.

## 4. Work items after approval

Do not start these until the Plan is confirmed and execution is requested.

| Id | Work |
| --- | --- |
| EP1 | Edit chrome: 50/50 halves, row splitter, one scrollbar per half, fixed-width controls, no half titles |
| EP2 | Partition-workspace projection, unallocated gap nodes, upper `TopologyNodeControl` |
| EP3 | Keep upper-right partition actions, bind them to the new selection |
| EP4 | Pool-workspace projection, plus/draft pool, SCM tier hidden unless present |
| EP5 | Pool-only drag on the working copy; auto-tier SSD/HDD/SCM; refuse unknown media |
| EP6 | Simulation operations: tiered create, tier-aware membership, modify, dissolve |
| EP7 | Lower two-column grid, recommended defaults, multi-vdisk lock + warning, Execute |
| EP8 | Localization |
| EP9 | Tests for the two projections, gap unallocated nodes, auto-tier, Manage non-regression |
| EP10 | CHANGELOG after the developer accepts the implemented stage. Version stays V0.45 |

Commit split after execution: documentation, refactor, feature, visual.

## 5. Out of scope

- Real disk, partition, volume, pool, tier, or virtual-disk mutation.
- Changing Manage-page topology rules.
- Network / external pool editing.
- Test and Development workspace features.
- Schema or IPC version changes unless a remaining answer proves they are
  required.
- Push, tag, Release, or binary upload.
- Revising or publishing the V10 article.
- Work outside `Program\WinPool`.

## 6. Safety

- Deny-by-default executor stays in force.
- UAC or Real mode is not authorization.
- Protected-machine policy continues to refuse `R4+` real structure mutation.
- Hardware data stays behind the redaction boundary.
- Inventory PowerShell stays embedded and read-only.
- A development Agent must not perform a real mutation under this Plan.

## 7. Verification when this Plan is later executed

Documentation-only edits of this file do not run code tests.

After a later approved implementation:

- Application tests for the two Edit projections, unallocated gaps, auto-tier,
  multi-vdisk lock, and dissolve.
- Existing Manage topology tests remain green without rewriting Manage rules.
- Local simulation-only click-through of Edit. Real hardware is not a
  verification method.
- Full quality gate and OS-matrix evidence stay `not_required` until the
  developer asks for formal testing.

## 8. Approval gate

This Plan is ready for the remaining answers. It is not ready for
implementation.

Reply to Q20–Q31. After that, either confirm this Plan, or name the changes
to make before confirmation. Silence is not approval.
