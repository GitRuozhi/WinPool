# WinPool

[English](README.md) | [简体中文](README_CN.md)

WinPool is a third-party graphical interface for Windows storage systems. It is intended to replace the aging Disk Management and Storage Spaces graphical experiences built into Windows with one modern, coherent workspace.

WinPool is designed to:

- present clear hierarchical relationships between storage systems, pools, tiers, disks, virtual disks, partitions, and other storage objects;
- create storage objects with complete and reviewable parameters;
- provide modern storage testing and monitoring workflows;
- reserve stable integration points for developers;
- expose predictable, structured operations that are convenient for AI agents to understand and invoke.

## Current development stage

WinPool V0.31 is the current source integration checkpoint for the **V0.3** architecture line. It contains the merged V0.2 one-time architecture rewrite and the V0.31 documentation, directory, version, and four-process staging refactor. It is not a binary release or GitHub Release; V0.32 remains the user-confirmed manual acceptance checkpoint. The accepted V0.13 appearance remains the visual baseline while acceptance fixes and refactoring continue.

The remaining work is release hardening rather than an unstarted rewrite: full manual GUI/UAC/external-tool/lifecycle acceptance, long-running validation, data-location round trips, and reproducible packaging still need completion. Database schema, algorithm, and IPC versions remain independent of the product version.

The repository contains a runnable WinUI 3 application plus separate tray Agent, TestWorker, and one-shot elevated Broker processes. Stable internal Application contracts, typed named-pipe IPC, SQLite persistence, testing, monitoring, simulation, and deny-by-default execution boundaries are implemented without freezing a public API.

The application models one read-only local computer and multiple persistent simulated
storage systems. A captured KS/StatSys hardware report supplies the first simulation,
and WinPool can export a complete system document or import it as another editable
simulation.

At this stage:

- simulated storage data is a normal and approved development source;
- the backend does not need to implement every future real storage-management capability represented by the frontend;
- existing features and the former Test-page scope are being preserved and absorbed into the rewritten architecture;
- current Windows discovery is read-only and may remain incomplete while frontend behavior is refined;
- local discovery uses fixed, assembly-embedded read-only commands through Windows
  PowerShell 5.1; WinPool does not deploy a writable `.ps1` inventory file;
- the bundled KS reference system retains 154 structured results in 13 categories;
  a live local report collects all of them natively, except a small documented set
  that Windows does not expose through read-only queries;
- simulated renames, drive-letter changes, formatting, partition removal, disk-state
  changes, and pool optimization jobs affect simulation documents only;
- the Edit page now rehearses full simulated workflows — disk initialization with an
  optional MSR, partition extend/shrink/delete/create, drag-and-drop pool creation,
  and virtual-disk provisioning with reviewable 64K interleave and 64K cluster
  defaults — while every local mutation entry point stays disabled;
- the Test page orchestrates configured external DiskSpd, fio, Dite, RoboCopy,
  and RAMMap tools through typed adapters; those tools are discovered or installed
  separately and are not bundled with WinPool;
- the visible per-user tray Agent continues monitoring after the main window closes;
  it is not a service and a tray Exit requests a complete application shutdown;
- app data lives in `%LocalAppData%\WinPool` by default or in a portable `Data`
  folder next to the executable, with automatic migration when switching;
- during a normal launch, the tray Agent performs the fixed read-only inventory,
  persists a redacted normalized snapshot and bounded full document in SQLite v10,
  and serves the cached document to the UI; `machine.json` remains only as a
  no-Agent developer fallback;
- frontend requirements do not freeze a public API, database, plug-in contract, or C#/Python wire protocol.

The interface expresses the intended product direction. It must not be interpreted as a promise that every displayed workflow is already connected to a production backend.

## Product direction

### Storage management

WinPool aims to make Windows storage objects understandable as one connected system rather than a collection of unrelated management dialogs. The primary interface should show both:

- a focused workspace for the selected object, its properties, warnings, and applicable actions;
- a complete topology that preserves pool, tier, disk, virtual-disk, and partition relationships.

Future creation and management workflows should expose the complete relevant parameters, provide a reviewable plan, and make the resulting operation understandable before execution.

### Testing and monitoring

WinPool brings storage testing, health monitoring, performance observation, result comparison, and evidence export into the same product. The V0.3 integration work is complete only after its stated automatic gates; real-tool, long-duration, UAC, recovery, and full GUI acceptance remain V0.32 human gates.

### Developer and AI integration

Developer-facing and AI-facing integration should eventually use typed objects, stable identities, explicit parameters, structured results, and clearly separated read and write operations. No public integration contract is frozen yet.

## Current safety boundary

Real storage-structure mutation remains prohibited in the current application.

- It must not create, initialize, format, resize, repair, remove, or otherwise modify storage objects.
- File-scoped tests may write, read, verify, and clean only files registered under an explicitly selected test directory.
- Typed and audited system-support actions may clean approved temporary files, flush a volume, run TRIM/Optimize, adjust a registered test process, or temporarily change the power plan. Release UX must warn or ask first and restore reversible state.
- External tools remain separate installations and may be configured by path or installed through confirmed Settings actions.
- Simulation is the default and may be used for all frontend development and demonstrations.
- Mutating storage support requires a separately approved implementation stage and a disposable test environment.

## Technology

The current draft uses:

- C# and WinUI 3;
- .NET 10;
- Windows App SDK;
- an unpackaged, self-contained x64 desktop deployment model.

Developer setup, architecture, build commands, and implementation boundaries are documented in [DEVELOP.md](DEVELOP.md).

## Research background

WinPool grows from the WinPool Storage Spaces research project. Within the completed Windows 10 22H2 tests, the current tested recommendation is:

```text
64K interleave + 64K NTFS allocation unit size
```

Equivalent Windows 11 testing has not yet been completed.

## Project documents

- [README.md](README.md): user-facing English introduction.
- [README_CN.md](README_CN.md): user-facing Simplified Chinese introduction.
- [AGENTS.md](AGENTS.md): operational constraints for AI agents.
- [DEVELOP.md](DEVELOP.md): developer-facing architecture and workflow.

Historical product and implementation documents are archived outside this repository directory under `Old\Program\WinPool`. They are retained for reference but no longer constrain current development. The current plan and its non-authoritative management reference are in [Plan/](Plan/).

## Rights

No license is granted by this repository. All rights are reserved.
