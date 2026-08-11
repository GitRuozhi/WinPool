# WinPool Development Guide

[English](Development.md) | [简体中文（仅供阅读）](Development.zh-CN.md)

## Technology and deployment

WinPool uses C#, WinUI 3, .NET 10, Windows App SDK, CommunityToolkit components
where already justified, and an unpackaged self-contained Windows x64 deployment.
The SDK is pinned in `global.json`; the single project version is defined in
`Directory.Build.props`.

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
Directory.Build.props
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
assets/                           tracked software-consumed resources
OriginArtWork/                    ignored user-managed source artwork
local-assets/                     ignored developer-local resources
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

These contracts are internal. V0.3 does not freeze a public API, plug-in contract,
IPC wire protocol, or C#/Python interoperability format.

## Persistence and process lifecycle

The standard data root is `%LocalAppData%\WinPool`. Portable mode uses a
writable `Data` directory beside the executable. The standard-root
`storage-location.json` pointer selects the mode; migrations retain the old copy
and verify the destination.

Normal launches use Agent-owned SQLite v10 for inventory, workspace state,
simulation documents, monitoring, test history, evidence, and recovery. JSON
stores remain only for explicitly supported no-Agent development fallbacks.

WinPool is single-instance through Windows App SDK application lifecycle. Normal
relaunch activates the existing window. An approved elevation handoff waits for
the old process before the elevated successor claims the instance key. Execution
mode is never persisted.

## Execution and external-tool boundaries

Real storage-structure mutation is denied by policy and executor behavior.
Simulation editing and read-only discovery are normal capabilities.

File testing requires an explicitly registered directory and run-owned files.
DiskSpd, fio, Dite, RoboCopy, and RAMMap remain external installations. Adapters
must validate fixed identities, targets, arguments, and output semantics; no
free-form command surface is allowed.

The embedded PowerShell inventory is fixed and read-only. It remains until a
native collector has equivalent field, identity, and degradation evidence.

## Build, test, and staging

From the repository root:

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

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

Product versions use `Va.bc`:

- `a`: major version;
- `b`: minor architecture/product line;
- `c`: one-digit iteration within the minor version.

Architecture and roadmap documents normally stop at `Va.b`. Iteration values are
assigned from actual work and cannot exceed 9. A normal iteration is committed
locally; remote pushes, tags, and releases require the authorization rules in
`AGENTS.md` and the active Plan.

`Va.bc` is the only project-version system. `Directory.Build.props` derives the
numeric fields required by .NET and Windows mechanically from `a`, `b`, and `c`;
those fields are build metadata with no independent version meaning. Database
schema revisions, algorithm IDs, and IPC compatibility identifiers do not
redefine the project version.

## Documentation lifecycle

Each fact has one owner:

- Product: long-term purpose, non-goals, boundaries, and roadmap.
- Development: architecture, module ownership, environment, build, version, Git,
  and document workflow.
- Quality: stable gates, result vocabulary, acceptance classes, and exceptions.
- Plan: the only active stage, current decisions, work, evidence, and completion
  criteria.
- CHANGELOG: results that actually occurred.
- Archive: completed, superseded, or invalidated historical state.
- Reference: non-authoritative external or cross-project methods.

A current user decision outranks a generic project-management reference. Archive
content is read-only history and cannot become a current requirement merely
because it is detailed.

An unsuffixed Markdown file is authoritative. A matching `.zh-CN.md` file is a
Chinese reading copy only and must identify its unsuffixed authority. Documents
already written in Chinese do not need a duplicate Chinese copy. When an
authoritative document changes, update its reading copy in the same work item;
the reading copy never controls behavior, acceptance, status, or history.

When a stage is user-confirmed complete, update the CHANGELOG, freeze the Plan
under Archive with its real final state, update the Archive index, and remove the
active Plan if no next stage exists. Tags and releases remain separately
authorized actions.

## Contribution boundaries

- Preserve the deny-by-default execution and process ownership model.
- Do not add real storage mutation without a separately confirmed plan and
  disposable environment.
- Keep software-consumed resources in tracked `assets`.
- Do not make tracked code depend on ignored `OriginArtWork` or `local-assets`.
- Do not couple WinPool to another repository through relative paths, copied live
  files, submodules, or runtime imports.
- Keep path moves, behavior changes, tests, and release actions independently
  reviewable.
