# Agent Instructions for WinPool

[English](AGENTS.md) | [简体中文（仅供阅读）](AGENTS.zh-CN.md)

This file is the AI working entry and the highest-priority operational rules
inside `Program\WinPool`. It states what must not be crossed. Product direction,
architecture, quality gates, results, and history belong in `docs` and are read
only when the current task needs them.

## Precedence

When instructions differ, use this order:

1. The user's explicit decision in the current task.
2. Safety, authorization, and protected-data rules in the parent and local
   `AGENTS.md` files.
3. The dedicated `docs` owner for that fact, when that document has been read
   because the task requires it.
4. Current implementation.

A generic parent rule must not silently replace a more specific user decision.
The user has decided that historical WinPool documents belong in `docs/Archive`;
the parent-project `Old` rule continues to apply to superseded WinPool content
that is not documentation.

## Default reading

The only project document required at the start of an ordinary task is this
file. If a more specific local Agent file exists in the current working tree,
follow it as well.

Do not read README, Product, Development, Quality, CHANGELOG, Reference, or
Archive by default, and do not load both language copies of the same document.
Check Git status, branch, upstream, and protected paths before editing.

Read other documents only when the task needs them:

| Need | Read |
| --- | --- |
| How to run, current version, current capability, user-facing limits | `README.md` |
| Product goals, boundary, roadmap, whether a request is in scope | `docs/Product.md` |
| Architecture, module ownership, persistence, IPC, staging, version scheme | `docs/Development.md` |
| Tests, acceptance vocabulary, whether a quality gate applies | `docs/Quality.md` |
| Execute, continue, inspect, accept, or edit the current formal stage | `docs/Plan.md` |
| Confirmed completed results or an important compatibility change | `docs/CHANGELOG.md` |
| A named historical design, version, or superseded decision | the specific `docs/Archive` file |
| A named method, study, or deferred observation | the specific `docs/Reference` file |

How to run and current capability are in `README.md`. Long-term product
rules are in `docs/Product.md`. Architecture is in `docs/Development.md`.
Tests and formal acceptance are in `docs/Quality.md`. Confirmed results are
in `docs/CHANGELOG.md`. Project progress is not answered from Git status
alone; read README, Product, and CHANGELOG when the question is progress.

`docs/Plan.md` is read only when the developer explicitly asks to execute,
continue, inspect, accept, or change the current formal plan. Its presence is
not a reason to read it. Archive and Reference are never default context; do
not read either tree in bulk. Reference is not a current project requirement.

The developer's current decision outranks Plan, CHANGELOG, Archive, and old
test assertions. If a document conflicts with a current decision or the
confirmed implementation, treat the document as possibly stale; do not use
old documents or old tests to reverse the current implementation.
`docs/Plan.md` records only the confirmed current plan. It is not a permanent
product contract. When no plan is active, do not invent the next stage from
history.

## Environment and scope

- Windows and PowerShell are the supported development environment.
- The solution targets .NET 10, WinUI 3, Windows App SDK, and unpackaged x64.
- Keep work inside `Program\WinPool` unless the task explicitly names another
  path.
- Do not reorganize Dite, KS, Research, Tests, Showcase, or other projects.
- Preserve unrelated and pre-existing user changes.

## Safety and data boundaries

Real storage-structure mutation is denied by default. Do not create, remove,
initialize, format, resize, repair, or otherwise mutate real disks, partitions,
volumes, Storage Pools, Storage Tiers, or Virtual Disks unless the current
product boundary or a confirmed Plan explicitly permits that path, and only
after the required explicit authorization for that exact operation and those
exact targets. Free-form storage commands remain forbidden.

UAC elevation or selecting Real mode is not authorization. Simulation remains
the default path for storage-structure changes, including while the UI is in
Real mode, until the user has completed the real-mutation authorization flow.

Before a development Agent performs each actual storage mutation, it must ask
the developer for specific approval of that operation and its targets. A
previous, broad, or implied approval is insufficient. In the product, a user's
explicit current-session selection of the local real-mutation option is the
authorization for controlled real operations permitted by the current product
boundary; that option must not be preselected or persisted as consent.

Which product phase may add a typed real-mutation path is defined by
[Product](docs/Product.md) and the confirmed Plan, not by this file.

- File tests may touch only files registered to a run inside an explicitly
  selected test directory. Raw-device writes are forbidden.
- Support actions such as cache cleanup, volume flush, TRIM/Optimize, process
  scheduling, and temporary power-plan changes require typed plans, target
  validation, confirmation where required, audit, and restoration of reversible
  state.
- External DiskSpd, fio, Dite, RoboCopy, and RAMMap engines remain separately
  installed tools behind typed adapters. Do not bundle or reimplement them.
- Persisted, exported, imported, logged, or copied hardware data must pass
  through the approved redaction boundary.
- Do not store or publish a standalone inventory `.ps1`; fixed read-only
  PowerShell remains assembly-embedded and is supplied through standard input.

## Files and documents

- Do not directly delete files by default. A narrow exception requires a
  user-approved refactoring whose confirmed Plan names the exact obsolete
  source or test targets and requires replacement and regression evidence first.
- Historical WinPool documents go to `docs/Archive`, with an index and truthful
  status. Archive content is not a current requirement.
- Other confirmed superseded WinPool content goes to the parent-project `Old`
  tree, preserving relative paths where practical.
- Low-value generated material goes to the parent-project `Rubbish` tree.
- Do not create local `Old`, `Rubbish`, or variant directories inside WinPool.
- `README.md` is the user-facing entry. This file contains operational
  constraints only. Product, Development, Quality, Plan, CHANGELOG, Reference,
  and Archive content belongs under `docs`.
- Only one active `docs/Plan.md` may exist. When no stage is active, the file
  may be absent. Completed or invalidated plans are frozen under `docs/Archive`;
  do not rewrite them to make history appear correct.
- One long-term fact has one owner. Other documents may keep a one-sentence
  summary and a link; they must not duplicate the full rule.
- Git records process. Long-term documents record rules and important final
  results, not commit lists, review logs, per-round test counts, or intermediate
  construction notes.
- Repository `assets` contains software-consumed resources and is tracked by
  Git. `OriginArtWork` remains ignored until the user approves an asset
  strategy.
- An unsuffixed Markdown file is authoritative. A matching `.zh-CN.md` file is
  a non-authoritative Chinese reading copy. If they differ, the unsuffixed
  document controls. Update a reading copy in the same work item as its
  authority. Ordinary tasks read the authoritative file only.

## Agent workflow and Git

- Implement only the confirmed minimum closed loop. Do not add framework,
  schema, API, task, or deployment machinery because it might be needed later.
- Changing product behavior, data boundaries, or stage scope requires
  confirmation. Already-decided items are not re-litigated by old documents.
- Do not write discussion drafts or unconfirmed schemes into long-term
  documents. Do not create extra planning documents unless the developer
  explicitly asks.
- Split commits: documentation, refactor, feature, and visual or asset
  changes go in separate commits. Equivalent refactors stay separate from
  visual adjustments.
- This is a solo repository. Commit directly on `main`. Do not create
  feature branches and do not open pull requests.
- After a completed in-scope change, create a local Git commit by default.
  Do not ask. Follow existing commit-message habits. Do not commit
  generated `artifacts/`, `Ref/`, `temp/`, discussion drafts, or unrelated
  user files.
- Do not push unless the developer explicitly asks. When five unpushed
  commits have accumulated, remind the developer to push.
- Before a push, fetch, verify `origin/main` is an ancestor of local HEAD,
  inspect outgoing commits, and refuse divergence or force push. Never
  force push.
- Do not tag, create a GitHub Release, upload binaries, or deploy unless
  the developer explicitly authorizes that action.

The project version is defined in `Directory.Build.props`. Do not invent a
second version system. Architecture and roadmap documents normally specify only
`Va.b`. At iteration `c=8` or `c=9`, remind the developer to control scope.
Never create `c=10`.

## Verification triggers

Use the result vocabulary and gate definitions in [Quality](docs/Quality.md)
when a test or acceptance task requires them.

- Documentation-only changes do not run code, native, device, or visual tests.
- Ordinary small edits and ordinary feature work do not run the full quality
  gate.
- If the developer names a test, run that scope.
- If a change has an obvious local risk, the smallest directly related check is
  allowed; do not escalate it into a full verification flow.
- Completing a formal stage does not start full acceptance. Ask the developer
  whether to enter formal testing.
- Never report an unavailable or unrun gate as passed.
- Automatic checks do not substitute for UAC, tray, native-picker, visual,
  device, or long-duration human evidence.
- Real hardware mutation is not an accepted verification technique unless a
  confirmed Plan for a permitted mutation stage requires it, and then only
  under the authorization rules above.
