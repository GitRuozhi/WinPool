# Agent Instructions for WinPool

[English](AGENTS.md) | [简体中文（仅供阅读）](AGENTS.zh-CN.md)

This file contains stable operational rules for work inside `Program\WinPool`.
Product direction, architecture, quality gates, current work, results, and history
belong in their dedicated documents under `docs`.

## Precedence and required reading

Use this order when instructions differ:

1. The user's explicit decision in the current task.
2. Safety, authorization, and protected-data rules in the parent and local
   `AGENTS.md` files.
3. `docs/Product.md`.
4. The confirmed `docs/Plan.md`, when it exists.
5. Project contracts and approved design rules.
6. `docs/Development.md` and `docs/Quality.md`.
7. Current implementation and automatic evidence.
8. `docs/CHANGELOG.md` and `docs/Archive` as historical records.

A generic parent rule must not silently replace a more specific user decision. The
user has explicitly decided that historical WinPool documents belong in
`docs/Archive`; the parent-project `Old` rule continues to apply to superseded
WinPool content that is not documentation.

Before changing the project, read the root README, this file, Product, Development,
Quality, and the current Plan if present. Check Git status, branch, upstream, and
protected paths before editing.

## Environment and scope

- Windows and PowerShell are the supported development environment.
- The solution targets .NET 10, WinUI 3, Windows App SDK, and unpackaged x64.
- Keep work inside `Program\WinPool` unless the task explicitly names another
  path. Moving WinPool documents from their approved parent-project archive source
  is within document-migration scope.
- Do not reorganize Dite, KS, Research, Tests, Showcase, or other projects.
- Preserve unrelated and pre-existing user changes.

## Safety and data boundaries

- Do not implement or enable real disk, partition, volume, Storage Pool, Storage
  Tier, or Virtual Disk creation, removal, initialization, formatting, resizing,
  repair, or equivalent mutation.
- Simulation is the implementation path for storage-structure changes, including
  when the UI is in Real mode.
- File tests may touch only files registered to a run inside an explicitly selected
  test directory. Raw-device writes are forbidden.
- Support actions such as cache cleanup, volume flush, TRIM/Optimize, process
  scheduling, and temporary power-plan changes require typed plans, target
  validation, confirmation where required, audit, and restoration of reversible
  state.
- External DiskSpd, fio, Dite, RoboCopy, and RAMMap engines remain separately
  installed tools behind typed adapters. Do not bundle or reimplement them.
- Persisted, exported, imported, logged, or copied hardware data must pass through
  the approved redaction boundary.
- Do not store or publish a standalone inventory `.ps1`; fixed read-only
  PowerShell remains assembly-embedded and is supplied through standard input.

## File and documentation rules

- Do not directly delete files by default. A narrow exception may be granted only
  for a user-approved refactoring whose confirmed Plan names the exact obsolete
  source/test targets and requires replacement and regression evidence first.
  The user has approved that exception for V0.33 retirement of
  `src/WinPool.Core` and `tests/WinPool.Core.Tests`; this does not authorize
  execution before the V0.33 Plan is confirmed, or deletion of any other content.
- Historical WinPool documents go to `docs/Archive`, with an index and truthful
  status. Archive content is not a current requirement.
- Other confirmed superseded WinPool content goes to the parent-project `Old`
  tree, preserving relative paths where practical.
- Low-value generated material goes to the parent-project `Rubbish` tree.
- Do not create local `Old`, `Rubbish`, or variant directories inside WinPool.
- `README.md` and `README.zh-CN.md` are user-facing entry points.
- `AGENTS.md` contains operational constraints only.
- Product, Development, Quality, Plan, CHANGELOG, Reference, and Archive content
  belongs under `docs`.
- Only one active `docs/Plan.md` may exist. When no stage is active, the file may
  be absent.
- Completed or invalidated plans are frozen under `docs/Archive`; do not rewrite
  them to make history appear correct.
- Repository `assets` contains software-consumed resources and is tracked by Git.
- `OriginArtWork` contains user-managed source artwork and remains ignored until
  the user approves Git, Git LFS, or another asset strategy.
- A `.zh-CN.md` file is a non-authoritative Chinese reading copy of the matching
  unsuffixed Markdown file. If they differ, the unsuffixed document controls.

## Version and Git rules

- Product versions use `Va.bc`: `a` is major, `b` is minor, and `c` is the
  one-digit iteration inside that minor version.
- `Va.bc` is the only project-version system. Required numeric .NET/Windows
  fields are mechanically derived build metadata and must never be named or
  documented as another project version.
- Architecture and roadmap documents normally specify only `Va.b`.
- At `c=8` or `c=9`, remind the developer to control scope. Never create
  `c=10`; reduce scope, combine work, or advance the minor version.
- A normal `c` iteration requires a local commit but no push, tag, or release
  unless explicitly authorized.
- V0.35 is the current user-confirmed version. Its M01--M04 and inherited
  native/manual cases remain `unverified`; version confirmation is not evidence
  that they passed.
- The accepted V0.35 Plan and its unchanged manual source record are archived
  under `docs/Archive/V0.35`; there is no active `docs/Plan.md`. The V0.35
  decision authorizes its local checkpoint, `main` push, and local portable
  deployment only. Tag, GitHub Release, and binary upload remain unauthorized.
- Before pushing, fetch, verify the remote target is an ancestor of local HEAD,
  inspect outgoing commits, and refuse divergence or force push.
- A tag, GitHub Release, binary upload, or deployment always requires separate
  explicit authorization.

## Verification rules

- Use the exact commands and result vocabulary in `docs/Quality.md`.
- Automatic checks do not substitute for UAC, tray, native-picker, visual, device,
  or long-duration human evidence.
- Never report an unavailable or unrun gate as passed.
- Real hardware mutation is not a verification method.
