# WinPool Development Guide

[English](Development.md) | [简体中文（仅供阅读）](Development.zh-CN.md)

## Technology and deployment

WinPool uses C#, WinUI 3, .NET 10, Windows App SDK, CommunityToolkit components
where already justified, and an unpackaged self-contained Windows x64 deployment.
The SDK is pinned in `global.json`; the single project version is defined in
`Directory.Build.props`.

Portable delivery is the only implemented mode through V0.7. Signed MSIX work
is scheduled for V0.8–V0.9, and Microsoft Store submission is post-V1.0 work.
These roadmap entries do not authorize packaging work in an earlier Plan.

The portable artifact must be kept as one complete directory. Run
`WinPool.App.exe` with its `Agent`, `TestWorker`, `Broker`, framework, and
resource files in their staged relative locations. WinPool installs no Windows
service and opening the application does not itself require elevation. Exit all
WinPool processes before replacing program files; partially overwriting a live
directory is not a supported upgrade method.

V0.8–V0.9 MSIX acceptance must cover signing and package identity, clean install,
first launch, update, downgrade rejection, uninstall, repair, startup,
App/Agent activation, data locations, retention, and interrupted-update recovery
on the named Windows matrix. Post-V1.0 Store work additionally requires approved
privacy, support, certification, listing, and update-path material. Packaging,
account creation, upload, and publication each remain separately authorized.

The product consists of four processes:

- `WinPool.App`: WinUI shell, pages, presentation adapters, and user interaction.
- `WinPool.Agent`: visible per-user tray runtime, SQLite single writer, inventory,
  monitoring, orchestration, and lifecycle owner.
- `WinPool.TestWorker`: isolated supervised execution of registered test plans.
- `WinPool.ElevatedBroker`: one-shot process for reviewed typed R3 support actions.

## Repository structure

```text
README.md
README.zh-CN.md
AGENTS.md
AGENTS.zh-CN.md
Directory.Build.props
Directory.Build.targets
global.json
WinPool.slnx
docs/
  Product.md
  Development.md
  Quality.md
  Plan.md                         present only while a stage is active
  CHANGELOG.md
  Reference/
  Archive/
build/
  Publish-Staged.ps1
  Rebuild-WinPool.ps1
assets/                           tracked software-consumed resources
OriginArtWork/                    ignored user-managed source artwork
local-assets/                     ignored developer-local resources
artifacts/                        ignored local build output
src/
workers/
tests/
```

No root `Plan` or root `DEVELOP.md` is part of the current structure.

## Dependency and ownership model

The dependency direction is presentation and ports → Application → Domain.

- Domain contains identities and pure storage rules.
- Execution contains immutable plans, risk classification, authorization,
  preconditions, policy evaluation, simulation, replay, and explicit denial.
- Application owns stable internal use-case contracts and projections.
- Infrastructure.Windows owns read-only Windows integration and reviewed system
  support ports.
- Infrastructure.Sqlite owns persistence implementation; normal App code never
  writes SQLite directly.
- Agent.Client and Ipc own the closed App-to-Agent transport.
- Inventory, Monitoring, Testing, Testing.Tools, and ToolManagement own their
  respective models and typed adapters.
- App consumes Application contracts and presentation models.
- Agent owns the database write lease and supervises Worker and Broker children.

These contracts are internal. Do not freeze a public API, plug-in contract, IPC
wire protocol, or C#/Python interoperability format until Product permits it.

## Persistence and process lifecycle

The standard data root is `%LocalAppData%\WinPool`. Portable mode uses a
writable `Data` directory beside the executable. The standard-root
`storage-location.json` pointer selects the mode; a location switch verifies the
destination before making it active.

Normal launches use Agent-owned SQLite for inventory, workspace state, simulation
documents, monitoring, test history, evidence, and recovery. Only the current
schema is created or reopened. Older schemas are rejected without migration or
modification. The current schema revision and IPC protocol number are recorded
in [CHANGELOG](CHANGELOG.md) Compatibility notes, not as extra project versions.

User preferences do not live in SQLite: `settings.json` is their sole durable
authority. JSON stores remain only for explicitly supported no-Agent development
fallbacks.

The persistence ownership policy has two durable authorities:

- `settings.json` owns user preferences, including visual and language choices,
  startup intent, monitoring preferences, and user-selected external-tool paths.
- `winpool.db` owns inventory and workspace cache, simulation documents,
  external-tool detection results, monitoring, tests, audit, recovery, and
  process/session history. The Agent remains its only normal writer.

Product code must not silently erase an unknown data root.
`storage-location.json` is the one durable bootstrap exception because WinPool
must locate the active data root before opening either authority. IPC endpoint
files are rebuildable runtime state; diagnostics and managed-tool payloads are
auxiliary files with fixed directories and retention rules, not additional
state authorities.

WinPool is single-instance through Windows App SDK application lifecycle. Normal
relaunch activates the existing window. An approved elevation handoff waits for
the old process before the elevated successor claims the instance key. Execution
mode is never persisted.

## Execution and external-tool boundaries

Executor policy denies real storage-structure mutation until Product and a
confirmed Plan permit a typed path. Simulation editing and read-only discovery
are normal capabilities. Each permitted mutation must validate its exact
targets, show a reviewed preview, record an audit entry, and remain
deny-by-default until authorized. Authorization rules live in
[AGENTS](../AGENTS.md) and [Product](Product.md).

File testing requires an explicitly registered directory and run-owned files.
DiskSpd, fio, Dite, RoboCopy, and RAMMap remain external installations. Adapters
must validate fixed identities, targets, arguments, and output semantics; no
free-form command surface is allowed.

The embedded PowerShell inventory is fixed and read-only. It remains until a
native collector has equivalent field, identity, and degradation evidence.

## Build and staging

From the repository root:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File .\build\Rebuild-WinPool.ps1
```

The rebuild script stops running WinPool processes, removes regenerable `artifacts` output and leftover project `bin`/`obj` folders, rebuilds the four-process tree, and writes `WinPool.lnk` in the repository root and on the current-user Desktop. It does not touch Tests evidence, Research material, `%LocalAppData%\WinPool` data, `Old`, or `Rubbish`.

A manual build is:

```powershell
dotnet restore WinPool.slnx
dotnet build WinPool.slnx -c Release --no-restore -m:1
.\artifacts\Release\WinPool.App.exe
```

`dotnet build` writes generated files only under `artifacts`:

```text
artifacts\$(Configuration)\     four-process run tree
artifacts\obj\                  compiler intermediates
artifacts\build\                class-library and test outputs
```

`src`, `workers`, and `tests` stay source. The App/Agent/Worker/Broker projects do not copy one another after build.

Test commands and when to run them are defined in [Quality](Quality.md).

The reproducible self-contained staging command requires a new path outside the
repository and refuses to overwrite an existing path:

```powershell
.\build\Publish-Staged.ps1 `
  -OutputPath ..\..\Rubbish\YYYYMMDD_winpool_staging\Program\WinPool `
  -Configuration Release
```

The required layout is:

```text
WinPool.App.exe
Agent/WinPool.Agent.exe
Agent/TestWorker/WinPool.TestWorker.exe
Agent/Broker/WinPool.ElevatedBroker.exe
```

Staging must not contain duplicate child executables, scripts, source artwork,
unreferenced local assets, SQLite files, test results, external tools, or release
metadata. Software resources explicitly consumed by the application may be
included. Generated output is evidence only and is never committed.

## Version progression

Product versions use `Va.b` for a new product line and may use `Va.bc` for a
nonzero iteration within that line:

- `a`: major version;
- `b`: minor architecture/product line;
- `c`: one-digit nonzero iteration within the minor version.

Architecture and roadmap documents use `Va.b`. Iteration values are assigned
from actual work and cannot exceed 9. A nonzero iteration is recorded with a
local commit only when the user authorizes that commit; remote pushes, tags,
and releases follow [AGENTS](../AGENTS.md).

`Va.b` / `Va.bc` is the only project-version system. `Directory.Build.props`
derives the numeric fields required by .NET and Windows mechanically from `a`,
`b`, and `c`; those fields are build metadata with no independent version
meaning. Database schema revisions, algorithm IDs, and IPC compatibility
identifiers do not redefine the project version.

## Documentation lifecycle

Each fact has one owner:

- Product: long-term purpose, non-goals, boundaries, and roadmap.
- Development: architecture, module ownership, environment, build, version,
  and document workflow.
- Quality: stable gates, result vocabulary, acceptance classes, and when to
  run them.
- Plan: the only active formal stage, when one exists.
- CHANGELOG: important final results and compatibility changes.
- Archive: completed, superseded, or invalidated historical state.
- Reference: non-authoritative external or cross-project methods.
- AGENTS: operational, safety, authorization, reading, and Git rules.

A current user decision outranks a generic project-management reference. Archive
and Reference are never current requirements.

An unsuffixed Markdown file is authoritative. A matching `.zh-CN.md` file is a
Chinese reading copy only and must identify its unsuffixed authority. Documents
already written in Chinese do not need a duplicate Chinese copy. When an
authoritative document changes, update its reading copy in the same work item;
the reading copy never controls behavior, acceptance, status, or history.

When a stage is user-confirmed complete, record important final results in the
CHANGELOG, freeze the Plan under Archive with its real final state, update the
Archive index, and remove the active Plan if no next stage exists. Tags and
releases remain separately authorized.

## Contribution boundaries

- Preserve the deny-by-default execution and process ownership model.
- Do not add real storage mutation before a confirmed Plan in a Product-permitted
  phase defines the typed operation and the required explicit authorization flow.
- Keep software-consumed resources in tracked `assets`.
- Do not make tracked code depend on ignored `OriginArtWork` or `local-assets`.
- Do not couple WinPool to another repository through relative paths, copied live
  files, submodules, or runtime imports.
- Keep path moves, behavior changes, tests, and release actions independently
  reviewable.
