# V0.45

[English](README.md) | [简体中文（仅供阅读）](README.zh-CN.md)

Implemented 2026-09-02. Accepted and frozen 2026-09-04 after the developer
confirmed the native Edit click-through passed (local, simulation-only run).

This is the frozen V0.45 Edit-page topology workspace plan. The Edit page
became two independently scrolling halves: an upper disk/partition workspace
with split unallocated-gap nodes and the existing partition actions, and a
lower pool workspace with primordial disk-only display, a synthetic plus-pool
ordered last, working-copy drag with SSD/HDD/SCM auto-tiering, V10-based
tiered-pool defaults, Execute create/modify, and pool dissolution back to
primordial. All operations are simulation-only.

Verification at acceptance (2026-09-04): the full Release automatic gate on
the merged `main` passed 369 tests with 0 failed and 0 skipped, a warning-free
Release build, and no known vulnerable packages. The targeted Application
tests cover both projections, gap unallocated nodes, auto-tier assignment,
unknown-media refusal, tiered create, dissolve, and multi-virtual-disk modify
rejection. The developer confirmed the native Edit click-through.

Inherited device, UAC, DPI, OS-matrix, and long-duration cases remain
`unverified`. Implementation commits `a4e8f57`…`7bd21f2` (plus fix `15215fd`)
are already on `origin/main`. This acceptance created no push, tag, Release,
binary upload, or deployment.

The active Plan is now the unified topology layout engine stage
(`docs/Plan.md`), installed 2026-09-04.

Later annotation, 2026-09-04: the original unified-layout proposal
(`WinPool Unified Topology Layout Engine Plan.md`) was moved here from
`docs/`. It is the unamended source record superseded by the installed
active Plan and is not a current requirement.
