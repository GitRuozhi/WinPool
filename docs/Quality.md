# WinPool Quality and Acceptance

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
- Markdown links and documented paths resolve.
- Version sources, runtime display values, and tracked documentation agree.
- Architecture boundaries, closed diagnostics, typed commands, and deny-by-default
  execution remain covered.
- Git scope excludes local assets, generated output, databases, logs, external
  tools, and release binaries.

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

The fixed manual root for the current V0.3 matrix is
`D:\WinPool-V03-Manual-Test`. Manual checks must not select another drive root,
the source tree, a network share, or an unregistered directory.

## Acceptance policy

- Automatic gates establish deterministic engineering facts; they do not approve
  visual intent or physical-device behavior.
- The Agent cannot mark a human gate passed without user evidence.
- Approved exceptions record reason, scope, approver, date, risk, and expiry.
- Test counts belong in the active Plan or CHANGELOG evidence, not in this long-term
  policy.
- Real hardware mutation is never an accepted verification technique for V0.3.
