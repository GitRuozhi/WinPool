# WinPool Development Guide

## Development status

WinPool V0.31 is the current source integration checkpoint on the **V0.3** architecture line. It contains the merged V0.2 one-time architecture rewrite and the V0.31 documentation, directory, version-source, and four-process staging refactor. It is not a binary release or GitHub Release; V0.32 requires user-confirmed manual acceptance. The accepted V0.13 appearance remains the visual baseline while acceptance defects and frontend architecture may be corrected.

V0.3 is the architecture-plan name; V0.31 is its current integration checkpoint. Schema, algorithm, and IPC versions remain independent. Outstanding work is tracked as V0.32 manual acceptance and later debt, not as an unimplemented architecture cutover.

The current solution has stable internal Application contracts, Agent-owned SQLite persistence, typed named-pipe IPC, a visible per-user tray process, an isolated TestWorker, and a one-shot elevated Broker. Real storage-structure mutation remains denied; simulation, read-only inventory, registered-directory testing, monitoring, and explicitly reviewed system-support actions form the V0.3 implementation boundary.

## Product goal

WinPool is a third-party Windows storage-system GUI intended to replace the aging Disk Management and Storage Spaces graphical interfaces.

The intended product will:

- show storage objects as one understandable hierarchy;
- create storage objects with complete, explicit, and reviewable parameters;
- provide modern testing and monitoring workflows;
- expose extension points for developers;
- make structured operations convenient for AI agents to inspect and invoke.

V0.2 does not freeze a public SDK or require every future real management operation, but every retained feature must pass through the rewritten internal architecture.

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
Directory.Build.props             Single V0.31 display/technical version source
WinPool.slnx
Plan/                             Current V0.3 plan and non-authoritative references
  Reference/                      Non-authoritative architecture-management reference
local-assets/                     Ignored developer-local art/source material
build/                            Reproducible publish/staging command and validation
src/
  WinPool.App/                    WinUI shell, pages, controls, and presentation adapters
  WinPool.Application/            Stable internal contracts and use-case policies
  WinPool.Domain/                 Structured identities and pure storage rules
  WinPool.Execution/              Immutable plans, authorization, gates, and executors
  WinPool.Agent/                  Visible tray runtime and SQLite single writer
  WinPool.Agent.Client/           Typed App-to-Agent client
  WinPool.Ipc/                    Closed named-pipe envelopes and handshake contracts
  WinPool.Infrastructure.Sqlite/  Versioned high-volume persistence
  WinPool.Infrastructure.Windows/ Fixed read-only inventory and Windows ports
  WinPool.Inventory/              Normalized inventory model
  WinPool.Monitoring/             Sampling, buffering, rollups, and sessions
  WinPool.Testing/                Test planning, workspaces, metrics, and evidence
  WinPool.Testing.Tools/          Typed external-tool adapters
  WinPool.ToolManagement/         Tool discovery and controlled installation planning
workers/
  WinPool.TestWorker/             Isolated supervised test execution
  WinPool.ElevatedBroker/         One-shot typed R3 execution process
tests/
  WinPool.*.Tests/                Per-layer and architecture regression suites
```

The repository root intentionally contains only four Markdown documents:

```text
README.md
README_CN.md
AGENTS.md
DEVELOP.md
```

Historical documents are stored under the parent project's `Old\Program\WinPool` directory and are not active constraints.

`local-assets` holds developer-local visual source material and is intentionally
ignored. It is neither generated output nor a build input: tracked code must not
depend on it. A future runtime-art or Git LFS decision requires explicit user
approval and a reproducible distribution design.

## Architecture

The V0.2 dependency direction is presentation/ports → Application → Domain. The
App never writes SQLite directly. Normal launches use the Agent for inventory,
workspace state, simulation documents, monitoring, test execution, history, and
evidence. The Agent owns the database write lease; TestWorker and elevated Broker
are short-lived, typed children rather than alternate persistence owners.

### WinPool.App

`WinPool.App` owns:

- application startup and the main window;
- title-bar navigation and execution-mode presentation;
- Manage, Edit, Test, Monitor, Development, and Settings pages;
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
- a fixed read-only PowerShell inventory provider;
- the persistent simulated-system repository;
- Agent-backed normalized inventory and full-document adapters; legacy
  `machine.json` remains only for no-Agent developer fallback;
- an assembly-embedded, stdin-driven Windows PowerShell 5.1 command runner;
- a staged hardware-report collection and storage-projection pipeline;
- a read-only PDH physical-disk performance sampler for the Monitor page;
- the data-location service (`StorageDataLocations`) that resolves the active
  data root. The standard root is `%LocalAppData%\WinPool`; portable mode uses a
  `Data` folder next to the executable. The tiny `storage-location.json` pointer
  always lives in the standard root; switching modes migrates existing data to
  the new root and keeps the old files. If the executable folder is not
  writable, portable mode is refused. Normal persistence is Agent-owned SQLite
  v10 plus evidence attachments; legacy JSON repositories remain only on
  explicit no-Agent developer fallback paths.

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

- an upper complete storage-topology workspace;
- a lower object-focused operation workspace;
- a horizontal splitter between them;
- System, Pool, Tier, Disk, and Partition categories shown as vertical tabs with
  horizontal icon-and-label items;
- one shared comparison table whose columns are the category objects and whose
  first column (property names) stays frozen while the value columns scroll
  horizontally; the object name is the first table row, value columns size to
  content and wrap long values instead of truncating them, every cell offers
  hover feedback, the selected column enables in-place text selection, and the
  selected column is highlighted and centered;
- category switches that prefer the object related to the current selection
  (partition to its disk, disk to its pool, tier to its pool, and the reverse
  child direction) before falling back to the remembered or first object;
- an Export list command on every category, named after the current category
  (Export [category] info list), that writes the table as CSV;
- a Delete simulation command on the System category that removes the selected
  simulation after confirmation and deletes its persisted file; the built-in
  simulation and the local system cannot be deleted;
- nested enclosure blocks for relationships, without connector lines, where each
  node carries a type icon inline at the type row and Windows-backed disks and
  partitions carry a four-square Windows mark right after the type icon.
Scan progress, scan completion, import/export results, and errors surface as
bottom-right notifications that dismiss automatically after a few seconds; the
in-progress scan notification stays until the scan finishes. Disk and
partition Properties commands open the Windows native experience (the Device
Manager device property sheet for disks, the volume properties dialog for
partitions), and the partition group also offers an Optimize drives command
that opens `dfrgui.exe`. These native commands are enabled for any
local-consistent system: the local system or a simulation whose recorded
source host name matches this machine. The System category's Convert local to
simulation command persists a redacted copy of the local system with the
source host name and activates it immediately; from such a copy the native
target is re-resolved against the live local snapshot at click time, and a
missing target raises a warning without permanently disabling the command.

The last shell page, active document, per-category selections, and topology
highlight persist through Agent-owned SQLite and are restored on the next launch.
`workspace.json` is only the no-Agent developer fallback; topology expansion
state and the execution mode are never persisted. `Ctrl+1` through `Ctrl+6`
select Manage, Edit, Test, Monitor, Development, and Settings without changing
the frozen title-bar appearance.
Right-clicking a topology node that is the current workspace selection opens a
context menu with the same commands as the operation-area buttons, placed at
the node's right edge (left edge when space runs out); right-clicking an
unselected node shows nothing.

The Edit page applies every mutating or destructive workflow to simulated systems
only: partition extend/shrink/delete/format/create, disk initialization with an
optional 16 MB MSR (a persisted setting, on by default), pool creation through
drag-and-drop disk assignment, and virtual-disk creation with reviewable
interleave, resiliency, and cluster-size parameters. All simulated operations are
recorded in a read-only command log shown on the Development page, and the
built-in simulation can be reset. Local storage remains read-only: every local
mutation entry point is disabled, even in Real mode.

The Monitor page is split into three areas separated by a draggable splitter,
each framed in its own card including the button row. The top graph area draws
one color per disk in a 60-second right-to-left window: activity as a dotted
line (0-100 percent fixed scale), read throughput as a dashed line, and write
throughput as a solid line with a translucent fill underneath; the throughput
scale adapts to the data with a 100 KiB/s floor and uses KiB/MiB units, the
activity axis label sits at the left and the speed scale at the right, and the
right edge reserves a gutter where each disk carries three left-pointing flag
labels (activity, read, write) with solid backgrounds that center on their
curve endpoints and cover each other in write-over-read-over-activity order. The middle table lists name,
owning pool, accessible volumes, media, capacity, activity, read, and write
per disk; a legend swatch in its own column shows the disk's graph color,
auto-assigned by default and pickable from a hue-sorted preset palette or a
hex RGB input (Enter or focus loss confirms), and the button row can reassign
all disk colors automatically; each row's checkbox controls whether that disk
is drawn in the graph (all disks are always sampled; disks with volumes start
checked, disks without volumes start unchecked). The bottom button row offers
a keep-monitoring-in-background checkbox (off by default; otherwise
monitoring runs only while the page is open and the window is not minimized),
a labeled sampling-rate selector (0.2/0.5/1/2/5/10/20 Hz, default 1) that
also drives the UI refresh cadence, an automatic-colors command, and
right-aligned start, stop, and CSV export commands; every button carries an
icon. The graph and table areas share an invisible draggable splitter, and on
the first page entry of each app run the split is computed so the table
exactly fits its rows when there are few disks and caps at 40 percent of the
workspace with a scrollbar beyond that; the ratio then stays as the user left
it for the rest of the run, is stored only in memory, and is recomputed on
the next launch. Monitoring starts when the
page opens. The independent tray Agent owns the long-running monitoring session,
bounded buffering, SQLite batch persistence, health-event subscription, and App
reconnection window. Samples come from read-only PDH English counters for both
physical and virtual disks, with disk activity capped at 100 percent.

The Test page is a complete V0.2 workflow for immutable plan review, configured
external DiskSpd/fio/Dite/RoboCopy/RAMMap adapters, registered test directories,
repeat execution, optional reviewed R3 support actions, live progress, history,
comparison, and evidence export. External tool engines are not reimplemented or
bundled with WinPool.

The settings page offers theme, accent, and language drop-downs (the language
defaults to following Windows and falls back to English for unsupported
cultures, switching immediately without a restart), the execution-mode
switch, the MSR-on-initialize option, a "show hardware IDs" privacy toggle
(off by default; enabling it warns about serial-number exposure and unmasks
sensitive hardware values in the UI only), a welcome-page toggle, and a data
location selector (standard `%LocalAppData%\WinPool` or a portable `Data`
folder next to the executable; switching migrates existing data). Product,
version, provider, website, update, feedback, and community links live in a
single About card. A welcome dialog with artwork and introduction text
appears at startup until the user clears the "show at startup" checkbox in
the dialog or in Settings. Pages no longer show a large in-page title; the
title-bar tabs carry the page identity.

On normal launches, the Agent executes the fixed read-only inventory, redacts the
full local document, projects a normalized snapshot, and binds both in SQLite v10.
The UI loads that cached document immediately while an Agent scan refreshes it.
Simulation documents are likewise Agent/SQLite-owned. `machine.json` and the
legacy `Systems` directory are used only by explicit no-Agent developer fallback.

The frontend should continue to support:

- simulation and real-system presentation;
- Chinese and English;
- System, Light, and Dark themes;
- Windows and preset accent colors;
- keyboard and accessible names;
- high-contrast-safe presentation;
- adaptive behavior as the window narrows.

Use standard WinUI controls and theme resources first. Keep custom controls narrowly focused on topology or verified layout needs.

Theme-correct color handling: code that reads theme resources programmatically
(`Application.Current.Resources[...]`) does not follow the app-level
`RequestedTheme` override and returns the OS-theme variant, which makes text
and divider brushes vanish in the opposite app theme. For programmatically
built UI, read computed values from hidden XAML probe elements whose
properties use `{ThemeResource ...}` (see `BrushProbes` in `MainPage.xaml`),
and rebuild such UI on `ActualThemeChanged`.

## Simulation and backend boundaries

Simulation is a normal development mode, not a fallback of last resort.

Use simulation when:

- a complex storage graph is needed for visual design;
- the local computer lacks a representative Storage Spaces configuration;
- a frontend workflow has no backend yet;
- testing the real system would introduce risk or unnecessary coupling.

Backend work should stay within the current internal contracts and approved acceptance scope. The Agent, SQLite persistence, monitoring, typed external-tool adapters, TestWorker, and one-shot Broker already exist; do not add a public SDK, general service, free-form command channel, bundled third-party engine, or real storage-structure mutation merely because the frontend shows a future capability.

Real mode currently demonstrates execution-mode and privilege UX. It does not authorize or enable storage mutation.

Simulation operations may update and persist simulated documents. They must never
resolve simulated identities to local storage commands.

## Build and test

From the WinPool repository root:

```powershell
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1
dotnet build src\WinPool.App\WinPool.App.csproj -c Release -p:Platform=x64
```

To publish and validate the self-contained four-process staging tree, pass a new,
empty path outside the repository. The script refuses to overwrite an existing path:

```powershell
.\build\Publish-Staged.ps1 `
  -OutputPath ..\..\Rubbish\20260810_winpool_v031_staging\Program\WinPool `
  -Configuration Release
```

The command publishes the App and its child projects into one staging root and
validates this exact runtime layout:

```text
<stage>/WinPool.App.exe
<stage>/Agent/WinPool.Agent.exe
<stage>/Agent/TestWorker/WinPool.TestWorker.exe
<stage>/Agent/Broker/WinPool.ElevatedBroker.exe
```

The staging check rejects duplicate child executables at the root and rejects
scripts, local assets, SQLite files, test results, and known external tools. Its
output is verification evidence only: do not commit it, package it, tag it, or
create a release from it during V0.31.

For frontend changes, build success alone is not sufficient. Launch the generated `WinPool.App.exe` and confirm:

- a responsive top-level window appears;
- the intended page and topology render;
- selection and navigation work;
- the relevant theme and language state remain usable;
- the layout remains understandable at the widths affected by the change.

Use simulation for normal frontend verification. Run real inventory only when the task specifically requires read-only Windows integration.

## Testing policy

The V0.31 automatic suite covers Core, Domain, Application,
Execution, Agent/IPC, SQLite persistence, inventory, monitoring, external-tool
planning/adapters, ToolManagement, TestWorker, and architecture boundaries. These
tests cover the deny-by-default and process-boundary design, but they do not
replace the manual GUI/UAC/external-tool/lifecycle matrix in
`Plan/16_V0.3文档与目录重构计划.md`.

New frontend work should favor:

- pure tests for topology, selection, and layout rules;
- fake inventory providers;
- deterministic simulation snapshots;
- mocked privilege and service state;
- visual and runtime checks for theme, localization, accessibility, and resizing.

Tests must not create or modify storage objects.

Hardware-specific inventory assertions should be separated from portable unit tests when the test suite is expanded.

## Near-term priorities

1. Complete the V0.32 manual acceptance matrix recorded in `Plan/16_V0.3文档与目录重构计划.md`.
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
