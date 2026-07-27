# WinPool Development Guide

## Development status

WinPool is currently in the **requirements-confirmed and frontend-visual-design stage**.

The immediate objective is a coherent, modern Windows storage interface. Backend completeness is not an acceptance requirement for this stage. Minimal read-only services, fake providers, and simulated storage graphs are appropriate when they allow frontend work to proceed safely.

The current executable should be treated as a frontend and interaction prototype with a small read-only backend, not as a completed storage-management product.

## Product goal

WinPool is a third-party Windows storage-system GUI intended to replace the aging Disk Management and Storage Spaces graphical interfaces.

The intended product will:

- show storage objects as one understandable hierarchy;
- create storage objects with complete, explicit, and reviewable parameters;
- provide modern testing and monitoring workflows;
- expose extension points for developers;
- make structured operations convenient for AI agents to inspect and invoke.

The current stage defines how these capabilities should appear and interact. It does not require all of them to be connected to a complete backend.

## Technology and deployment

The current solution uses:

- C#;
- WinUI 3;
- .NET 10;
- Windows App SDK;
- CommunityToolkit where an existing dependency already fills a specific gap;
- unpackaged, self-contained, Windows x64 deployment.

The SDK version used by the repository is pinned in `global.json`.

## Repository structure

```text
WinPool.slnx
src/
  WinPool.App/                    WinUI shell, pages, controls, view models, simulation data
  WinPool.Core/                   Domain records, selection state, topology and layout rules
  WinPool.Infrastructure.Windows/ Minimal read-only Windows services and inventory script
tests/
  WinPool.Core.Tests/
  WinPool.Infrastructure.Tests/
Ref/                              Read-only reference material (KS/StatSys collector and capture)
```

The repository root intentionally contains only four Markdown documents:

```text
README.md
README_CN.md
AGENTS.md
DEVELOP.md
```

Historical documents are stored under the parent project's `Old` directory and are not active constraints.

## Architecture

### WinPool.App

`WinPool.App` owns:

- application startup and the main window;
- title-bar navigation and execution-mode presentation;
- Manage and Settings pages;
- frontend view models;
- localization and appearance behavior;
- storage-topology controls and layout panels;
- the fixed simulation snapshot;
- frontend export and notification services.

Frontend code may use simulation-specific objects and presentation models when this keeps visual iteration clear and safe.

### WinPool.Core

`WinPool.Core` contains the current shared domain records and pure behavior:

- storage-unit identities;
- storage snapshots;
- workspace selection;
- topology projection;
- flow and weighted layout calculations;
- execution-mode state;
- service interfaces used by the current application.
- storage-system documents, complete hardware reports, simulation jobs, and pure
  simulation-operation validation;
- simulated storage mutations (rename, drive letter, format, delete, convert,
  offline, initialize with optional MSR, create/extend/shrink partition, create
  and populate pools, create virtual disks, drive optimization) with their
  reviewable PowerShell command text;
- storage-layout findings (busy pools, legacy dynamic volumes, MBR disks) and a
  read-only in-memory command log.

Sensitive hardware values (mainboard, memory, volume, and disk serial numbers
and MAC addresses) stay unmasked in memory so the privacy toggle can reveal
them, and are always masked again by
`StorageSystemDocumentSanitizer.RedactSensitiveData` before any value is
persisted, exported, or imported.

These types are internal architecture, not a frozen public SDK or serialization contract.

### WinPool.Infrastructure.Windows

`WinPool.Infrastructure.Windows` currently provides:

- privilege detection;
- the confirmed elevation-restart handoff;
- local preference persistence;
- a fixed read-only PowerShell inventory provider.
- the persistent simulated-system repository;
- the per-launch local machine record (`machine.json`, always redacted);
- an assembly-embedded, stdin-driven Windows PowerShell 5.1 command runner;
- a staged hardware-report collection and storage-projection pipeline;
- a read-only PDH physical-disk performance sampler for the Monitor page.

The inventory command may query Windows storage state, but it must remain read-only
and must not accept free-form user commands. No `.ps1` file is shipped. The fixed
command is embedded in the assembly and sent to the system Windows PowerShell 5.1
process over standard input.

The KS/StatSys migration preserves its 13 categories and 154 item identities in a
structured hardware report. A live local report collects all items through the same
fixed read-only command, except the few values Windows does not expose read-only
(storage-tier allocation unit, shared and total GPU memory, DirectX feature level,
monitor-to-GPU mapping, and monitor color format and dynamic range); those entries
are explicitly recorded as `Unavailable`. The bundled
`DESKTOP-PL96UKD_20260727_114130` reference report retains values for all 154 items.

### Storage-system documents

The application catalog contains the local system first, followed by persistent
simulations. Imports always create a new simulation identity. A versioned system
document contains the storage snapshot, hardware report, and simulated jobs. Imported
content is data only and is never evaluated as a command.

## Frontend baseline

The current shell uses title-bar destinations for:

- Manage;
- Edit;
- Test;
- Monitor;
- Development;
- Settings.

Only the frontend behavior required for current design validation needs to be functional.

The Manage page is based on:

- an upper object-focused operation workspace;
- a lower complete storage-topology workspace;
- a horizontal splitter between them;
- System, Pool, Tier, Disk, and Partition categories shown as vertical tabs;
- one shared comparison table whose columns are the category objects, with the
  selected column highlighted and centered;
- nested enclosure blocks for relationships, without connector lines.

The Edit page applies every mutating or destructive workflow to simulated systems
only: partition extend/shrink/delete/format/create, disk initialization with an
optional 16 MB MSR (a persisted setting, on by default), pool creation through
drag-and-drop disk assignment, and virtual-disk creation with reviewable
interleave, resiliency, and cluster-size parameters. All simulated operations are
recorded in a read-only command log shown on the Development page, and the
built-in simulation can be reset. Local storage remains read-only: every local
mutation entry point is disabled, even in Real mode.

The Monitor page samples local physical-disk activity, read, and write rates
through read-only PDH English counters. The Test page is a capability
placeholder for the planned Dite/RealSoak-style workflows.

The settings page offers theme, accent, and language drop-downs, the
execution-mode switch, the MSR-on-initialize option, and a "show hardware IDs"
privacy toggle (off by default; enabling it warns about serial-number exposure
and unmasks sensitive hardware values in the UI only). Product, version,
provider, website, update, feedback, and community links live in a single About
card.

Local machine information is refreshed into
`%LocalAppData%\WinPool\machine.json` after every successful scan; simulated
systems persist as redacted JSON documents in `%LocalAppData%\WinPool\Systems`.

The frontend should continue to support:

- simulation and real-system presentation;
- Chinese and English;
- System, Light, and Dark themes;
- Windows and preset accent colors;
- keyboard and accessible names;
- high-contrast-safe presentation;
- adaptive behavior as the window narrows.

Use standard WinUI controls and theme resources first. Keep custom controls narrowly focused on topology or verified layout needs.

## Simulation and backend boundaries

Simulation is a normal development mode, not a fallback of last resort.

Use simulation when:

- a complex storage graph is needed for visual design;
- the local computer lacks a representative Storage Spaces configuration;
- a frontend workflow has no backend yet;
- testing the real system would introduce risk or unnecessary coupling.

Backend work should be limited to the smallest change needed for the current frontend task. Do not implement complete command execution, monitoring infrastructure, Dite integration, databases, services, or plug-in systems merely because the frontend shows their intended location.

Real mode currently demonstrates execution-mode and privilege UX. It does not authorize or enable storage mutation.

Simulation operations may update and persist simulated documents. They must never
resolve simulated identities to local storage commands.

## Build and test

From the WinPool repository root:

```powershell
dotnet test WinPool.slnx -c Release
dotnet build src\WinPool.App\WinPool.App.csproj -c Release -p:Platform=x64
```

To produce the unpackaged self-contained build:

```powershell
dotnet publish src\WinPool.App\WinPool.App.csproj `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  --self-contained true
```

For frontend changes, build success alone is not sufficient. Launch the generated `WinPool.App.exe` and confirm:

- a responsive top-level window appears;
- the intended page and topology render;
- selection and navigation work;
- the relevant theme and language state remain usable;
- the layout remains understandable at the widths affected by the change.

Use simulation for normal frontend verification. Run real inventory only when the task specifically requires read-only Windows integration.

## Testing policy

Current tests cover pure Core behavior and the fixed read-only Windows inventory boundary.

New frontend work should favor:

- pure tests for topology, selection, and layout rules;
- fake inventory providers;
- deterministic simulation snapshots;
- mocked privilege and service state;
- visual and runtime checks for theme, localization, accessibility, and resizing.

Tests must not create or modify storage objects.

Hardware-specific inventory assertions should be separated from portable unit tests when the test suite is expanded.

## Near-term priorities

1. Refine requirements and remove ambiguity from the four authoritative root documents.
2. Stabilize frontend information architecture and visual behavior.
3. Improve responsive, bilingual, keyboard, and high-contrast behavior.
4. Expand simulation scenarios to cover representative storage hierarchies and abnormal states.
5. Keep the real Windows backend read-only and make only frontend-driven minimal changes.
6. Define developer and AI integration concepts only after the frontend object and operation model is stable.
7. Begin mutating backend design only after a separate user-approved plan and disposable hardware environment exist.

Candidate later workflows include pool membership and hot spares, virtual-disk
repair/connectivity, reliability counters and identification LEDs, partition access
paths and integrity checks, and storage health/diagnostic reports. These remain
design candidates rather than current local execution capabilities.

## Contribution boundaries

- Do not add local storage mutation in the current stage; simulated mutations on simulated documents are the only permitted write path, and every local mutation entry point must stay disabled, including in Real mode.
- Do not treat visible prototype functions as authorization to implement their backend.
- Do not create new documentation directories or planning files when one of the four root documents is the correct home.
- Do not use archived documents as current requirements.
- Do not commit, push, tag, or publish unless explicitly requested.
