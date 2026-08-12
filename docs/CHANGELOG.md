# Changelog

[English](CHANGELOG.md) | [简体中文（仅供阅读）](CHANGELOG.zh-CN.md)

This file records results that actually occurred. Planned work belongs in
`Plan.md` while a stage is active; historical plans remain in `Archive`.

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

V0.36 passed 511 automatic Release tests with no skipped tests, a warning-free
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
