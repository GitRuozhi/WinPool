# WinPool Product Direction

[English](Product.md) | [简体中文（仅供阅读）](Product.zh-CN.md)

## Purpose

WinPool is a third-party Windows storage-system desktop application. Its long-term
purpose is to replace the fragmented Disk Management and Storage Spaces graphical
experiences with one coherent view of storage topology, testing, monitoring, and
reviewable operations.

The product should make relationships between systems, pools, tiers, physical
disks, virtual disks, partitions, volumes, network storage, and other logical
groups understandable without hiding important parameters.

## Product principles

- Present the complete storage topology and a focused operation workspace together.
- Make every proposed operation explicit, reviewable, typed, and auditable.
- Treat simulation and read-only discovery as first-class capabilities.
- Integrate testing, monitoring, comparison, and evidence export without bundling
  or reimplementing external benchmark engines.
- Keep internal contracts structured enough for future developer and AI-agent use
  without freezing a public SDK, plug-in API, database contract, or wire protocol.
- Preserve bilingual, theme-aware, keyboard-accessible, high-contrast, and
  responsive desktop behavior.

## Product boundary

WinPool may inspect the local machine through read-only collectors, edit persistent
simulated systems, monitor supported devices, and execute explicitly reviewed
support actions.

Real storage-structure mutation is outside the product boundary until V0.5. Until
then, WinPool must not initialize, clear, format, create, remove, repair, or
resize real disks, partitions, volumes, Storage Pools, Storage Tiers, or Virtual
Disks. V0.5 is the earliest stage permitted to introduce those operations through
reviewed typed plans; the product is not permanently simulation-only.

From V0.5 onward, each real mutation has two explicit authorization contexts:

- A development Agent must request the developer's approval immediately before
  each exact mutation, naming the operation and targets. Earlier, blanket, or
  implied approval does not apply.
- A product user's explicit current-session selection of the local real-mutation
  option authorizes V0.5 controlled real operations. UAC elevation or Real mode
  by itself is not consent, and consent must not be preselected or persisted.

The mutation path must retain typed operations, target validation, an operation
preview, and an audit record. Simulation remains the default path, and free-form
storage commands remain outside the product boundary.

## WinPool 1.x scope

WinPool 1.0 and the complete 1.x product line focus on storage topology,
management and editing, monitoring, settings, data safety, and release-quality
delivery. The Test and Development tabs remain visible navigation destinations,
but each contains only a short roadmap notice. Their complete user interfaces,
registered-directory test workflows, developer workspace, and AI Agent features
are outside every 1.x release and are planned as WinPool 2.0 features.

Disk-test, external-tool, and Development/AI diagnostics subsystems have been
removed from the 1.0 release path and are deferred to 1.x/2.0; they are not part
of the supported 1.x product surface.

## Architecture line

The V0.4 product line retains the accepted V0.13 visual baseline and the V0.2
multi-process rewrite, reduced to the two processes required for the 1.0 release
path:

- one unpackaged WinUI 3 App;
- one visible per-user tray Agent and SQLite writer;
- typed named-pipe IPC and deny-by-default execution policy.

The project version uses `Va.b` for a new product line and may use `Va.bc` for a
nonzero iteration. Architecture milestones stop at `Va.b`; database schema
revisions, algorithm IDs, and IPC compatibility identifiers are internal
contracts and do not form additional project versions.

### Internal engines

Two internal engines are long-term product assets:

- **Topology layout engine.** `TopologyLayoutEngine` plans topology layout in
  integer width/height units. Structure decisions — sibling row packing,
  column budgets, shrinking, row-height relaxation, and minimum widths such as
  the two-unit floor for layered pools sharing a row — belong to the unit plan
  and stay independent of pixels and DPI; pixel widths only stretch the
  finished unit plan to the available width. The Manage topology is the
  reference behavior. Before modifying this engine, read the execution pitfall
  record under `docs/Reference` (2026-09-05).
- **Hardware information engine.** The retained KS/StatSys-derived report
  factory produces a structured hardware report of 13 categories and 154
  defined items, each carrying Source, Status, and Warning evidence, collected
  through the embedded read-only PowerShell inventory. It currently feeds
  storage scans and storage-system documents and is a retained asset for a
  future full-hardware surface.

## Confirmed development route

The user confirmed the following product route on 2026-08-12. It defines phase
objectives, not permission to bypass the product boundary, a substitute for an
active Plan, or evidence that a phase is complete.

| Phase | Objective | Governing constraint |
| --- | --- | --- |
| V0.1 | Deliver the minimum prototype and establish the basic front-end visual direction. | Historical foundation. |
| V0.2 | Completely restructure the codebase; establish the baseline architecture and development rules. | Historical foundation. |
| V0.3 | Correct code defects and establish normal operation. | Historical foundation. Version confirmation does not mark remaining native or manual evidence as passed. |
| V0.4 | Complete visual/art polish and refine existing functions and basic interactions. Later V0.4 iterations also cover platform and portable-distribution work. | Preserve accessibility and the accepted structural baseline. |
| V0.5 | Deliver the minimum closed set of management and editing workflows required for the 1.0 storage-management product. | First phase permitted to introduce selected controlled real storage-structure operations under the explicit developer- and product-user authorization model; broad operation coverage is not a 1.0 requirement. |
| V0.6 | Complete the monitoring and storage-health experience required for 1.0. | Test execution and the full Test workspace are excluded; monitoring must retain explicit targets, bounded persistence, and truthful diagnostics. |
| V0.7 | Freeze the 1.0 feature scope and close remaining integration gaps across management, monitoring, settings, and data handling. | Test, developer, and AI Agent workspaces remain placeholders; no new feature family is introduced. |
| V0.8 | Close known release-blocking defects and quality gaps and begin the signed MSIX packaging path while retaining portable delivery. | Work is limited to the frozen 1.0 scope; unknown future defects cannot be pre-declared resolved. |
| V0.9 | Enter internal testing across the defined supported Windows platform matrix and correct findings. | Validate portable and MSIX install, upgrade, uninstall, startup, and data-location behavior across the named matrix. |
| V1.0 | Publish the formal release. | Requires the approved V0.9 release-readiness gate; Microsoft Store submission begins only after V1.0 is complete. A tag, binary upload, and GitHub Release each still require explicit authorization. |
| V1.x | Maintain the 1.0 product line with compatibility, reliability, security, and narrowly approved management or monitoring corrections. | The Test and Development tabs remain roadmap placeholders throughout 1.x. |
| V2.0 | Introduce the complete Test workspace and the complete developer and AI Agent workspace. | External engines remain separate typed adapters; public or automation contracts require a separately confirmed product design. |

Every implementation phase requires its own confirmed `docs/Plan.md`. The
deny-by-default executor, simulation-first storage editing, read-only inventory,
and data-redaction boundaries remain in force throughout this route; V0.5 real
mutation is the defined, explicitly authorized exception to the simulation-only
rule.

## Windows support

Published support from V0.44:

```text
Minimum supported: Windows 10 22H2 x64
Primary:           Windows 11 24H2 x64
                   Windows 11 25H2 x64
```

The compile-time Windows SDK / TFM is not the published minimum OS. WinPool
does not ship ARM64 or x86. Older Windows versions may still start and are not
a published guarantee.

## Installation route

WinPool currently ships only as an unpackaged, self-contained Windows x64
portable application. There is no released MSIX package and no Microsoft Store
listing. A future route in this document does not imply that its package exists.

| Mode | Availability | Intended behavior |
| --- | --- | --- |
| Portable | Implemented now and retained | The user controls the complete self-contained program directory. Data defaults to `%LocalAppData%\WinPool`; an explicitly selected writable `Data` directory beside the executable remains available. |
| Signed MSIX | Planned for V0.8–V0.9 | Develop and validate signing, identity, install, upgrade, uninstall, startup registration, data paths, recovery, and the supported Windows matrix. Portable delivery remains available. |
| Microsoft Store | Planned after V1.0 is complete | Prepare identity, privacy, support, certification, package, and listing material only after the formal V1.0 product and its release evidence exist. Store publication is not a V1.0 prerequisite. |

All modes must expose the same safety boundary. Packaging must not grant extra
storage-mutation authority, turn the Agent into a Windows service, or bypass
explicit target validation. Creating an account, reserving a product name,
uploading a package, or publishing a listing each requires explicit
authorization at that time.
