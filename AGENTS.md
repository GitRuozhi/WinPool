# Agent Instructions for WinPool

This file defines the current operational rules for AI agents working in `Program\WinPool`.

The parent project rules in `..\..\AGENTS.md` also apply. When the two files differ, preserve the stricter safety, evidence, archive, and Git rule unless the user explicitly changes it.

## Authoritative documents

Only these four Markdown files at the WinPool repository root define the current documentation baseline:

1. `README.md` — user-facing English introduction.
2. `README_CN.md` — user-facing Simplified Chinese introduction.
3. `AGENTS.md` — AI-agent rules.
4. `DEVELOP.md` — developer architecture and workflow.

The user-approved Chinese V0.2 rewrite plan is stored under `Plan`. The four root
documents continue to describe the current V0.13 baseline until the rewrite cuts
over, while `Plan` is authoritative for V0.2 target architecture, scope,
sequencing, executor boundaries, and acceptance. When they differ, use the
current documents to understand V0.13 behavior, use `Plan` for the V0.2 target,
and always preserve the stricter safety rule.

Documents archived under the parent project's `Old` directory are historical references. They do not constrain current implementation and must not be treated as current requirements.

Do not recreate a `Docs` directory without explicit user approval.

## Current project stage

WinPool has a runnable V0.13 frontend baseline and is entering the user-approved
**V0.2 one-time architecture rewrite stage**.

The current priorities are:

- freeze the current appearance while allowing frontend fixes and refactoring;
- preserve every current feature in the new architecture;
- define stable internal Application contracts without freezing a public API;
- introduce SQLite for high-volume test and monitoring data;
- design the executor port and safety model now;
- replace the Test placeholder with a complete testing workflow that orchestrates
  DiskSpd, RoboCopy, fio, RAMMap, and other configured tools through typed adapters;
- split persistent monitoring from the main UI into a visible per-user tray
  process that communicates with the UI through named pipes;
- retain the current read-only PowerShell inventory until a native collector is
  implemented and validated against it.

The V0.2 backend does not need to implement every future real operation. It does
need enough architecture, simulation, persistence, testing, monitoring, and
deny-by-default execution behavior to support the complete V0.2 application.

## Backend policy

- Simulated data is an approved first-class development source.
- A frontend element does not require a complete production backend in the current stage.
- Prefer simulation or read-only providers for capabilities that cannot safely
  run on the current machine.
- Do not freeze a public API, plug-in contract, IPC protocol, or C#/Python wire format.
  Current persistence and typed IPC are internal and may change without migration.
  Normal launches use Agent-owned SQLite v10; legacy JSON stores remain only for
  explicitly supported no-Agent developer fallback paths.
- KS/StatSys read-only collection has been explicitly requested. Preserve its
  structured report boundary and current scripts while native alternatives are
  evaluated; do not turn the Flask prototype into the production boundary.
- Keep the current Windows inventory code read-only and retain its embedded
  PowerShell implementation until the native replacement has been validated.
- Designing the executor port, immutable plans, policy engine, authorization
  model, simulation executor, replay executor, and explicit deny implementation
  is approved.
- Do not add or enable operations that change real storage structure: disk
  initialization/state conversion, partition creation/deletion/resizing/format,
  Storage Pool/Tier/VirtualDisk creation/deletion/resizing/repair, or equivalent.
- The current machine may perform file-scoped tests in an explicitly selected
  test directory. Creating, writing, reading, verifying, and cleaning only the
  registered test files is allowed.
- Development may perform explicitly planned temporary-file cleanup, RAMMap
  system-cache/standby-list cleanup, volume flush, TRIM/Optimize, process
  priority/CPU-affinity adjustment, and temporary power-plan changes. The final
  product must warn or ask before these actions, record them, and restore
  reversible settings.
- DiskSpd, RoboCopy, fio, RAMMap, and other benchmark or test-support tools
  remain external tools. Do not reimplement their engines in C# or Win32. They
  are not bundled; Settings provides discovery, official installation actions,
  and custom paths.
- Sensitive hardware values may remain unmasked in memory to support the
  privacy toggle, but everything persisted, exported, or imported must pass
  through `StorageSystemDocumentSanitizer.RedactSensitiveData`.
- Do not store or publish a `.ps1` inventory file. Fixed read-only PowerShell text
  must be embedded in the assembly and provided through standard input.

V0.2 may replace the current project structure and add the projects and internal
dependencies defined in `Plan`. Do not preserve old boundaries merely for
compatibility.

## Product direction

WinPool is a third-party Windows storage-system GUI intended to replace the aging Disk Management and Storage Spaces graphical experiences.

The product direction includes:

- clear hierarchical storage-object relationships;
- complete and reviewable parameters when creating storage objects;
- modern testing and monitoring;
- reserved developer integration points;
- structured operations suitable for AI-agent invocation.

These are product goals, not claims that the current backend already implements them.

## Current frontend baseline

- Use a single WinUI 3 desktop window on .NET 10.
- Keep title-bar destinations for Manage, Edit, Test, Monitor, Development, and Settings.
- The Manage workspace uses an upper complete topology area and a lower operation area separated by a horizontal splitter.
- The lower area shows System, Pool, Tier, Disk, and Partition as vertical tabs with horizontal icon-and-label items, one shared comparison table (columns are the category objects, the object name is the first row, and the property-name column stays frozen during horizontal scrolling), and command buttons with icons.
- Table value columns size to content and wrap long values; every cell offers hover feedback, the selected column enables in-place text selection, and selecting a value cell highlights the column, centers it horizontally, and stays in sync with topology selection. Switching categories prefers the object related to the current selection before the remembered or first object. Every category button group ends with an Export list command that writes the table as CSV.
- The upper topology shows the complete storage structure through nested enclosure blocks; containment is the relationship language, with no relationship lines. Each node carries a type icon inline at the type row, unhealthy objects carry badges, and Windows-backed disks and partitions carry a four-square Windows mark right after the type icon. System root names carry [Local]/[Simulation] prefixes.
- Scan progress, scan results, import/export results, and errors surface as auto-dismissing bottom-right notifications; the in-progress scan notification remains until replaced by the completion notification.
- Disk and partition Properties commands open the Windows native experience on the local machine (the Device Manager device property sheet for disks, the volume properties dialog for partitions) and the partition group also offers an Optimize drives command that opens `dfrgui.exe`. These native commands are enabled whenever the selected system is local-consistent: the local system itself or a simulation whose recorded source host name matches this machine (created by the System category's "Convert local to simulation" command, which persists a redacted copy of the local system and activates it immediately). From a local-consistent simulation the target is re-resolved against the live local snapshot at click time (partition by disk and partition number, disk by OS disk number); a missing target raises a warning notification without permanently disabling the command.
- All mutating and destructive workflows live on the Edit page and act on simulated systems only; Manage keeps entries that navigate to Edit.
- The Monitor page samples local physical and virtual disks through read-only PDH counters (activity capped at 100 percent) and presents a task-manager-style graph (dotted activity, dashed read, solid write with translucent fill, 60-second window, per-disk colors, selectable per row), a disk table, and start/stop/export commands with icons. Every monitoring session records incrementally to `<data root>\Monitoring\yyyyMMdd_HHmmss.csv`; background monitoring is opt-in and monitoring otherwise pauses when the page is closed or the window is minimized. The Development page shows a read-only log of executed commands; the Test page is a placeholder.
- A welcome dialog with artwork and introduction text appears at startup until disabled through its "show at startup" checkbox or the matching Settings toggle.
- On a normal launch, the Agent performs the fixed read-only inventory, persists a
  redacted normalized snapshot plus a bounded SHA-256-protected full local document
  in SQLite v10, and serves the cached document while a background scan refreshes it.
  `machine.json` is only a no-Agent developer fallback. Workspace page, document,
  per-category selections, and topology highlight are likewise Agent/SQLite-owned in
  normal launches; `workspace.json` is only the no-Agent fallback. Topology expansion
  state is not persisted.
- All persistence resolves through `StorageDataLocations`: the standard root is `%LocalAppData%\WinPool`, portable mode uses a `Data` folder next to the executable, and the `storage-location.json` pointer in the standard root selects the mode. Switching modes migrates existing data and keeps the old files; a non-writable executable folder refuses portable mode.
- Workspace selection and topology highlighting remain independent where the current interaction requires it.
- Network and other logical storage groups must not be counted as Windows Storage Pools.
- Pages show no large in-page titles; the title-bar tabs carry the page identity. Right-clicking the currently selected topology node opens a context menu mirroring the operation-area buttons; right-clicking an unselected node shows nothing. The language follows Windows by default with an English fallback for unsupported cultures and switches immediately from Settings.
- Use stock, theme-aware WinUI controls and resources while the visual system is still being refined.
- Preserve light, dark, system-theme, high-contrast, keyboard, and bilingual design considerations.

Prefer built-in WinUI controls and shared resources. Add custom controls only when the storage-topology requirement or a verified platform limitation justifies them.

## Execution-mode boundary

- Normal launches start in Simulation.
- Execution mode is never persisted.
- A standard-user process may demonstrate the confirmed UAC restart flow when Real is selected.
- Real currently changes privilege and frontend state only.
- Real must not enable storage mutation in this stage.
- A manual administrator launch still starts in Simulation.

## Safety rules

1. Do not initialize, clear, format, resize, repair, remove, or create real
   disks, partitions, volumes, Storage Pools, Storage Tiers, or Virtual Disks.
   Simulation remains the only implementation for those storage-structure
   operations, including in Real mode.
2. File-based tests may use only an explicitly selected test directory and may
   touch only files registered to that test run. Raw-device writes are forbidden.
3. Temporary-file cleanup must exclude Windows Update, component store,
   installer, recovery, file-protection, and other protected operating-system
   data. Volume flush, TRIM/Optimize, process scheduling changes, and temporary
   power-plan changes require a typed plan and audit; release builds must warn or
   ask before execution.
4. Do not expose unmasked serial numbers in clipboard, logs, persisted files, or
   exports. The settings privacy toggle may reveal them in UI text only.
5. Do not directly delete files.
6. Move superseded WinPool content to the parent project root `Old`, preserving its relative path when practical.
7. Move low-value generated material to the parent project root `Rubbish`.
8. Do not create local `Old`, `old`, `OLD`, `旧`, or `Rubbish` directories inside the WinPool repository.
9. Do not commit, push, tag, publish, or create a release unless explicitly requested.
10. Preserve unrelated user changes in a dirty working tree.

## Documentation rules

- Keep exactly the four authoritative Markdown files at the WinPool repository root unless the user explicitly changes this policy.
- User-facing wording belongs in `README.md` and `README_CN.md`.
- AI operational rules belong in `AGENTS.md`.
- architecture, setup, and developer workflow belong in `DEVELOP.md`.
- The explicitly approved V0.2 rewrite plan belongs under `Plan` and may contain
  multiple Chinese Markdown files.
- Do not use archived documents as active requirements.
- Do not create another planning directory beside `Plan`.

## Verification

For documentation-only changes:

- verify that the WinPool root contains only the four approved Markdown files;
- verify all `Plan` files and their internal links when the V0.2 plan changes;
- verify all links and referenced paths;
- verify English and Chinese product statements describe the same stage and scope.

For application changes:

- build the affected project;
- run relevant tests;
- launch the unpackaged x64 application;
- confirm a responsive top-level window and the expected UI;
- use simulated data for frontend verification unless real read-only inventory is specifically needed.

Real hardware mutation is not an accepted verification method.
