---
project: WinPool
phase: V0.3
architecture_version: V0.3
status: accepted
branch: main
confirmed_by_user: 2026-08-10
code_gate: passed
native_integration_gate: passed
manual_gate: unverified
remote_gate: passed
accepted_by_user: 2026-08-10
---

# WinPool V0.31 documentation architecture correction

[English](Plan.md) | [简体中文（仅供阅读）](Plan.zh-CN.md)

## Background

The first V0.31 restructuring incorrectly replaced the user's explicit
`docs/Archive` decision with a generic parent-archive rule. Commits `6cf68e3` and
`8d7fb25` are retained as audit history; this plan corrects the result forward
without revert, amend, force push, tag, or release.

The active documentation architecture follows the project-management reference
selectively and remains specific to WinPool's native Windows technology and safety
model.

## Confirmed decisions

- No root `Plan` directory remains; the only active plan is `docs/Plan.md`.
- Root documentation is limited to the English and Chinese README files plus the
  authoritative and Chinese-reading-copy Agent rules.
- Product, development, quality, current plan, changelog, references, and archives
  live under `docs`.
- Historical WinPool documents go to `docs/Archive`; other superseded non-document
  content goes to the parent-project `Old` tree.
- Software-consumed resources live in tracked `assets`.
- User-managed source artwork lives in ignored `OriginArtWork`; it does not enter
  Git until the user decides on Git, Git LFS, or another asset strategy.
- English authoritative Markdown documents have `.zh-CN.md` reading copies. The
  unsuffixed document always controls; documents already in Chinese need no
  duplicate.
- V0.31 remains a source integration version, not a tag, binary release, or
  GitHub Release. V0.32 requires explicit user acceptance.

## Correction work packages

| ID | Status | Deliverable |
| --- | --- | --- |
| DOC-01 | passed | Preserve the incorrect V0.31 Plan and index under `docs/Archive/V0.31-pre-correction`. |
| DOC-02 | passed | Move the 16-file V0.2 archive to `docs/Archive/V0.2` with byte and SHA-256 equality. |
| DOC-03 | passed | Establish Product, Development, Quality, Plan, CHANGELOG, Reference, and Archive ownership. |
| DOC-04 | passed | Remove active `Plan`/root `DEVELOP` references and repair source evidence paths. |
| DOC-05 | passed | Run structure, link, Release test/build, vulnerability, Git-scope, and remote gates. |
| DOC-06 | passed | Add non-authoritative Chinese reading copies, track `assets`, and ignore `OriginArtWork`. |

## V0.32 manual acceptance carry-over

| ID | Status | Case |
| --- | --- | --- |
| CARRY-01 | unverified | Startup, tray, and single-instance behavior |
| CARRY-02 | unverified | Six pages and read-only/simulation boundary |
| CARRY-03 | unverified | Chinese/English, opposite theme, and external-tool status |
| CARRY-04 | unverified | In-App DiskSpd in the registered D: directory |
| CARRY-05 | unverified | Cancellation and next-run recovery |
| CARRY-06 | unverified | RoboCopy/FullHash generation, copy, comparison, and export |
| CARRY-07 | unverified | Monitoring continuity across App window closure |
| CARRY-08 | unverified | R3/UAC D: flush target, confirmation, Broker, and audit |
| CARRY-09 | unverified | Complete tray exit and child-process cleanup |
| CARRY-10 | unverified | Standard → portable → standard data-location round trip |
| CARRY-11 | unverified | Forced Worker termination and Job Object recovery |
| CARRY-12 | passed | Actual four-process V0.31 staged layout and forbidden-content check |

## Deferred V0.3 debt

| ID | Status | Work |
| --- | --- | --- |
| DEBT-01 | deferred_by_user | Converge implementation, automatic, and manual evidence for 205 compatibility IDs |
| DEBT-02 | deferred_by_user | Native/script inventory parity and failure degradation |
| DEBT-03 | deferred_by_user | Keyboard, screen reader, high contrast, DPI, and narrow-window matrix |
| DEBT-04 | deferred_by_user | User-approved bilingual and visual regression matrix |
| DEBT-05 | deferred_by_user | Multi-hour monitoring, reconnect, backpressure, handle, and memory evidence |
| DEBT-06 | deferred_by_user | Large-directory, capacity-boundary, Dite/fio/RoboCopy adapter evidence |
| DEBT-07 | deferred_by_user | R3 failure, cancellation, crash, audit, and recovery matrix |
| DEBT-08 | deferred_by_user | Large data-location migration and rollback |
| DEBT-09 | deferred_by_user | Privacy and collection policy decision |
| DEBT-10 | deferred_by_user | Validate speculative algorithms or retain their confidence label |
| DEBT-11 | deferred_by_user | Long-test live curves and event-gap compensation |
| DEBT-12 | deferred_by_user | Complete reproducible release-engineering closure |

## Automatic completion criteria

- Root `Plan` and root `DEVELOP.md` are absent.
- Required `docs` files exist and `docs/Plan.md` is the only active plan.
- V0.2 contains exactly 16 unchanged files, 212,510 bytes, and the verified
  pre-migration hashes.
- All active Markdown links and source evidence paths resolve.
- Release restore, test, build, and dependency-vulnerability checks pass.
- `git diff --check` and the explicit source/document staging whitelist pass.
- The correction is pushed forward to `origin/main`; no force push, tag, release,
  binary upload, Old, Rubbish, local assets, or generated output is included.

After the automatic criteria pass, this plan moves to `awaiting_acceptance`.
Only explicit user confirmation may establish V0.32.

## V0.32 acceptance decision

On 2026-08-10 the user explicitly assigned the current project version as
V0.32. This closes the documentation-architecture stage and archives this Plan.
The decision does not change the 11 unrun native/manual cases: they remain
`unverified`, and no test result has been inferred from the version assignment.
V0.32 remains the confirmed project version without a tag, binary release, or GitHub
Release.

## Verified evidence

- V0.2 archive: 16 files, 212,510 bytes, and all 16 pre-migration SHA-256
  values matched on 2026-08-10.
- Documentation structure test: 24/24 passed, including the single-active-plan
  assertion.
- Full Release test suite: 456/456 passed (the original 455 tests plus the new
  documentation-structure test), with 0 failed and 0 skipped.
- Full Release build: 0 warnings and 0 errors.
- Direct and transitive dependency vulnerability scan: no vulnerable package
  reported for any solution project.
- A fresh V0.32 four-process staging completed at
  `Rubbish/20260810_winpool_v032_staging/Program/WinPool`; App, Agent,
  TestWorker, and Broker each report project version `V0.32`.
- Manual GUI, tray, UAC, D: tool, and data-location gates remain unverified.
- Forward correction commit `236eb3f` was pushed to `origin/main` on 2026-08-10
  after confirming that the previous remote head was its ancestor.
- Documentation/resource follow-up: all 8 English authoritative document pairs
  passed link and authority checks; `assets` contains 35 tracked files
  totaling 15,189,113 bytes; `OriginArtWork` is ignored; the full Release suite
  passed 458/458 with 0 failures, and the build completed with 0 warnings and
  0 errors.
- V0.32 revalidation: all 37 Markdown files passed relative-link checks, the full
  Release suite passed 458/458 with 0 failures and 0 skipped, the Release build
  completed with 0 warnings and 0 errors, and no vulnerable direct or transitive
  dependency was reported.
- Git completion: resource/document commit `dc5e263` and V0.32 version commit
  `7b7a798` were pushed to `origin/main` by normal fast-forward on 2026-08-10.

## Version-semantics correction

Annotation on 2026-08-10: `V0.32` is the only project version, following
`Va.bc` with `a=0`, `b=3`, and `c=2`. The previously named technical-version
field was invalid. Numeric .NET/Windows fields are mechanically derived build
metadata and have no independent project-version meaning.

The corrected model was revalidated with 458/458 Release tests, a 0-warning and
0-error Release build, and a fresh four-process staging at
`Rubbish/20260810_winpool_v032_version_semantics/Program/WinPool`. All four
executables report the sole project version `V0.32`.
