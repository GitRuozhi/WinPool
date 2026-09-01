# WinPool Document Archive

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

Archive content records historical state and does not define current requirements.
V0.44 is the latest implemented record. The V0.44 plan is archived under
`V0.44`; there is no active Plan.

| Entry | Status | Date | Version / commits | Contents |
| --- | --- | --- | --- | --- |
| [`V0.2/`](V0.2/) | archived | 2026-08-10 | V0.2; source baseline `ec8b34a` | 15 architecture, implementation, acceptance, and manual-test plans plus their frozen index |
| [`V0.31-pre-correction/`](V0.31-pre-correction/) | superseded / invalid | 2026-08-10 | `6cf68e3`, `8d7fb25` | The Plan and Plan index that incorrectly overrode the user's `docs/Archive` decision |
| [`V0.32/`](V0.32/) | accepted; manual cases unverified | 2026-08-10 | V0.32; `dc5e263`, `7b7a798` | Final V0.31 correction state and the user's V0.32 version decision |
| [`V0.33/`](V0.33/) | accepted; native/manual cases unverified; pushed | 2026-08-11 | V0.33; implementation `6b66c68`…`0dcd22a`; version `38ff043`; acceptance `e148b61` | Architecture convergence, lifecycle hardening, exact migration, verification evidence, and user acceptance |
| [`V0.34/`](V0.34/) | accepted; native/manual cases unverified | 2026-08-11 | V0.34; implementation `f9a9869`…`aee9eb6`; version `b18f119` | Defect closure, strict process identity, schema-12 clean break, event reseeding/backpressure, and accepted execution record |
| [`V0.35/`](V0.35/) | accepted; native/manual cases unverified | 2026-08-12 | V0.35; implementation `ab83458`…`5338603`; candidate `a2ab8ae`; acceptance checkpoint | Local identity authority, watcher isolation, terminal persistence, bounded shutdown, schema verification, and process-incarnation closure |
| [`V0.36/`](V0.36/) | implemented; automatic gates passed; native/manual cases unverified | 2026-08-12 | V0.36; local Git checkpoint | Schema constraints, connection disposal, watcher accounting, monotonic worker persistence, historical Local identity, and protocol-error closure |
| [`V0.37/`](V0.37/) | implemented; automatic gates passed; native/manual cases unverified | 2026-08-13 | V0.37; local Git commit | UI reentrancy, unobserved-task handling, async void recovery, partition input validation, simulated pool preview, event recovery, redirect timeout, and RoboCopy output normalization |
| [`V0.38/`](V0.38/) | implemented; automatic gates passed; native/manual cases unverified | 2026-08-13 | V0.38; local Git commit | Stale Agent endpoint identity verification and recovery |
| [`V0.38B/`](V0.38B/) | implemented; automatic gates passed; native/manual cases partially verified | 2026-08-13 | V0.38B; local Git commit | Comparison-table identity safety, Agent shutdown cleanup, startup notification deduplication, and WinForms synchronization-context deadlock fix |
| [`V0.39/`](V0.39/) | implemented; automatic gates passed; native/manual cases unverified | 2026-08-13 | V0.39; local Git commit | V0.3 stability closure before V0.4 |
| [`V0.39-final-correction/`](V0.39-final-correction/) | implemented; automatic gates passed; target WinUI cases passed; inherited native/manual cases unverified | 2026-08-13 | V0.39; final local correction commit | Agent control-pipe isolation, Test-page unknown-outcome reconciliation, and final V0.3 correction evidence |
| [`V0.39-architecture-hardening/`](V0.39-architecture-hardening/) | implemented; automatic gates passed; targeted native navigation passed; remaining native/manual cases unverified | 2026-08-14 | V0.39; architecture-governance branch | Pre-V0.40 unused-code cleanup, Agent responsibility split, definition-graph relocation, data-location handoff isolation, and architecture guards |
| [`V0.41/`](V0.41/) | implemented; automatic gates passed; targeted native checks passed; inherited native/manual cases unverified | 2026-08-14 | V0.41; no Git commit, push, tag, release, or deployment authorized | Startup, welcome, monitoring, persistence, tray, and basic-interaction plan with the final V0.41 product version |
| [`V0.43/`](V0.43/) | implemented; automatic gates passed; targeted native checks passed; inherited native/manual cases unverified | 2026-09-01 | V0.43; local Git checkpoint | Product slimming: disk-test, external-tool, Development/AI diagnostics, TestWorker, and ElevatedBroker removed; App+Agent runtime; IPC protocol 4; SQLite schema 14 |
| [`V0.44/`](V0.44/) | implemented; automatic gates passed; targeted native smoke passed; inherited native/manual matrix unverified | 2026-09-01 | V0.44; local Git checkpoint | Platform upgrade to Windows App SDK 2.4; 26100 TFM with 28000 BuildTools; AI/ML publish exclusion; nested Agent layout retained; staging without PDB; 338.40 MiB |
| [`V0.44-draft/`](V0.44-draft/) | archived source records; superseded by the V0.44 Plan | 2026-09-01 | handwritten input for V0.44 | Developer handwritten Chinese drafts for platform upgrade and distribution slimming; superseded by `V0.44/Plan.md` |
| [`V0.39a-draft/`](V0.39a-draft/) | superseded; never approved or implemented | 2026-08-13 | invalid draft label, not a product version | Original broad correction proposal preserved as historical input; reusable observations moved to the V0.8–V0.9 technical-debt reference |
| [`V0.33重构.md`](V0.33重构.md) and [`V0.33重构补充.md`](V0.33重构补充.md) | archived source records | 2026-08-11 | V0.33 | Original V0.33 reconstruction and supplement records, preserved unchanged after relocation from `docs/` |

The forward correction is recorded by commit `236eb3f`, which is present on
`origin/main`. It preserves the two superseded commits above as audit history.

Archived files remain read-only except for broken-link repair or an explicit factual
correction. Any correction must be identified as a later annotation; historical
acceptance results must never be fabricated.
