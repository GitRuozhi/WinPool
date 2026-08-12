# WinPool Document Archive

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

Archive content records historical state and does not define current requirements.
There is no active Plan; V0.36 is the latest implemented record.

| Entry | Status | Date | Version / commits | Contents |
| --- | --- | --- | --- | --- |
| [`V0.2/`](V0.2/) | archived | 2026-08-10 | V0.2; source baseline `ec8b34a` | 15 architecture, implementation, acceptance, and manual-test plans plus their frozen index |
| [`V0.31-pre-correction/`](V0.31-pre-correction/) | superseded / invalid | 2026-08-10 | `6cf68e3`, `8d7fb25` | The Plan and Plan index that incorrectly overrode the user's `docs/Archive` decision |
| [`V0.32/`](V0.32/) | accepted; manual cases unverified | 2026-08-10 | V0.32; `dc5e263`, `7b7a798` | Final V0.31 correction state and the user's V0.32 version decision |
| [`V0.33/`](V0.33/) | accepted; native/manual cases unverified; pushed | 2026-08-11 | V0.33; implementation `6b66c68`…`0dcd22a`; version `38ff043`; acceptance `e148b61` | Architecture convergence, lifecycle hardening, exact migration, verification evidence, and user acceptance |
| [`V0.34/`](V0.34/) | accepted; native/manual cases unverified | 2026-08-11 | V0.34; implementation `f9a9869`…`aee9eb6`; version `b18f119` | Defect closure, strict process identity, schema-12 clean break, event reseeding/backpressure, and accepted execution record |
| [`V0.35/`](V0.35/) | accepted; native/manual cases unverified | 2026-08-12 | V0.35; implementation `ab83458`…`5338603`; candidate `a2ab8ae`; acceptance checkpoint | Local identity authority, watcher isolation, terminal persistence, bounded shutdown, schema verification, and process-incarnation closure |
| [`V0.36/`](V0.36/) | implemented; automatic gates passed; native/manual cases unverified | 2026-08-12 | V0.36; local Git checkpoint | Schema constraints, connection disposal, watcher accounting, monotonic worker persistence, historical Local identity, and protocol-error closure |
| [`V0.33重构.md`](V0.33重构.md) and [`V0.33重构补充.md`](V0.33重构补充.md) | archived source records | 2026-08-11 | V0.33 | Original V0.33 reconstruction and supplement records, preserved unchanged after relocation from `docs/` |

The forward correction is recorded by commit `236eb3f`, which is present on
`origin/main`. It preserves the two superseded commits above as audit history.

Archived files remain read-only except for broken-link repair or an explicit factual
correction. Any correction must be identified as a later annotation; historical
acceptance results must never be fabricated.
