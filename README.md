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

WinPool is currently in the **requirements-confirmed and frontend-visual-design stage**.

The repository contains a runnable WinUI 3 frontend draft used to validate information architecture, visual hierarchy, navigation, topology presentation, localization, theming, and interaction behavior. Backend work is intentionally limited to the minimum needed to support frontend development.

The draft now models one read-only local computer and multiple persistent simulated
storage systems. A captured KS/StatSys hardware report supplies the first simulation,
and WinPool can export a complete system document or import it as another editable
simulation.

At this stage:

- simulated storage data is a normal and approved development source;
- the backend does not need to implement every capability represented by the frontend;
- visible Create, Test, Monitor, and Development experiences may be prototypes or placeholders;
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
- the Monitor page observes local physical-disk activity and throughput through
  read-only performance counters, and the Development page shows a read-only log of
  every command WinPool executes;
- local machine information is refreshed into `%LocalAppData%\WinPool\machine.json`
  on every launch, always with sensitive serial numbers masked;
- frontend requirements do not freeze a public API, database, plug-in contract, or C#/Python wire protocol.

The interface expresses the intended product direction. It must not be interpreted as a promise that every displayed workflow is already connected to a production backend.

## Product direction

### Storage management

WinPool aims to make Windows storage objects understandable as one connected system rather than a collection of unrelated management dialogs. The primary interface should show both:

- a focused workspace for the selected object, its properties, warnings, and applicable actions;
- a complete topology that preserves pool, tier, disk, virtual-disk, and partition relationships.

Future creation and management workflows should expose the complete relevant parameters, provide a reviewable plan, and make the resulting operation understandable before execution.

### Testing and monitoring

WinPool is intended to bring storage testing, health monitoring, performance observation, result comparison, and evidence export into the same product. These areas are part of the frontend and product design, but their complete backend integration is not required in the current stage.

### Developer and AI integration

Developer-facing and AI-facing integration should eventually use typed objects, stable identities, explicit parameters, structured results, and clearly separated read and write operations. No public integration contract is frozen yet.

## Current safety boundary

The current application is a frontend and read-only inspection draft.

- It must not create, initialize, format, resize, repair, remove, or otherwise modify storage objects.
- The Real mode control currently represents execution-mode and privilege UX only.
- The Local real operations checkbox currently represents privilege UX only and shows
  a dismissible preview warning.
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

Historical product and implementation documents are archived outside this repository directory under the parent project's `Old` tree. They are retained for reference but no longer constrain current development.

## Rights

No license is granted by this repository. All rights are reserved.
