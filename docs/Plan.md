# WinPool V0.44 Platform Upgrade and Distribution Slimming Plan

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## 0. Status, authority, and baseline

- **Plan status:** confirmed scope; implementation not started
- **Created:** 2026-09-01
- **Baseline commit:** `407c1e92c1493dd41b608d0b5693715ffd22382e`
- **Working branch:** `main`
- **Current product version:** V0.43
- **Target product version:** V0.44
- **Stage type:** platform modernization and portable-distribution slimming; no new user feature

Handwritten Chinese drafts that preceded this Plan are frozen under
[`Archive/V0.44-draft`](Archive/V0.44-draft/README.md). They are historical
input. This file is the only active Plan.

The developer has made the following controlling decisions:

1. V0.44 upgrades Windows App SDK from the current 1.8 series to **2.4 Stable**.
   The current stable package is **2.4.0**. A later 2.4.x servicing build may be
   used if it exists at implementation time. Preview and experimental packages
   are out of scope.
2. V0.44 upgrades Windows SDK tooling from the current 26100 series toward the
   **28000 series**, subject to the TFM probe in WP1.
3. .NET remains **.NET 10**. This stage does not change the .NET major version
   and does not relax `global.json` to chase a Windows TFM.
4. The published minimum Windows version becomes **Windows 10 22H2 x64**.
5. Older Windows versions may receive non-promised compatibility checks. They
   are not part of the published support matrix and must not block V0.44.
6. Windows 11 24H2 and 25H2 are named primary platforms. Newer still-supported
   Windows 11 versions may be added after verification.
7. V0.44 continues to ship only the current **unpackaged, portable, fully
   self-contained x64** mode.
8. Framework-dependent, Lite, single-file, MSIX, ARM64, and x86 distributions
   belong to later product plans, not V0.44.
9. `WinPool.App` and `WinPool.Agent` remain two processes. The preferred
   portable tree stores shared self-contained runtime files once.
10. Windows App SDK must be consumed as the components WinPool actually uses.
    Unused AI, ML, ONNX, DirectML, Widgets, Search, and similar metapackage
    payloads must not enter the portable tree only because the top-level
    metapackage was referenced.
11. Agent keeps the existing WinForms tray. V0.44 does not replace it with
    `Shell_NotifyIcon`.
12. Welcome images and other existing image bytes are not compressed, resized,
    re-encoded, or converted.
13. Formal staging and the distribution ZIP contain no PDB. Build and diagnostic
    outputs continue to keep PDB files.
14. V0.44 does not enable .NET trimming and does not treat trimming as an
    experimental completion item.
15. V0.44 sets no absolute MiB, percentage, or other hard size gate. Size is
    recorded; the developer judges whether the result is acceptable.
16. V0.44 does not add user features, split large business modules, or open
    real storage-structure mutation.
17. `net10.0-windows10.0.28000.0` is attempted on the pinned .NET SDK. If that
    TFM is rejected (including NETSDK1140), V0.44 keeps the highest Windows TFM
    the pinned SDK accepts, currently expected to be the 26100 series, and may
    still update `Microsoft.Windows.SDK.BuildTools` to a 28000-series package
    that restore accepts. Failure to retarget the TFM to 28000 is not by itself
    a V0.44 stop condition.
18. Windows-targeted projects are unified onto one Windows TFM before App and
    Agent outputs are merged. The current split (App `26100`, Agent and other
    Windows projects `19041`) is not carried into the merge step.
19. The local `artifacts\$(Configuration)\` run tree and the portable staging
    tree use the same process layout. V0.44 does not keep a nested Agent path
    for local runs and a flat path for staging.
20. Flattening App and Agent into one directory is the preferred size result.
    If same-named files differ and cannot be reconciled without process merger,
    trimming, or dropping WinForms, the nested `Agent\` layout may remain and
    V0.44 can still close on the platform upgrade, componentization, and PDB
    exclusion.
21. Product version metadata changes to V0.44 after the platform upgrade is
    stable, not as the first code change.
22. `TargetPlatformMinVersion` stays `10.0.17763.0` unless Windows App SDK 2.4
    itself requires a higher value. V0.44 does not add a runtime lock whose only
    purpose is to refuse older Windows.

Writing this Plan does not authorize implementation, push, tag, GitHub Release,
binary upload, deployment, or real storage mutation. Implementation starts only
after the developer explicitly asks to execute this Plan.

## 1. Objective and required outcome

V0.43 remains the product-capability baseline: topology, simulation editing,
monitoring, settings, tray Agent, IPC protocol 4, and SQLite schema 14. V0.44
keeps that capability and reduces third-party runtime cost.

The completed stage must have all of these properties:

1. The product version source reads **V0.44 / 0.4.4**.
2. Windows App SDK is **2.4 Stable** and no 1.8 package remains in the active
   graph.
3. Windows SDK tooling is on a recorded, consistent baseline: 28000 series if
   the TFM probe succeeded, otherwise the documented fallback.
4. .NET remains .NET 10, with `PublishTrimmed=false`.
5. Published support text uses **Windows 10 22H2 x64** as the minimum supported
   version, not “this binary cannot start below build 28000”.
6. App and Agent remain independent executables.
7. The portable tree still runs without a preinstalled .NET runtime or Windows
   App Runtime.
8. Unused Windows App SDK AI/ML-class components are absent from both the NuGet
   graph and the staging tree.
9. Shared self-contained runtime files are stored once, or the nested layout is
   explicitly retained under decision 20 with the duplicate-size evidence
   recorded.
10. Agent WinForms tray behavior is unchanged.
11. Existing image bytes are unchanged.
12. Release staging contains no `.pdb`.
13. Build diagnostic symbols remain available.
14. WinUI XAML, PRI, XBF, startup, navigation, theme, language, picker, Agent,
    IPC, inventory, monitoring, and SQLite behavior have no known V0.44
    regression.
15. Real storage-structure mutation remains denied.

Size reduction is evidence, not a substitute for a correct dependency graph or
portable self-contained behavior.

## 2. Permanent safety and product boundaries

V0.44 does not change the V0.43 storage safety model.

- Real disk, partition, volume, Storage Pool, Storage Tier, and Virtual Disk
  mutation remains denied. This stage does not implement V0.5 operations.
- Simulation remains the only storage-edit execution path.
- Inventory and monitoring remain read-only with respect to storage structure.
- The Agent remains the only normal SQLite writer.
- App and Agent continue to use the existing constrained IPC.
- No free-form command, script, plug-in, or public automation surface is added
  because a newer SDK exposes it.
- APIs that exist only on newer Windows than 22H2 require an explicit version
  check and fallback, or they stay out of V0.44.
- Platform upgrade must not weaken IPC authentication, pipe ACLs, process
  identity checks, or protocol boundaries.
- Platform upgrade must not change SQLite ownership or writer rules.

A Windows SDK version is a compile-time API ceiling. It is not the published
minimum OS.

## 3. Platform baseline

### 3.1 .NET

```text
.NET 10
RuntimeIdentifier: win-x64
```

This stage does not perform a .NET major upgrade, NativeAOT, trimming,
single-file conversion, or ReadyToRun experiments. `PublishTrimmed=False`
remains.

### 3.2 Windows SDK

Current facts at the baseline commit:

- App TFM: `net10.0-windows10.0.26100.0`
- Agent and other Windows projects: `net10.0-windows10.0.19041.0`
- `Microsoft.Windows.SDK.BuildTools`: `10.0.26100.7705`
- The development machine at Plan writing had Windows SDK `10.0.26100.0`
  installed and .NET SDK `10.0.400` from `global.json`
- .NET 10's TFM allowlist has rejected `10.0.28000.0` on some SDK builds
  (NETSDK1140)

WP1 therefore probes before committing a 28000 TFM:

1. Restore and build a Windows-targeted project with
   `net10.0-windows10.0.28000.0` on the pinned SDK.
2. If that succeeds, unify Windows-targeted WinPool projects onto that TFM and
   update BuildTools to a matching 28000-series package.
3. If it fails, keep the highest accepted Windows TFM, unify Agent and other
   Windows projects onto that same TFM, and record the fallback in the stage
   notes. BuildTools may still move to 28000 if restore accepts the package.
4. Do not unpin `global.json` (`10.0.400`, `rollForward: latestPatch`) to force
   28000 TFM support.

Compiling against a newer SDK does not authorize calling 28000-only APIs on
Windows 10 22H2.

### 3.3 Windows App SDK

Current package: `Microsoft.WindowsAppSDK` `1.8.260416003`.

V0.44 target: **2.4 Stable**. Windows App SDK 1.8 is in maintenance until
2026-09-09; that date explains urgency, not permission to skip gates.

The first 2.4 step keeps a functionally complete dependency set and restores
current behavior. Componentization is a later work package.

Upgrade checks:

- App start
- XAML initialization
- PRI/XBF load
- Windowing
- Folder/File picker
- Theme and accent color
- Runtime language switch
- DPI and basic multi-display behavior
- Agent start and shutdown
- App-Agent IPC
- unpackaged self-contained publish

`CommunityToolkit.WinUI.Controls.Sizers` is a known third-party risk. If it
fails on 2.4, use a compatible toolkit version or a minimal local fix. Do not
remove the manage-page splitter to make the upgrade look smaller.

The existing App publish workaround that copies generated XBF/PRI into the
publish directory is kept or replaced with a 2.4-equivalent fix. The portable
tree must still contain those resources.

## 4. Windows support policy

Published support after V0.44:

```text
Minimum supported: Windows 10 22H2 x64
Primary:           Windows 11 24H2 x64
                   Windows 11 25H2 x64
```

V0.44 does not ship ARM64 or x86.

Older Windows:

- may start;
- may be checked later at low cost;
- is not a published guarantee;
- does not require a full regression farm;
- does not block V0.44 by default.

Documents must say **minimum supported version**, not that the program is
technically unable to run below that version.

Native verification uses the machines the developer actually has. A missing
SKU is `unverified`. It is never reported as `passed`. Compile success does
not replace Windows 10 22H2 native smoke when that machine is available.

## 5. Windows App SDK componentization

The top-level `Microsoft.WindowsAppSDK` 2.4.0 metapackage currently pulls at
least Base, Foundation, Runtime, WinUI, InteractiveExperiences, DWrite, AI,
ML, Search, and Widgets. V0.44 must not keep unused payloads only because the
metapackage is convenient.

The minimum supported set is discovered from the 2.4 NuGet graph, compile
references, publish provenance, and a clean staging launch. Package names are
not frozen in this Plan in advance of that graph.

Removal is allowed only through the project/NuGet graph. Hand-deleting publish
DLLs is not componentization.

Expected audit targets with no current WinPool consumer:

- AI
- ML
- ONNX Runtime
- DirectML
- Widgets
- Search
- other optional metapackage components with no compile or runtime consumer

WinUI may still require Foundation, Base, Runtime, InteractiveExperiences, or
DWrite as transitive dependencies. Those stay if the graph and launch evidence
require them.

Componentization is complete only when restore, build, tests, and publish
succeed; App starts from a clean staging directory; WinUI/XAML and pickers
work; unused AI/ML-class packages are absent from the graph and staging; and
the tree does not depend on a developer-machine global Windows App Runtime.

## 6. App / Agent distribution layout

### 6.1 Process model

```text
WinPool.App.exe
    WinUI shell, navigation, pages, and user interaction

WinPool.Agent.exe
    tray runtime, inventory, monitoring, SQLite single writer,
    App IPC, persistence, and lifecycle ownership
```

Do not merge Agent into App, move monitoring back to App, drop the tray
lifetime, or weaken crash isolation or IPC boundaries in order to save bytes.

### 6.2 Current layout

V0.43 stages:

```text
WinPool.App.exe
Agent/WinPool.Agent.exe
```

That nested path is currently a contract, not only a script detail. It is
encoded in at least:

- `Directory.Build.props`
- `src/WinPool.App/WinPool.App.csproj`
- `src/WinPool.App/App.xaml.cs`
- `src/WinPool.App/Services/AgentStartupRegistration.cs`
- `build/Publish-Staged.ps1`
- `build/Rebuild-WinPool.ps1`
- `tests/WinPool.Architecture.Tests/ArchitectureBoundaryTests.cs`
- `docs/Development.md`
- `docs/Quality.md`

WP4 updates all of these together if flattening proceeds.

### 6.3 Preferred V0.44 layout

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
├── shared .NET, WinPool, WinUI, Windows App SDK, and WinForms files
├── Assets/
└── XBF / PRI / other required resources
```

Each process keeps its own apphost, deps, runtimeconfig, entry assembly, and
lifetime. Shared files are stored once on disk. The two CLR instances are not
merged in memory.

Local `artifacts\$(Configuration)\` uses this same layout.

### 6.4 Merge rules

Do not publish App, publish Agent, and copy with last-writer-wins.

Required staging merge:

1. Publish App and Agent to separate temporary directories.
2. Hash every same-named file.
3. Identical content: keep one copy in staging.
4. Different content: fail the staging build. No silent overwrite.
5. Agent-only WinForms files are kept.
6. App-only WinUI / Windows App SDK files are kept.
7. Staging is rebuilt from an empty directory.
8. Both App and Agent are launched from that merged directory.

This step removes duplicate files. It does not share one in-memory runtime.

### 6.5 Flattening fallback

If WP4 hits irreconcilable same-name different-content files, stop flattening.
Do not merge processes, enable trimming, delete features, or alter images to
force a smaller tree. Keep the nested `Agent\` layout, record the conflicting
files, and continue with WP5–WP7.

## 7. PDB and symbols

Symbol generation stays on. Build trees may contain `*.pdb`.

Formal portable staging and the distribution ZIP must not contain `*.pdb`.
Exclusion happens in staging rules, not by disabling compiler symbols.

Checks:

- expected PDBs still exist in build outputs;
- staging contains none;
- PDB exclusion does not delete `.dll`, `.json`, `.pri`, or `.xbf`.

## 8. Explicit non-goals

V0.44 does not include:

- new user features
- real storage-structure mutation
- V0.5 management operations
- large WorkspaceViewModel or business-architecture splits
- native WinForms tray replacement
- image compression or format conversion
- deleting or changing welcome-image selection
- trimming, NativeAOT, single-file, or ReadyToRun experiments
- framework-dependent or Lite distributions
- a new MSIX mode
- ARM64 or x86
- auto-update or installer design
- V1.0 multi-channel distribution design

If any of those would also reduce size, record them as later candidates. Do not
expand V0.44 to take them.

## 9. Work packages and order

Implementation of WP0–WP7 starts only after explicit developer authorization.

### WP0 — Freeze and measure the V0.43 baseline

Before any upgrade:

1. Record the baseline commit.
2. From a clean tree, run the standard restore/test/build commands in
   [Quality](Quality.md).
3. Produce V0.43 Release portable staging with `build/Publish-Staged.ps1` into
   a new path.
4. Record total size, file count, App size, Agent size, duplicate App/Agent
   file size, PDB size, Windows App SDK-related size, .NET runtime size,
   WinForms size, and WinPool-owned size.
5. Keep a text report. Do not commit large temporary binaries.

The handwritten drafts mentioned about 380.44 MiB for a complete V0.43
distributable tree. Local `artifacts\Release` at Plan writing was a different
tree (about 294 MiB) and is not that measurement. WP0 remeasures from this
baseline commit. The handwritten figure is not reused as evidence.

A failed baseline check is `failed` or `unverified`. It is not described as a
passed V0.43 state.

### WP1 — Windows SDK probe and TFM unification

1. Probe `net10.0-windows10.0.28000.0` on the pinned .NET SDK.
2. Apply the 28000 TFM or the documented fallback.
3. Unify Windows-targeted projects onto one Windows TFM.
4. Update BuildTools, projections, and any hard-coded platform-version test
   assumptions.
5. Fix compile errors. Do not adopt 28000-only product APIs.
6. Restore, build, and test.

### WP2 — Windows App SDK 2.4 functional equivalence

1. Move `Microsoft.WindowsAppSDK` to 2.4 Stable, keeping a complete functional
   dependency set for this step.
2. Repair breaking API, WinUI, XAML resource, unpackaged initialization,
   publish, picker, and windowing differences.
3. Confirm the checks in section 3.3.
4. Run the automatic gate and a WinUI smoke launch from staging.

Do not start componentization or layout flattening until this step is stable.

### WP3 — Windows App SDK componentization

Replace the metapackage with the minimum supported component set discovered in
section 5. Re-record size and file differences. Launch from a clean staging
directory after each graph reduction.

### WP4 — Shared App / Agent staging

Change publish and local output to the preferred flat layout, update every
nested-path contract listed in section 6.2, and add hash merge with fail-fast
conflicts. Verify App and Agent from the merged directory.

If flattening cannot complete under section 6.5, retain the nested layout and
continue.

### WP5 — Exclude PDB from Release staging

Formal staging and ZIP omit PDB. Build outputs still have symbols. App and
Agent still start from staging.

This package does not depend on WP4 succeeding.

### WP6 — Version and documents

Advance `Directory.Build.props` from V0.43 / 0.4.3 to **V0.44 / 0.4.4** and
align tests and documents with the implemented platform, layout, and support
text. Do this after WP2 is stable so a failed 2.4 upgrade cannot leave a V0.44
tree that still ships 1.8.

Documents in this package:

- `Directory.Build.props`
- `README.md` and `README.zh-CN.md`
- `docs/Product.md` and `docs/Product.zh-CN.md`
- `docs/Development.md` and `docs/Development.zh-CN.md`
- `docs/Quality.md` and `docs/Quality.zh-CN.md`
- `docs/CHANGELOG.md` and `docs/CHANGELOG.zh-CN.md`
- this Plan
- other active text that names portable staging, Windows support, or platform
  versions

English remains authoritative. Chinese copies are updated in the same work
item. Product should state that the later V0.4 line includes platform and
distribution work, not only visual polish.

### WP7 — Final verification and size report

Produce the final V0.44 Release staging and a comparison:

```text
V0.43 baseline
Windows SDK / TFM result
WinAppSDK 2.4
WinAppSDK componentization
shared App/Agent staging or retained nested layout
PDB exclusion
V0.44 final
```

Scripts record facts. They do not decide that the size is acceptable.

Recommended commit split after authorization:

```text
docs: define WinPool V0.44 platform and slimming plan
build: probe Windows SDK 28000 and unify Windows TFMs
build: upgrade Windows App SDK to 2.4
build: componentize Windows App SDK dependencies
build: merge App and Agent portable staging
build: exclude PDB from distribution staging
chore: bump product version to V0.44
docs: update Windows support and deployment docs
```

If flattening is abandoned, omit or replace the merge commit with a note that
the nested layout was retained. If a step fails, roll back that step. Do not
restore all of V0.43, enable trimming, delete features, or change images as a
remedy.

## 10. Automatic quality gates

Use the project standard from [Quality](Quality.md):

```powershell
dotnet restore WinPool.slnx

dotnet test WinPool.slnx `
    -c Release `
    --no-restore `
    --maxcpucount:1 `
    -m:1

dotnet build WinPool.slnx `
    -c Release `
    --no-restore `
    -m:1

dotnet list WinPool.slnx package `
    --vulnerable `
    --include-transitive
```

V0.44 also requires:

- one recorded Windows SDK / TFM baseline
- Windows App SDK 2.4 Stable and no leftover 1.8 package
- no unused AI/ML-class package in the graph
- `PublishTrimmed` is false
- formal staging contains no PDB
- App/Agent same-name conflicts fail fast
- staging is rebuilt from an empty directory
- shared runtime files are not duplicated, or the retained nested layout is
  documented
- XBF/PRI, Agent executable, deps, and runtimeconfig are complete
- architecture tests match the implemented layout

A skipped, unavailable, or unrun gate is `unverified` or `not_required`. It is
never `passed`. Completing implementation does not start formal acceptance.
Ask the developer whether to enter formal testing.

## 11. Native and manual verification

Required on the current development Windows 11 machine:

- extract-and-run portable staging
- App cold start
- Agent auto-start
- tray icon
- main window open/close
- Chinese/English switch
- Light/Dark/System theme
- Folder Picker
- local inventory
- topology
- simulation
- monitoring
- SQLite data location
- App/Agent restart
- Agent-only lifetime
- no preinstalled .NET or Windows App Runtime requirement

Windows 10 22H2 x64 is the published minimum. When that machine is available,
run the same smoke list; compile success does not replace it. If it is not
available, record `unverified`.

Windows 11 24H2 and 25H2 follow the same rule: run the primary regression when
the SKU exists here; otherwise `unverified`.

Do not “fix” an older-Windows failure by weakening 22H2/24H2/25H2 correctness
unless the developer expands the support range.

## 12. Risks and fallbacks

| Risk | Control |
| --- | --- |
| WinUI, picker, windowing, or unpackaged startup changes on 2.4 | Finish functional equivalence before componentization or flattening |
| 28000 TFM rejected by the pinned .NET SDK | Keep the accepted TFM; do not unpin `global.json` |
| Removing a component that WinUI needs indirectly | Reduce only through NuGet; launch from clean staging after each cut |
| Same-name different-content DLLs in a shared folder | Fail fast; fall back to nested `Agent\` |
| Apparent size drop caused by becoming framework-dependent | Verify portable staging on a machine without the development runtimes |
| Chasing an arbitrary MiB number | No hard size gate; no trimming or image changes |

## 13. Completion and archival gate

V0.44 implementation is complete only when:

- WP0–WP7 have finished without an unresolved stop condition, applying the
  documented TFM and flattening fallbacks where they were required;
- the product version is V0.44;
- .NET 10 is unchanged and trimming is still off;
- Windows App SDK is 2.4 Stable and componentized;
- unused AI/ML-class metapackage payloads are gone;
- App and Agent remain independent processes;
- the implemented layout, nested or flat, is consistent between local output
  and staging and is described by the documents;
- Release staging has no PDB and original images are unchanged;
- WinForms tray behavior is unchanged;
- automatic gates completed or are truthfully marked unverified;
- required native checks have truthful recorded outcomes;
- English documents and Chinese reading copies match the implemented result;
- the final size comparison against the WP0 baseline is recorded;
- the developer has judged that size result acceptable;
- no unrelated files or generated artifacts are committed;
- no push, tag, Release, upload, or deployment is claimed without separate
  authorization.

The closing statement for a completed V0.44 is:

> WinPool has moved to the new Windows platform baseline. It keeps Windows 10
> 22H2+ as the published minimum, the two-process portable self-contained
> architecture, and the existing product capability, while removing unused
> Windows App SDK components and, where the merge succeeded, duplicate
> App/Agent runtime files.

Implementation completion does not automatically start or pass formal V0.44
acceptance. After the implementation gate, the developer decides whether to
enter formal testing. When the stage is genuinely finished, this Plan is frozen
under `docs/Archive/V0.44/`; it is never rewritten afterward to make history
appear cleaner.

## 14. After V0.44

V0.44 does not pre-decide later work on native tray replacement,
framework-dependent Lite shipping, dual portable/installed channels, trimming,
NativeAOT, single-file, image optimization, or ARM64.

Those choices wait for the actual V0.44 file-size report. Later slimming uses
the V0.44 tree, not the V0.43 handwritten 380.44 MiB figure.
