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
simulated systems, run registered-directory file tests, monitor supported devices,
and execute explicitly reviewed support actions.

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

External DiskSpd, fio, Dite, RoboCopy, and RAMMap installations remain separate.
WinPool discovers or invokes them through typed adapters and validated targets; it
does not bundle their engines.

## Architecture line

The V0.4 product line retains the accepted V0.13 visual baseline and the V0.2
multi-process rewrite:

- one unpackaged WinUI 3 App;
- one visible per-user tray Agent and SQLite writer;
- one isolated TestWorker;
- one one-shot elevated Broker;
- typed named-pipe IPC and deny-by-default execution policy.

The project version uses `Va.b` for a new product line and may use `Va.bc` for a
nonzero iteration. Architecture milestones stop at `Va.b`; database schema
revisions, algorithm IDs, and IPC compatibility identifiers are internal
contracts and do not form additional project versions.

## Confirmed development route

The user confirmed the following product route on 2026-08-12. It defines phase
objectives, not permission to bypass the product boundary, a substitute for an
active Plan, or evidence that a phase is complete.

| Phase | Objective | Governing constraint |
| --- | --- | --- |
| V0.1 | Deliver the minimum prototype and establish the basic front-end visual direction. | Historical foundation. |
| V0.2 | Completely restructure the codebase; establish the baseline architecture and development rules. | Historical foundation. |
| V0.3 | Correct code defects and establish normal operation. | Historical foundation. Version confirmation does not mark remaining native or manual evidence as passed. |
| V0.4 | Complete visual/art polish and refine existing functions and basic interactions. | Preserve accessibility and the accepted structural baseline. |
| V0.5 | Complete management and editing workflows to provide a modern functional alternative to the legacy Windows storage GUIs. | First phase permitted to introduce controlled real storage-structure operations under the explicit developer- and product-user authorization model. |
| V0.6 | Complete testing and monitoring functions. | External tools remain typed adapters with explicit target validation. |
| V0.7 | Complete development-facing and AI Agent functions. | Do not freeze a public SDK, plug-in API, database contract, or wire protocol until internal models are stable. |
| V0.8 | Complete the remaining approved functions and close known release-blocking defects and quality gaps. | Begin the signed MSIX packaging path while retaining portable delivery; unknown future defects cannot be pre-declared resolved. |
| V0.9 | Enter internal testing across the defined supported Windows platform matrix and correct findings. | Validate portable and MSIX install, upgrade, uninstall, startup, and data-location behavior across the named matrix. |
| V1.0 | Publish the formal release. | Requires the approved V0.9 release-readiness gate; Microsoft Store submission begins only after V1.0 is complete. A tag, binary upload, and GitHub Release each still require explicit authorization. |

Every implementation phase requires its own confirmed `docs/Plan.md`. The
deny-by-default executor, simulation-first storage editing, read-only inventory,
data-redaction, and external-tool boundaries remain in force throughout this
route; V0.5 real mutation is the defined, explicitly authorized exception to the
simulation-only rule.

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
storage-mutation authority, turn the Agent into a Windows service, bundle the
external DiskSpd/fio/Dite/RoboCopy/RAMMap engines, or bypass explicit target
validation. Creating an account, reserving a product name, uploading a package,
or publishing a listing each requires explicit authorization at that time.
