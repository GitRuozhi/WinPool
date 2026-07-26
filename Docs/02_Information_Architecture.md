# Information Architecture and Workspace Behavior

## Single-window shell

Use one main WinUI window without outer navigation.

```text
Title bar       WinPool [Administrator]   [Simulation | Real] [Settings]
───────────────────────────────────────────────────────────────────────
Upper operation area
  System | Pool | Tier | Disk | Logical volume
  Vertical object selector | object information and CommandBar
════════════════ horizontal GridSplitter ═════════════════════════
Lower logic area
  Complete nested storage topology
```

- Default row ratio: 40% operation area and 60% logic area.
- Each area owns its own scrolling.
- The title bar keeps normal Windows drag and caption behavior.
- Simulation/Real and Settings stay in the title bar and do not consume workspace height.
- Append ` [Administrator]` / ` [管理员]` to the title only when elevated; do not show privilege as a separate workspace label.
- Settings replaces the complete workspace with a theme-and-language page until the user returns.
- The first UI uses stock theme-aware WinUI geometry and controls.

## Selection model

```text
WorkspaceSelection =
  HorizontalCategory
  + VerticalObject
  + ActiveStorageUnit
```

The vertical object collection changes with the horizontal category:

```text
系统     → 当前计算机
池       → 未池化 | Pool01 | └ VirtualDisk01 | Pool02 | ...
层       → Pool01 / 性能层 | Pool01 / 容量层 | ...
磁盘     → 磁盘 0 | 磁盘 1 | 型号与编号 | ...
逻辑卷   → C: | D: | E: | 无盘符卷 | ...
```

Rules:

- Category switching updates the vertical list and operation content atomically.
- The most recent valid selection is remembered independently for each category.
- Rescan preserves selection by stable ID, never display name alone.
- A missing object produces an explicit disappeared-object state; no command is selected automatically.
- Clicking a topology node selects the same stable object in the upper area and never executes a command.
- A virtual disk maps to Pool and appears as an indented child of its pool.
- A partition maps to Disk, selects its parent physical disk, and remains the active unit shown in the operation heading.

## Upper operation area

The operation area always contains:

1. horizontal category tabs;
2. category-dependent vertical object selector;
3. object identity, health, scan time, properties, and relationships;
4. warning or blocking `InfoBar`;
5. one native `CommandBar` containing only implemented actions.

Unimplemented commands are absent rather than fake-enabled. Execution mode is always visible in the title bar and is not hidden in Settings.

### System

- Object: current computer.
- Information: OS, current user, privilege, mode, storage subsystem, object counts, protected disks, global health, last scan.
- Commands: Rescan, Export system snapshot, Copy diagnostic summary, View operation records.
- No storage mutation.

### Pool

- Objects: Unpooled, each pool, and indented virtual disks.
- Unpooled information: eligible capacity, rejected disks and reasons, selected candidates, storage subsystem.
- Unpooled commands: Rescan, Select candidate disks, Clear selection, Create storage pool.
- Pool information: identity, health, capacity, subsystem, members, tiers, virtual disks, volumes.
- Pool commands: Rescan, Export pool information, Copy pool summary, Create new storage pool.
- Virtual-disk information: identity, health, resiliency, columns, interleave, provisioning, size, pool, tiers, volumes.
- Virtual-disk commands: Rescan, View pool, View related volumes, Export virtual-disk information.

### Tier

- Objects: tiers grouped by pool.
- Information: role, media type, resiliency, columns, interleave, fault domain, supported/allocated/footprint size, pool, disks, virtual disks.
- Commands: Rescan, View pool, Locate member disks, Export tier information, Copy tier summary.
- No tier mutation in the first milestone.

### Disk

- Objects: all physical disks with protected, pooled, eligible, and health states.
- Information: number, model, masked serial, stable ID, bus/media type, capacity, sector size, health, `CanPool`, protection, pool/tier, partitions and volumes.
- Common commands: Rescan, Copy disk identity, View pool, View volumes.
- Eligible-disk commands: Add to candidate selection or Remove from candidate selection.
- With two or more eligible selections: Create storage pool from selected disks.
- Protected or ineligible disks show reasons and never show candidate-selection commands.

### Logical volume

- Objects: drive-letter volumes followed by unmounted volumes identified by label or stable ID.
- Information: letter, label, identity, file system, allocation-unit size, capacity, usage, health, mount points, partition, disk, virtual disk, pool, tier.
- Commands: Rescan, Open in File Explorer when mounted, Copy path, Copy volume information, View physical disk, View virtual disk, View pool.
- No format, resize, drive-letter, repair, or remove commands in the first milestone.

## Lower logic area

The logic area always displays the whole graph:

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

- Preserve the earlier frontend's nested block-enclosure effect.
- Relationships are shown only by physical containment of bordered blocks; do not draw tree branches, connector lines, leader lines, callouts, or arrows.
- Do not inherit the old colors, cut-corner buttons, or branded chrome yet. The bordered nesting geometry is the explicit exception.
- Nodes support select, containment-path highlight, expand, and collapse.
- The logic area contains no execution buttons.
- Duplicate references share one stable identity and selection state.
- Volumes below a virtual disk must not appear as direct children of one pool member physical disk.
- A category change keeps the entire graph visible but focuses and highlights the matching units.

## Responsive behavior

- Wide: vertical selector and object details sit side by side in the upper area.
- Medium: details reflow below the selector without removing the lower logic area.
- Narrow: the vertical selector becomes an overlay/flyout and the operation details become one column.
- The two main areas keep explicit minimum heights; the splitter cannot collapse either area completely.

## Storage-pool creation entry

Pool creation starts from Unpooled, an existing pool's category command, or an eligible multi-disk selection. All entries produce the same typed plan and review experience. The topology itself never executes the operation.
