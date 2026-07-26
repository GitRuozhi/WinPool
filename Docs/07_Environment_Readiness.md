# WinUI Environment Readiness

Audit date: 2026-07-24

## Present

| Component | Detected state |
|---|---|
| Operating system | Windows 10 Pro 22H2, build 19045, x64 |
| Developer Mode | Enabled |
| Visual Studio | Visual Studio Community 2022 17.14, complete and launchable |
| .NET SDK | 9.0.308 |
| Windows SDK | 10.0.26100.0 |
| MSBuild | Present through Visual Studio |

## Missing

- The `winui` template is not available through `dotnet new`.
- The Windows App SDK C# workload was not detected.

## Current readiness

The machine is not yet ready to scaffold and verify a new WinUI 3 application. This is expected because the current phase is documentation-only.

## Next-phase procedure

At Gate 1, use the WinUI setup workflow to install the required workload, then verify:

1. `dotnet new list winui` returns the template.
2. An unpackaged C# WinUI project can be scaffolded without overwrite flags.
3. The generated project builds.
4. The application launches and shows a responsive top-level window.

Environment readiness, successful build, and successful launch are separate gates; one does not prove the others.
