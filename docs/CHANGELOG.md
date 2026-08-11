# Changelog

[English](CHANGELOG.md) | [简体中文（仅供阅读）](CHANGELOG.zh-CN.md)

This file records results that actually occurred. Planned work remains in
`Plan.md`; historical plans remain in `Archive`.

## Unreleased

### V0.33 final candidate awaiting acceptance — 2026-08-11

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
- V0.33 has passed all automatic final-candidate gates. Native/manual gates are
  still `unverified`; no tag, binary release, GitHub Release, or remote push has
  been performed for V0.33.

## V0.32 source checkpoint — 2026-08-10

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

V0.32 is a source checkpoint, not a tag, binary release, or GitHub Release.
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

## V0.31 source integration checkpoint — 2026-08-10

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
