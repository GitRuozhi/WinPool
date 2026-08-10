# Changelog

[English](CHANGELOG.md) | [简体中文（仅供阅读）](CHANGELOG.zh-CN.md)

This file records results that actually occurred. Planned work remains in
`Plan.md`; historical plans remain in `Archive`.

## Unreleased

No unreleased changes are recorded after the V0.32 checkpoint.

## V0.32 source checkpoint — 2026-08-10

- The user explicitly assigned V0.32 after reviewing the V0.31 restructuring.
- Updated the shared display and technical versions to V0.32 / 0.3.2.0.
- Added non-authoritative `.zh-CN.md` reading copies for English project
  documentation; unsuffixed documents remain controlling.
- Added software-consumed `assets` to Git control and excluded user-managed
  `OriginArtWork` from Git.
- Preserved all 11 outstanding native/manual cases as `unverified`; the version
  assignment is not evidence that those cases passed.
- Revalidated all 458 Release tests and the nested four-process V0.32 staging;
  every staged executable reports V0.32 / 0.3.2.0.

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

- Added the shared V0.31/0.3.1.0 version source.
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
