# Changelog

[English](CHANGELOG.md) | [简体中文（仅供阅读）](CHANGELOG.zh-CN.md)

This file records important final results. Planned work belongs in `Plan.md`
while a stage is active; historical plans remain in `Archive`. Git history
records construction process. New entries use result sections; older entries
are not rewritten for format consistency.

## V0.45 shell-first startup — 2026-09-02

### Changed
- The main window paints tab structure before waiting for the tray Agent.
- Manage shows connecting/scanning status only in the global notification stack.
  Cached local inventory is projected first, then the PowerShell scan replaces it
  in place. Global toasts sit on an opaque theme surface so they remain readable
  over Mica.
- Replacing the empty startup local document with a cached scan no longer throws
  a workspace-initialization error while the first-paint selection is still bound
  to the previous identity.

### Verification
- Architecture tests require `NavigateStartupPage` before
  `InitialAgentConnectionTask`, and no full-window `ProgressRing`.
- Local unpackaged launch: main window handle was non-zero, title `WinPool`,
  shell tabs and Manage categories were present while Agent started.

### Known Limitations
- Cold Agent start and the embedded PowerShell inventory can still take several
  seconds; the wait no longer leaves an empty frame.

## V0.45 local runtime collision gate — 2026-09-01

### Changed
- Local App and Agent builds write independent trees, then reuse the staging
  SHA-256 union. Same-name different-content files fail the local build.

### Verification
- Architecture tests require the shared merge script and separate local trees.
- Product version remains V0.45.

### Known Limitations
- Inherited OS-matrix and full human UI cases remain `unverified`.

## V0.45 — 2026-09-01

### Changed
- Product version is **V0.45**.
- Portable App and Agent share one collision-checked runtime directory.
- Settings uses ToggleSwitch rows, one phrase per switch, and icon buttons.

### Verification
- Architecture version-source test matches `Directory.Build.props`.
- Inherited OS-matrix and full human UI cases remain `unverified`.

### Known Limitations
- No real storage mutation occurs.

## V0.44 Settings card layout — 2026-09-01

### Changed
- Settings uses one card layout for appearance, general, and about rows:
  shared section titles, label column, spacing, and left-aligned controls.

### Verification
- Targeted native screenshot of the Settings page after the layout change.
- Product version remains V0.44.

### Known Limitations
- Full bilingual, theme, DPI, and high-contrast Settings review remains
  `unverified`.

## V0.44 Agent client image path — 2026-09-01

### Changed
- Agent now expects `WinPool.App.exe` beside itself. The old `..\WinPool.App.exe`
  handshake check rejected the App after the flat layout, so IPC and inventory
  failed.

### Verification
- Architecture path guards require the beside-App client image.
- Product version remains V0.44.

### Known Limitations
- Full App/Agent UI inventory smoke remains `unverified` in this change.

## V0.44 local Agent self-contained run tree — 2026-09-01

### Changed
- Local Agent builds as self-contained `win-x64` into the shared run tree.
  `WinPool.lnk` / `artifacts\Release` no longer shows an install-or-update .NET
  dialog when App launches Agent.

### Verification
- Local `WinPool.Agent.runtimeconfig.json` uses `includedFrameworks`.
- Direct Agent start from `artifacts\Release` no longer requests a machine-wide
  .NET Runtime.

### Known Limitations
- Product version remains V0.44.
- Inherited OS-matrix and full human UI cases remain `unverified`.

## V0.44 shared runtime staging — 2026-09-01

### Changed
- Portable staging is the SHA-256-checked union of independent App and Agent
  self-contained publishes. Shared identical files are stored once. Same-name
  different-content files fail staging.
- Local `artifacts\$(Configuration)\` uses the same flat root:
  `WinPool.App.exe` and `WinPool.Agent.exe` beside each other.
- The nested `Agent\` runtime tree is removed from staging and from the local
  run tree. Product version remains V0.44.

### Verification
- Release solution build: 0 warnings, 0 errors.
- Release automatic tests: 353 passed, 0 failed, 0 skipped.
- No known vulnerable packages.
- Union merge: 281 shared, 288 App-only, 5 Agent-only, 0 collisions.
- Portable staging: 574 files, 231.58 MiB, down from the nested V0.44 baseline
  of 779 files / 338.40 MiB.
- Layout: both executables at the staging root, product version V0.44, 0 PDB,
  PRI and XBF present.
- Targeted process smoke from the merged tree: App cold-start launched Agent
  from the root path; Agent also started directly.

### Known Limitations
- Full App/Agent UI smoke (navigation, theme, language, Picker, inventory,
  monitoring, tray commands) remains `unverified` in this change.
- Win10 22H2, Win11 24H2/25H2 full manual matrices, and inherited device, UAC,
  DPI, and long-duration cases remain `unverified`.
- No real storage mutation occurs.

## V0.44 platform upgrade and distribution slimming — 2026-09-01

### Changed
- Windows App SDK upgraded from 1.8 to **2.4.0**. Unused AI, ML, Search, and
  Widgets payloads are excluded from publish assets; ONNX/DirectML files no
  longer enter the portable tree.
- Windows-targeted projects share TFM `net10.0-windows10.0.26100.0`. The pinned
  .NET SDK rejected a 28000 TFM (NETSDK1140). `Microsoft.Windows.SDK.BuildTools`
  is `10.0.28000.2705`.
- Published minimum OS is Windows 10 22H2 x64. Compile SDK is not that floor.
- Formal staging excludes PDB. Build outputs still keep symbols.
- App/Agent flattening was attempted and retained the nested `Agent\` layout
  after five same-name desktop assemblies differed.

### Verification
- Release solution build: 0 warnings, 0 errors.
- Release automatic tests: 352 passed, 0 failed, 0 skipped.
- No known vulnerable packages.
- Portable staging: 779 files, 338.40 MiB, down from the V0.43 baseline of
  853 files / 380.44 MiB.
- Targeted native smoke: App and Agent launched from a clean staged tree.

### Known Limitations
- Shared App/Agent runtime files are still stored twice in the nested layout.
- Win10 22H2, Win11 24H2/25H2 full manual matrices, and inherited device, UAC,
  DPI, and long-duration cases remain `unverified`.
- No real storage mutation occurs.

## V0.43 product slimming — 2026-08-31

### Changed
- Disk-test, external-tool, Development/AI diagnostics, TestWorker, and
  ElevatedBroker subsystems removed from the 1.0 release path and deferred to
  1.x/2.0.
- Runtime reduced to App + Agent.
- IPC protocol bumped to 4; SQLite schema bumped to 14.

### Verification
- Release solution build: 0 warnings, 0 errors.
- Release automatic tests: 350 passed, 0 failed, 0 skipped.

### Known Limitations
- The Test and Development tabs remain roadmap placeholders.
- No real storage mutation occurs.

## V0.42 scope reduction — 2026-08-31

### Changed
- Reduced the entire WinPool 1.x product line to the shortest release path for
  storage topology, management/editing, monitoring, settings, data safety, and
  release-quality delivery.
- Kept the Test and Development tabs discoverable, but replaced their full
  workspaces with short bilingual notices. Their complete product interfaces
  and workflows are now planned for V2.0 rather than any 1.x release.
- Revised the V0.5 through V2.0 route and the quality boundary to match the
  reduced 1.x scope.

### Verification
- Architecture boundary tests: 31 passed, 0 failed, 0 skipped.
- Release solution build: 0 warnings, 0 errors.

### Known Limitations
- Existing internal testing, worker, persistence, and diagnostic foundations
  remain in the codebase to avoid adding release risk; they are not supported
  1.x product surfaces.
- Native visual and manual acceptance of the two placeholder pages remains
  `unverified` in this change.

## V0.42 — 2026-08-28

### Added
- Unit-based topology layout engine: topology rows now pack by integer H/W
  units instead of the previous pixel-greedy wrap.
- Built-in simulation systems for verifying topology layout across
  representative storage configurations.

### Changed
- Rows fill the aligned height first, then move a whole pool to the next row,
  and only then wrap disks inside a single pool.
- While a row keeps multiple pools side by side, a pool may exceed the aligned
  height up to `max(H+1, 1.3H)` before the row breaks.
- Shrinking reduces unpartitioned disks before partitioned ones; a shared-row
  pool keeps a minimum width of 2 units for layered pools and
  pool → disk → partition structures, while simple two-level pools and a pool
  alone on its row are exempt.
- Topology area horizontal scrolling is automatic.

## V0.41 — 2026-08-14

### Added
- Startup welcome, continuous monitoring, persistence, tray, and basic
  interaction from the approved V0.41 plan.

### Changed
- Language, external-tool, data-location, and Edit/Settings layout.

### Fixed
- Preference-load hang that left the Agent in `Recovering`.
- Continuous-monitoring start, close, reopen, and reattach.

### Compatibility
- Agent-owned SQLite schema 13. Older schemas are rejected without migration.
- IPC remains protocol 3.
- Preferences live in `settings.json`; SQLite no longer stores user
  preferences.

### Known Limitations
- Inherited topology-context, device, UAC, DPI, external-tool execution, and
  long-duration cases remain `unverified`.
- No commit, push, tag, release, binary upload, or deployment was created.

## V0.4 — 2026-08-14

### Changed
- Opened the V0.4 product line. Mechanical .NET version metadata is `0.4.0`.

### Known Limitations
- The V0.4 version definition was pushed to GitHub. No tag, GitHub Release, or
  binary upload was created.

## V0.39 architecture hardening — 2026-08-14

### Changed
- Removed confirmed unused contracts and obsolete UI residue.
- Split concentrated Agent and page responsibilities.
- Moved closed I/O, Copy, and Mixed Directory definition graphs to
  `WinPool.Testing`.

### Known Limitations
- This is a V0.39 maintenance record, not a product release.
- Remaining native, device, UAC, installer, migration, and long-duration cases
  stay `unverified`.

## V0.39 — 2026-08-13

### Fixed
- Development-diagnostics refresh timeout, visible status, and duplicate-refresh
  protection.
- Control-pipe isolation after cancel or timeout so a late response cannot be
  consumed by the next request.
- `OutcomeUnknown` for lost or malformed Test-page Start/Cancel responses, with
  RunId reconciliation and no automatic retry.

### Changed
- Completed the final minimal V0.3 correction before the V0.4 visual phase.

### Known Limitations
- Inherited native, device, UAC, and long-duration cases remain `unverified`.
- The user authorized the local commit, GitHub push, V0.39 tag, and GitHub
  Release.

2026-08-14 correction: the earlier 562-test statement is not reproducible from
the V0.39 solution. The full Release command reports 526 passed, 0 failed, and
0 skipped; 526 is the governing V0.39 automatic-test count. Frozen archive text
is preserved as historical evidence rather than rewritten.

## V0.38 — 2026-08-13

- Added Agent endpoint process-identity verification so a stale
  `agent-endpoint.json` whose PID has been reused by an unrelated Windows process
  is treated as absent.
- `ConnectAsync` now starts a replacement Agent through the existing launcher
  instead of waiting on an obsolete named pipe.
- Added regression coverage for stale endpoint identity recovery.

V0.38 passed 520 automatic Release tests with no skipped tests, a warning-free
Release build, and the dependency audit. Native/manual cases remain
`unverified`. The user authorized this local Git commit only; no push, tag,
binary upload, GitHub Release, or deployment is authorized.

## V0.37 — 2026-08-13

- Closed the Settings language SelectionChanged reentrancy path and added
  preference-save failure recovery for theme, accent, language, MSR, and
  hardware-ID settings.
- Marked unobserved task exceptions as observed after writing crash evidence.
- Added exception recovery and control-state restoration to Test-page
  prepare/start/cancel/status-polling paths and Edit-page simulation mutations.
- Rejected invalid non-empty partition size input instead of silently using all
  remaining free space.
- Added a confirmation and parameter preview before simulated pool creation.
- Made the Development event stream and inventory comparison report failures and
  restore controls after transport errors.
- Bounded the single-instance activation redirect wait instead of waiting
  indefinitely.
- Normalized malformed RoboCopy output as a failed tool event.
- Replaced the format file-system text prompt with a fixed NTFS/ReFS/exFAT
  selector and made missing partition paths report a target-missing notice.

V0.37 passed 519 automatic Release tests with no skipped tests, a warning-free
Release build, and the dependency audit. Native/manual cases remain
`unverified`. The user authorized this local Git commit only; no push, tag,
binary upload, GitHub Release, or deployment is authorized.

## V0.36 — 2026-08-12

- Recorded the user-confirmed V0.1--V1.0 development route in `Product.md`,
  including the clarification that V0.5 is the first phase permitted to add
  controlled real storage-structure mutation. A development Agent requires the
  developer's per-operation approval; a product user's explicit current-session
  selection of the local real-mutation option authorizes V0.5 controlled real
  operations. This does not alter the current V0.3 boundary or authorize a
  release.
- Expanded schema-12 verification to compare table constraints/definitions and
  index metadata/definitions, including the singleton `CHECK` constraints,
  without opening a mutation path for corrupt current databases.
- Made `NamedPipeAgentConnection` lifetime-safe: disposal cancels and drains
  active connection/request operations before disposing shared resources;
  malformed handshake JSON is normalized as `agent.connect.failed`.
- Distinguished normal watcher disposal from actual bounded-channel overflow,
  avoiding false global event-gap recovery.
- Made worker-process identity fields immutable, heartbeats monotonic, and the
  stopping deadline establish-once and preserved through terminal persistence.
- Made a validated Local document ID authoritative even if the corresponding
  historical Local row has stale name or binding metadata.

V0.36 passed 519 automatic Release tests with no skipped tests, a warning-free
Release build, dependency audit, and a fresh four-process self-contained staging
tree. Native/manual cases remain `unverified`. The user authorized the local Git
checkpoint only; no push, tag, binary upload, GitHub Release, or deployment is
authorized.

## V0.35 — 2026-08-12

- Made the Agent-owned Local system identity authoritative in SQLite so
  comparison-first capture cannot create a new Local `SystemId`.
- Isolated slow App-side event watchers, made their overflow an explicit event
  gap, and reseeded healthy watchers after recovery.
- Made `worker_processes` terminal persistence states absorbing; stale writes
  are ignored atomically.
- Bounded shutdown operations even when an implementation ignores cancellation,
  and fenced late terminal effects.
- Reject schema-12 databases whose actual tables, columns, indexes, or foreign
  keys no longer match the read-only current-schema contract.
- Unified Main App handshake and shutdown checks around PID, executable image,
  and process-start incarnation witnesses.

The user explicitly accepted V0.35 after 507 automatic Release tests, a
warning-free Release build, dependency audit, and four-process self-contained
staging at `D:\WinPool-V035-Candidate-Staging-Final-20260812` passed.
Native/manual cases remain `unverified`; acceptance does not represent them as
passing. The decision authorizes the documentation archive, local checkpoint,
`main` push, and local portable deployment. No tag, binary upload, or GitHub
Release is authorized.

## V0.34 — 2026-08-11

- Bound every supervised process mutation to a process instance ID, PID, and OS
  process-start witness; IPC is now protocol 3.
- Made Local inventory identity Agent-owned, made storage-location post-commit
  cleanup report partial completion, and adopted schema 12 as a clean break
  that rejects older databases without changing them.
- Added authoritative shutdown status, snapshot reseeding after event gaps,
  explicit event backpressure, and isolated stdout/stderr progress decoders
  with EOF flushing.
- V0.34 passed 494 automatic Release tests, a warning-free Release
  build, dependency audit, and four-process self-contained staging at
  `D:\WinPool-V034-Candidate-Staging-Final`.

The user explicitly accepted V0.34 and authorized the associated documentation
archival, Git checkpoint, `main` push, and local portable deployment.
Native/manual cases remain `unverified`; acceptance does not represent them as
passing. No tag, binary upload, or GitHub Release is authorized.

## V0.33 — 2026-08-11

- The user explicitly accepted V0.33 and authorized documentation archival,
  Git commit, and push to `main`.

- Retired `WinPool.Core` into the canonical Application model and preserved
  system/document identity, simulation, projection, startup, notification, and
  layout behavior.
- Hardened Agent, Worker, Broker, Control IPC, and Event IPC lifecycles with
  retryable shutdown, bounded process termination, typed abort, isolated bad
  clients, disconnect recovery, snapshot reseeding, and explicit event-gap state.
- Added process-instance identity, bounded terminal diagnostics, and the real
  SQLite v10-to-v11 history migration; the single V0.33 wire-protocol bump is 2.
- Made the Agent own external-tool path configuration and made each tool
  invocation resolve one numeric output code page for stateful stdout/stderr
  decoding while preserving raw bytes.
- Replaced storage-location overwrite-copy with an exact same-volume staging
  transaction. It captures source and target, drains only the source store,
  verifies manifests and SQLite identity, rolls the previous target back on
  cancellation or failure, and removes stale managed target payload.
- Split test-state, system-support, and inventory ownership into three focused
  Agent coordinators while keeping `DesktopAgentRuntime` as the request facade.
- All 486 Release tests, the warning-free Release build, transitive dependency
  audit, Markdown checks, and four-process V0.33 staging passed.
- Ten native/manual cases remain `unverified`; version confirmation is not
  evidence that those cases passed.

V0.33 is the confirmed project version, not a tag, binary release, or GitHub Release.
Implementation range: `6b66c68` through `0dcd22a`; version commit `38ff043`;
acceptance documentation `e148b61`. These commits are present on `origin/main`.

## V0.32 — 2026-08-10

- The user explicitly assigned V0.32 after reviewing the V0.31 restructuring.
- Set the single project version to V0.32 under the user-defined `Va.bc` rule.
- Added non-authoritative `.zh-CN.md` reading copies for English project
  documentation; unsuffixed documents remain controlling.
- Added software-consumed `assets` to Git control and excluded user-managed
  `OriginArtWork` from Git.
- Preserved all 11 outstanding native/manual cases as `unverified`; the version
  assignment is not evidence that those cases passed.
- Revalidated all 458 Release tests and the nested four-process V0.32 staging;
  every staged executable reports project version V0.32.
- Removed the incorrectly introduced `TechnicalVersion` concept. Numeric fields
  required by .NET/Windows are derived build metadata, not another project
  version.

V0.32 was a confirmed project version, not a tag, binary release, or GitHub Release.
Commits: `dc5e263`, `7b7a798` (pushed to `origin/main`).

### V0.31 documentation-architecture correction — 2026-08-10

- Replaced the incorrect root `Plan` layout with the documented `docs` information
  architecture.
- Restored the user-approved repository-local document archive policy.
- Preserved the incorrect V0.31 plan as superseded audit history rather than
  rewriting or force-pushing Git history.
- Kept V0.32 manual acceptance unverified.
- Forward correction commit: `236eb3f` (pushed to `origin/main`).

This correction is not a tag, binary release, or GitHub Release.

## V0.31 source integration — 2026-08-10

- Added a shared V0.31 version source. That commit also incorrectly named numeric
  build metadata as a technical version; V0.32 later corrected the semantics.
- Added reproducible four-process publish staging and real-layout verification.
- Updated source and automatic architecture checks.
- Commits: `6cf68e3`, `8d7fb25`.

The original document-archive decision in these commits was invalid and is
superseded by the correction recorded above.

## V0.21 — 2026-08-09

- Published the V0.2 multi-process architecture integration with the V0.13 visual
  baseline.
- Fixed the unpackaged deployment packaging baseline in `ec8b34a`.
- Release commit: `fcebb67`.
