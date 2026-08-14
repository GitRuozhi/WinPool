# WinPool Quality and Acceptance

[English](Quality.md) | [简体中文（仅供阅读）](Quality.zh-CN.md)

## Result vocabulary

Every gate or case must use one of: `passed`, `failed`, `unverified`,
`not_required`, or `deferred_by_user`. A skipped or unavailable check is never
reported as passed.

## Quality model

WinPool is a native Windows, multi-process .NET application. Browser DOM tests,
web-server checks, and webpage screenshot rules from reference projects are not
applicable unless WinPool later introduces a separately approved web surface.

### Static and structure gate

- Required repository and documentation structure is present.
- Exactly one active `docs/Plan.md` exists when a stage is active.
- Every English authoritative Markdown document has a matching `.zh-CN.md`
  reading copy, and every copy identifies the unsuffixed document as controlling.
- Markdown links and documented paths resolve.
- Version sources, runtime display values, and tracked documentation agree.
- Architecture boundaries, closed diagnostics, typed commands, and deny-by-default
  execution remain covered.
- Git scope includes software-consumed `assets` and excludes `OriginArtWork`,
  local-only resources, generated output, databases, logs, external tools, and
  release binaries.

### .NET automatic gate

Run from the WinPool repository root:

```powershell
dotnet restore WinPool.slnx
dotnet test WinPool.slnx -c Release --no-restore --maxcpucount:1 -m:1
dotnet build WinPool.slnx -c Release --no-restore -m:1
dotnet list WinPool.slnx package --vulnerable --include-transitive
```

Tests must not mutate real storage structure. Build warnings require an explicit
explanation or a user-approved exception.

### Windows native integration gate

- The App, Agent, TestWorker, and Broker publish to their required nested paths.
- App runtime lookup paths match the staged tree.
- Named-pipe identity and ACL behavior, Worker cleanup, SQLite ownership, and
  read-only inventory boundaries remain covered by automatic or controlled local
  integration checks.
- Staging contains no scripts, local assets, databases, test results, external
  tools, or duplicate child executables.

### Human and device gate

Manual evidence is required for native WinUI presentation, bilingual switching,
themes, DPI, high contrast, keyboard use, tray lifecycle, UAC, native folder
pickers, registered D: tool execution, long-running monitoring, cancellation,
recovery, and data-location round trips.

The fixed manual root for the current V0.41 matrix is
`D:\WinPool-V041-Manual-Test`. Manual checks must not select another drive root,
the source tree, a network share, or an unregistered directory.

For V0.5-or-later controlled real-mutation verification, automatic tests and CI
remain simulation-only. A manual case may perform one real operation only after
the development Agent has obtained the developer's per-operation approval, or
the product user has explicitly selected the local real-mutation option in the
current session. The evidence must record the operation, targets, authorization
context, selection state, and result.

## Acceptance policy

- Automatic gates establish deterministic engineering facts; they do not approve
  visual intent or physical-device behavior.
- The Agent cannot mark a human gate passed without user evidence.
- Approved exceptions record reason, scope, approver, date, risk, and expiry.
- Test counts belong in the active Plan or CHANGELOG evidence, not in this long-term
  policy.
- Real hardware mutation is never an accepted verification technique for V0.41.
  V0.5-or-later controlled real-mutation cases use the documented explicit
  authorization flow and never convert an unrun case into a passing result.
