---
project: WinPool
phase: V0.3
architecture_version: V0.3
status: awaiting_acceptance
integration_checkpoint: V0.31
acceptance_checkpoint: V0.32
branch: main
confirmed_by_user: 2026-08-10
code_gate: passed
native_integration_gate: passed
manual_gate: unverified
remote_gate: passed
---

# WinPool V0.31 documentation architecture correction

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
- Root documentation is limited to the English and Chinese README files plus
  `AGENTS.md`.
- Product, development, quality, current plan, changelog, references, and archives
  live under `docs`.
- Historical WinPool documents go to `docs/Archive`; other superseded non-document
  content goes to the parent-project `Old` tree.
- Developer-local art and other non-code resources remain in ignored
  `local-assets` and do not enter Git.
- V0.31 remains a source integration checkpoint, not a tag, binary release, or
  GitHub Release. V0.32 requires explicit user acceptance.

## Correction work packages

| ID | Status | Deliverable |
| --- | --- | --- |
| DOC-01 | passed | Preserve the incorrect V0.31 Plan and index under `docs/Archive/V0.31-pre-correction`. |
| DOC-02 | passed | Move the 16-file V0.2 archive to `docs/Archive/V0.2` with byte and SHA-256 equality. |
| DOC-03 | passed | Establish Product, Development, Quality, Plan, CHANGELOG, Reference, and Archive ownership. |
| DOC-04 | passed | Remove active `Plan`/root `DEVELOP` references and repair source evidence paths. |
| DOC-05 | passed | Run structure, link, Release test/build, vulnerability, Git-scope, and remote gates. |

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
- Existing V0.31 four-process native staging evidence remains valid because this
  correction does not change publish projects or runtime behavior.
- Manual GUI, tray, UAC, D: tool, and data-location gates remain unverified.
- Forward correction commit `236eb3f` was pushed to `origin/main` on 2026-08-10
  after confirming that the previous remote head was its ancestor.
