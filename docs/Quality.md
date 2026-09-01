# WinPool Quality and Acceptance

[English](Quality.md) | [简体中文（仅供阅读）](Quality.zh-CN.md)

## Result vocabulary

Every gate or case must use one of: `passed`, `failed`, `unverified`,
`not_required`, or `deferred_by_user`. A skipped or unavailable check is never
reported as passed.

## When to run gates

Having a full test capability does not mean every change should run every gate.

- Documentation-only changes do not run code, native, device, or visual tests.
- Ordinary small edits and ordinary feature work do not run the full quality
  gate.
- If the developer names a test, run that scope.
- If a change has an obvious local risk, the smallest directly related check is
  allowed; do not escalate it into a full verification flow.
- Completing a formal stage does not start full acceptance. Ask the developer
  whether to enter formal testing.
- A formal version or formal acceptance run uses the gates below after the
  developer confirms that run.

Stage-specific test directories, matrices, and results belong in the active Plan
or in Archive, not in this long-term policy. Test counts belong in Plan or
CHANGELOG evidence when they are important final results.

WinPool 1.x exposes the Test and Development tabs as roadmap placeholders only.
Formal 1.x acceptance verifies that those placeholders are present, simple,
bilingual, and accessible. Test-workspace, external benchmark execution, and
developer/AI-workspace cases are `not_required` for 1.x; retained internal code
continues to receive automatic regression coverage where it remains in the
solution.

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
- The product version source is `Directory.Build.props`. Runtime display values
  must match that source. Documents that mention a product version must not
  contradict it.
- Architecture boundaries, closed diagnostics, typed commands, and deny-by-default
  execution remain covered.
- Git scope includes software-consumed `assets` and excludes `OriginArtWork`,
  local-only resources, generated output, databases, logs, and release binaries.

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

- The App and Agent publish to their required nested paths.
- Local `dotnet build` writes the same nested tree to `artifacts\$(Configuration)\`.
- App runtime lookup paths match the staged tree.
- Named-pipe identity and ACL behavior, SQLite ownership, and read-only
  inventory boundaries remain covered by automatic or controlled local
  integration checks.
- Staging contains no scripts, local assets, databases, test results, or
  duplicate child executables.

### Human and device gate

Manual evidence is required for native WinUI presentation, bilingual switching,
themes, DPI, high contrast, keyboard use, tray lifecycle, native folder pickers,
monitoring start/stop, and data-location round trips.

A formal manual matrix uses the directory named by the active Plan or the
archived stage that defined it. Manual checks must not select the source tree,
a network share, or an unregistered directory.

For controlled real-mutation verification in a Product-permitted stage,
automatic tests and CI remain simulation-only. A manual case may perform one
real operation only after the development Agent has obtained the developer's
per-operation approval, or the product user has explicitly selected the local
real-mutation option in the current session. The evidence must record the
operation, targets, authorization context, selection state, and result.

## Acceptance policy

- Automatic gates establish deterministic engineering facts; they do not approve
  visual intent or physical-device behavior.
- The Agent cannot mark a human gate passed without user evidence.
- Approved exceptions record reason, scope, approver, date, risk, and expiry.
- Real hardware mutation is never an accepted verification technique unless a
  confirmed Plan for a permitted mutation stage requires it. Unrun cases stay
  `unverified`.
