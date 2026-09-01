# WinPool V0.44 Shared Runtime Staging Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** implemented 2026-09-01; automatic gates passed; targeted merged-directory process smoke passed; inherited OS-matrix and full human UI cases unverified
- **Created:** 2026-09-01
- **Baseline commit:** `3bfd6192561fc590e1db2b33b1257badb97cf841`
- **Implementation commit:** `3e00633`
- **Working branch:** `main`
- **Current product version:** V0.44
- **Target product version:** V0.44
- **Stage type:** portable staging union after runtime alignment; no new user feature

The developer’s informal staging draft is frozen under
[`../V0.44-shared-staging-draft`](../V0.44-shared-staging-draft/README.md).
This file is the frozen Plan for that stage. Product version **must not** be bumped.

The runtime-alignment experiment is recorded in
[`V0.44 App - Agent runtime alignment experiment.md`](V0.44%20App%20-%20Agent%20runtime%20alignment%20experiment.md).
It confirmed:

```text
same-name identical: 207 → 288
same-name different: 5 → 0
Agent-only:          83 → 7
unique combined:     about 232 MiB
```

The five previous collisions are resolved. That experiment does not authorize
this staging change by itself.

The developer has made the following controlling decisions:

1. App and Agent remain two processes.
2. App keeps `FrameworkReference` `Microsoft.WindowsDesktop.App.WindowsForms`
   and must not set `UseWindowsForms`.
3. App remains WinUI and must not add WinForms UI code.
4. Portable staging is the **collision-checked union** of two independent
   self-contained publishes.
5. Same relative path and identical SHA-256: store one file.
6. Same relative path and different SHA-256: fail staging. No last-writer-wins.
7. App-only and Agent-only files are kept.
8. The old full runtime tree under `Agent\` is removed from staging and from
   the local run tree.
9. Local `artifacts\$(Configuration)\` and formal staging use the **same flat
   layout**. Two launch paths are forbidden.
10. Formal staging excludes PDB. Build outputs may still contain PDB files.
11. `PublishTrimmed` remains false. Trimming, NativeAOT, process merger,
    framework-dependent shipping, custom probing, and manual DLL edits are out
    of scope.
12. This stage does not bump the product version.

Writing this Plan does not authorize implementation, push, tag, GitHub Release,
binary upload, deployment, or real storage mutation. Implementation starts only
after the developer explicitly asks to execute this Plan.

## 1. Objective

Keep two executables:

```text
WinPool.App.exe
WinPool.Agent.exe
```

Keep different project graphs:

```text
App
├── shared .NET / WindowsDesktop runtime baseline
├── WinUI / Windows App SDK
└── App-specific files

Agent
├── shared .NET / WindowsDesktop runtime baseline
├── WinForms
└── Agent-specific files
```

Ship one portable directory that is the safe union of both self-contained
trees. Shared identical files exist once. Future same-name different-content
files fail the package build.

## 2. Permanent safety

This stage does not change the V0.44 storage safety model.

- Real storage-structure mutation remains denied.
- Inventory and monitoring remain read-only with respect to storage structure.
- The Agent remains the only normal SQLite writer.
- IPC protocol and process ownership do not change.
- Free-form storage commands remain forbidden.

## 3. Runtime alignment (already in the tree)

Keep:

```xml
<FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />
```

in `WinPool.App.csproj`. Add a short comment that this reference exists only to
align App and Agent self-contained WindowsDesktop asset selection.

Do not enable `UseWindowsForms` on App.

Add architecture tests that:

- require that FrameworkReference;
- forbid `System.Windows.Forms` usage in App source.

## 4. Local run tree and staging share one layout

Target layout for **both** `artifacts\$(Configuration)\` and formal staging:

```text
WinPool/
├── WinPool.App.exe
├── WinPool.App.dll
├── WinPool.App.deps.json
├── WinPool.App.runtimeconfig.json
├── WinPool.Agent.exe
├── WinPool.Agent.dll
├── WinPool.Agent.deps.json
├── WinPool.Agent.runtimeconfig.json
├── shared .NET and WindowsDesktop runtime files
├── App-only WinUI / Windows App SDK files
├── Agent-only WinForms files
├── Assets/
├── PRI
└── XBF
```

The nested path `Agent/WinPool.Agent.exe` is retired.

Required contract updates:

- `Directory.Build.props` and `Directory.Build.targets` Agent `OutputPath` /
  `OutDir`
- `src/WinPool.App/WinPool.App.csproj` `BuildAgentRuntime` output and
  `PublishAgentRuntimeBesideApp` (must not publish Agent into `PublishDir\Agent\`)
- `src/WinPool.App/App.xaml.cs` Agent launch path
- `src/WinPool.App/Services/AgentStartupRegistration.cs`
- `build/Publish-Staged.ps1`
- `build/Rebuild-WinPool.ps1`
- `tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs`
- `docs/Development.md` and `docs/Development.zh-CN.md`
- `docs/Quality.md` and `docs/Quality.zh-CN.md`

`StorageDataLocationsTests` exclusion of `Agent\Data` is a data-root guard, not
an executable path. Do not change it unless a test actually breaks.

## 5. Independent publish then merge

App and Agent first publish into separate temporary directories:

```text
temp/App
temp/Agent
```

Both publishes remain `win-x64`, self-contained, `PublishTrimmed=false`.

Do not publish Agent directly into the App output directory. The existing
`PublishAgentRuntimeBesideApp` target is incompatible with this rule and must
be removed or skipped.

Final staging is rebuilt from an empty directory. Merge every relative path:

| Case | Action |
| --- | --- |
| App-only | copy the App file |
| Agent-only | copy the Agent file |
| same path, same SHA-256 | copy one file |
| same path, different SHA-256 | fail immediately |

Skip `*.pdb` during the union. Do not disable compiler symbol generation.

Do not delete framework files by hand after publish.

## 6. Permanent collision gate

Every staging build recalculates relative path + SHA-256 for both trees.

A later upgrade of .NET, WindowsDesktop, Windows SDK / TFM, Windows App SDK,
WinForms, or related framework packs must pass the same gate. A new unique
file is allowed. A new same-name different-content file is not.

If a future upgrade cannot be aligned, restore isolated App / Agent runtime
directories for that release only after an explicit developer decision. Do not
auto-fallback, auto-pick a copy, or add custom probing.

## 7. Validation

Automatic gate:

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

Staging checks:

- collision count is 0
- shared files are stored once
- App-only and Agent-only files remain
- Release staging contains no PDB
- PRI and XBF remain
- `PublishTrimmed=false`
- both executables are at the staging root with product version V0.44
- local `artifacts\$(Configuration)\` uses the same root paths
- App launch and startup registration resolve `WinPool.Agent.exe` beside App

Smoke from the **merged** staging directory:

App: cold start, WinUI, navigation, PRI/XBF, theme, language, Picker,
inventory, monitoring, Agent connection.

Agent: direct startup, tray icon and commands, IPC, SQLite, inventory,
monitoring, shutdown and restart.

Also: App launches Agent from the root path; startup registration uses that
path; portable run does not require a preinstalled .NET Runtime or Windows App
Runtime.

Unavailable OS-matrix cases stay `unverified`. They are not reported as passed.

## 8. Size report

Record nested-before versus flat-after: total size, file count, shared count,
App-only count, Agent-only count, collision count.

The alignment experiment’s unique set of about 232 MiB is a reference, not a
hard gate.

Implemented result:

```text
nested V0.44 baseline: 779 files, 338.40 MiB
flat union:            574 files, 231.58 MiB
shared:                281
App-only:              288
Agent-only:            5
collisions:            0
```

## 9. Non-goals

This stage does not merge processes, make the two graphs identical, remove
WinForms or WinUI, add WinForms UI to App, enable trimming or NativeAOT, switch
to framework-dependent shipping, add `AssemblyLoadContext` or custom probe
paths, replace framework DLLs by hand, change images, add product features, or
advance the product version past V0.44.

## 10. Work order

1. Comment and guard the App WindowsDesktop alignment reference.
2. Flatten local Agent output beside App; stop nested Agent publish.
3. Publish App and Agent into separate temporary directories.
4. Implement SHA-256 union merge with fail-fast collisions and PDB exclusion.
5. Point Agent launch and startup registration at the root executable.
6. Update staging scripts, Rebuild-WinPool, architecture tests, Development,
   and Quality.
7. Run the automatic gate.
8. Smoke the merged directory.
9. Record size and file counts in CHANGELOG.
10. After the result is confirmed, archive the alignment-experiment note.

Recommended commit split after authorization:

```text
build: flatten App and Agent local and staged runtime trees
docs: update portable layout and collision-gate rules
```

Split further if the implementation naturally separates script work from App
path changes.

## 11. Completion

The stage is complete when:

1. App and Agent remain separate processes at version V0.44.
2. They publish independently.
3. The portable union has zero same-name hash collisions.
4. Shared runtime files exist once.
5. App-only and Agent-only files remain.
6. Both executables run from the same local and staged root.
7. Agent launch and startup registration use that root path.
8. Future collisions fail staging.
9. Automatic gates pass or are truthfully `unverified`.
10. Merged-directory smoke passes.

Closing statement:

> Two independent WinPool processes keep separate dependency descriptions, a
> deliberately aligned shared runtime baseline, and one collision-checked
> portable dependency union.
