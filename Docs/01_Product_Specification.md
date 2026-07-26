# Product Specification / 产品规格

## Product statement

WinPool makes Windows Storage Spaces state understandable and management operations deliberate, reviewable, and auditable.

WinPool 让 Windows 存储空间的状态可理解，并使每一次管理操作都能够预览、确认、验证和审计。

## Primary users

- Advanced Windows users managing local multi-disk systems.
- Storage researchers reproducing Storage Spaces behavior.
- Administrators who need a safer interface over Storage cmdlets.

## Initial implementation scope

The first implementation discovers and correlates storage subsystems, physical disks, partitions, storage pools, storage tiers, virtual disks, and logical volumes. It then adds storage-pool creation as the only mutating operation.

No tier, virtual-disk, partition, volume, formatting, repair, optimization, removal, or Dite operation is included in the first management milestone.

## Single-window product structure

WinPool uses one main window and no outer page navigation.

- The title bar contains the Simulation/Real selector and one Settings button in the upper-right corner.
- An elevated process appends `[Administrator]` / `[管理员]` to the title; privilege is not repeated in the workspace.
- The upper operation area contains horizontal categories, a vertical object selector, object information, warnings, and applicable commands.
- The lower logic area always shows the complete storage topology.
- The lower topology uses nested bordered blocks for relationships and never uses connector lines.
- A horizontal `GridSplitter` separates the two areas; the default height ratio is 40:60.
- Settings replaces the complete workspace and contains theme and language only in the first milestone.

Horizontal categories remain in this exact order:

1. System / 系统
2. Pool / 池
3. Tier / 层
4. Disk / 磁盘
5. Logical volume / 逻辑卷

Virtual disks remain first-class objects but appear as indented children in the Pool selector rather than adding a sixth horizontal category. Partitions map to their parent physical disk in the Disk category.

## Execution modes

- Normal launches start in `Simulation`.
- A standard-user process can select `Real`; WinPool asks whether to restart through UAC.
- An approved one-time elevated restart enters `Real` directly. A manual administrator launch still starts in `Simulation`.
- Cancelling the dialog or UAC leaves the current process in `Simulation`; execution mode is never persisted.
- A mode change invalidates every unexecuted plan.
- Administrator state does not bypass preflight, review, confirmation, plan-hash validation, verification, or audit.
- Simulation and Real use the same typed plan. Simulation never invokes a mutating command and reports `Simulated`, not success.

## Initial management capability

Storage-pool creation is enabled only after the safety workflow in `04_Operation_Safety.md` exists.

- Create one storage pool from two or more explicitly selected eligible physical disks.
- Use stable target IDs, a uniquely identified storage subsystem, typed parameters, and a hashed plan.
- Treat pool creation as non-reversible unless a separate verified reverse operation is implemented later.

## Conceptual domain models

| Model | Responsibility |
|---|---|
| `StorageSubsystem` | Stable identity and Storage Spaces subsystem capabilities |
| `PhysicalDisk` | Identity, bus/media type, capacity, health, pool eligibility, protection |
| `Partition` | Disk-relative identity, type, offsets, size, and volume relation |
| `StoragePool` | Membership, health, capacity, allocation, operational status |
| `StorageTier` | Media role, resiliency, supported size, allocation |
| `VirtualDisk` | Layout, columns, interleave, resiliency, health, tier relations |
| `Volume` | File system, cluster size, capacity, mount points, health |
| `StorageUnitRef` | Stable reference used by selectors and topology nodes |
| `WorkspaceSelection` | Active category and selected storage unit |
| `OperationPlan` | Targets, mode, snapshot, intended change, command representation, checks, confirmation |
| `OperationResult` | Simulated/real state, timing, output, verification, partial failure |
| `AuditRecord` | Requested/effective mode, privilege, plan hash, snapshots, evidence, result |

These are conceptual types only. They do not freeze public serialization, database storage, or the final C#/Python boundary.

## Settings

- Theme: System, Light, or Dark; System is the default.
- Language: Simplified Chinese or English; Chinese is the default.
- Theme and language persist and apply immediately.
- Execution mode is visible in the title bar and is never stored as a preference.

## Non-goals for the design phase

- No WinUI scaffold or application code.
- No WinUI scaffold or executable frontend.
- No public API, database, service, installer, or update channel.
- No legacy frontend color or button-shape migration.
- No real storage operation or disk integration test on the current machine.
