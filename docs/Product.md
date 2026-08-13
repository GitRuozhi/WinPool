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

Real storage-structure mutation is not part of the V0.3 product boundary. WinPool
must not initialize, clear, format, create, remove, repair, or resize real disks,
partitions, volumes, Storage Pools, Storage Tiers, or Virtual Disks during this
line. V0.5 is the earliest stage permitted to introduce those operations through
reviewed typed plans; it is not a permanent simulation-only product.

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

## Current architecture line

V0.3 retains the accepted V0.13 visual baseline and the V0.2 multi-process rewrite:

- one unpackaged WinUI 3 App;
- one visible per-user tray Agent and SQLite writer;
- one isolated TestWorker;
- one one-shot elevated Broker;
- typed named-pipe IPC and deny-by-default execution policy.

The project version follows `Va.bc` exclusively. Architecture milestones stop at
`Va.b`; database schema revisions, algorithm IDs, and IPC compatibility
identifiers are internal contracts and do not form additional project versions.

## Confirmed development route

The user confirmed the following product route on 2026-08-12. It defines phase
objectives, not permission to bypass the product boundary, a substitute for an
active Plan, or evidence that a phase is complete. The current local
implementation version is V0.37, an iteration in the V0.3 line.

| Phase | Objective | Status or governing constraint |
| --- | --- | --- |
| V0.1 | Deliver the minimum prototype and establish the basic front-end visual direction. | Historical foundation. |
| V0.2 | Completely restructure the codebase; establish the baseline architecture and development rules. | Historical foundation. |
| V0.3 | Correct code defects and establish normal operation. | Current product line. Version confirmation and automatic checks do not replace remaining native, manual, or platform evidence. |
| V0.4 | Complete visual/art polish and refine existing functions and basic interactions. | Future phase; preserve accessibility and the accepted structural baseline. |
| V0.5 | Complete management and editing workflows to provide a modern functional alternative to the legacy Windows storage GUIs. | First phase permitted to introduce controlled real storage-structure operations under the explicit developer- and product-user authorization model. |
| V0.6 | Complete testing and monitoring functions. | External tools remain typed adapters with explicit target validation. |
| V0.7 | Complete development-facing and AI Agent functions. | Do not freeze a public SDK, plug-in API, database contract, or wire protocol until internal models are stable. |
| V0.8 | Complete the remaining approved functions and close known release-blocking defects and quality gaps. | Unknown future defects cannot be pre-declared resolved; acceptance must use recorded evidence. |
| V0.9 | Enter internal testing across the defined supported Windows platform matrix and correct findings. | The matrix must name supported Windows versions, editions, architectures, hardware/device coverage, and required human evidence. |
| V1.0 | Publish the formal release. | Requires the approved V0.9 release-readiness gate; a tag, binary upload, and GitHub Release each still require explicit authorization. |

Every implementation phase requires its own confirmed `docs/Plan.md`. The
deny-by-default executor, simulation-first storage editing, read-only inventory,
data-redaction, and external-tool boundaries remain in force throughout this
route; V0.5 real mutation is the defined, explicitly authorized exception to the
simulation-only rule.
