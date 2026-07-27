# Agent Instructions for WinPool

This file defines the current operational rules for AI agents working in `Program\WinPool`.

The parent project rules in `..\..\AGENTS.md` also apply. When the two files differ, preserve the stricter safety, evidence, archive, and Git rule unless the user explicitly changes it.

## Authoritative documents

Only these four Markdown files at the WinPool repository root define the current documentation baseline:

1. `README.md` — user-facing English introduction.
2. `README_CN.md` — user-facing Simplified Chinese introduction.
3. `AGENTS.md` — AI-agent rules.
4. `DEVELOP.md` — developer architecture and workflow.

Documents archived under the parent project's `Old` directory are historical references. They do not constrain current implementation and must not be treated as current requirements.

Do not recreate a `Docs` directory without explicit user approval.

## Current project stage

WinPool is in the **requirements-confirmed and frontend-visual-design stage**.

The current priority is to refine:

- product information architecture;
- shell and title-bar behavior;
- storage-object hierarchy and topology presentation;
- selection and interaction behavior;
- theme, language, accessibility, and responsive layout;
- frontend representation of future Manage, Create, Test, Monitor, Development, and Settings workflows.

Backend implementation is intentionally minimal. It only needs to support the current frontend design and verification work.

## Backend policy

- Simulated data is an approved first-class development source.
- A frontend element does not require a complete production backend in the current stage.
- Prefer a small fake or read-only provider over premature integration.
- Do not freeze a public API, plug-in contract, IPC protocol, or C#/Python wire format.
  The current JSON persistence (settings, `machine.json`, simulated system documents)
  is internal and may change without migration.
- KS/StatSys read-only collection has been explicitly requested. Preserve its
  structured report boundary, but do not add a Python runtime, Flask service, or
  external automation.
- Keep current Windows inventory code fixed and read-only.
- Do not add a storage executor or any mutating command against the local machine.
  The only permitted write path is the simulated operation service acting on
  simulated documents.
- Sensitive hardware values may remain unmasked in memory to support the
  privacy toggle, but everything persisted, exported, or imported must pass
  through `StorageSystemDocumentSanitizer.RedactSensitiveData`.
- Do not store or publish a `.ps1` inventory file. Fixed read-only PowerShell text
  must be embedded in the assembly and provided through standard input.

The existing Core and Infrastructure projects may remain broader than the minimum frontend need, but new work should not expand them merely to make every visible prototype functional.

## Product direction

WinPool is a third-party Windows storage-system GUI intended to replace the aging Disk Management and Storage Spaces graphical experiences.

The product direction includes:

- clear hierarchical storage-object relationships;
- complete and reviewable parameters when creating storage objects;
- modern testing and monitoring;
- reserved developer integration points;
- structured operations suitable for AI-agent invocation.

These are product goals, not claims that the current backend already implements them.

## Current frontend baseline

- Use a single WinUI 3 desktop window on .NET 10.
- Keep title-bar destinations for Manage, Edit, Test, Monitor, Development, and Settings.
- The Manage workspace uses an upper operation area and a lower complete topology area separated by a horizontal splitter.
- The upper area shows System, Pool, Tier, Disk, and Partition as vertical tabs, one shared comparison table (columns are the category objects), and command buttons with icons.
- Selecting a table column highlights it, centers it horizontally, and stays in sync with topology selection.
- The lower topology shows the complete storage structure through nested enclosure blocks; containment is the relationship language, with no relationship lines. System disks and unhealthy objects carry badges.
- All mutating and destructive workflows live on the Edit page and act on simulated systems only; Manage keeps entries that navigate to Edit.
- The Monitor page samples local physical disks through read-only PDH counters; the Development page shows a read-only log of executed commands; the Test page is a placeholder.
- Workspace selection and topology highlighting remain independent where the current interaction requires it.
- Network and other logical storage groups must not be counted as Windows Storage Pools.
- Use stock, theme-aware WinUI controls and resources while the visual system is still being refined.
- Preserve light, dark, system-theme, high-contrast, keyboard, and bilingual design considerations.

Prefer built-in WinUI controls and shared resources. Add custom controls only when the storage-topology requirement or a verified platform limitation justifies them.

## Execution-mode boundary

- Normal launches start in Simulation.
- Execution mode is never persisted.
- A standard-user process may demonstrate the confirmed UAC restart flow when Real is selected.
- Real currently changes privilege and frontend state only.
- Real must not enable storage mutation in this stage.
- A manual administrator launch still starts in Simulation.

## Safety rules

1. Do not add or run commands that mutate the local machine.
2. Do not initialize, clear, format, resize, repair, optimize, remove, or create local storage objects.
   Simulated operations on simulated documents are the approved write path; every
   local mutation entry point must stay disabled, including in Real mode.
3. Do not use a real attached disk as a temporary test target.
4. Do not expose unmasked serial numbers in clipboard, logs, persisted files, or
   exports. The settings privacy toggle may reveal them in UI text only.
5. Do not directly delete files.
6. Move superseded WinPool content to the parent project root `Old`, preserving its relative path when practical.
7. Move low-value generated material to the parent project root `Rubbish`.
8. Do not create local `Old`, `old`, `OLD`, `旧`, or `Rubbish` directories inside the WinPool repository.
9. Do not commit, push, tag, publish, or create a release unless explicitly requested.
10. Preserve unrelated user changes in a dirty working tree.

## Documentation rules

- Keep exactly the four authoritative Markdown files at the WinPool repository root unless the user explicitly changes this policy.
- User-facing wording belongs in `README.md` and `README_CN.md`.
- AI operational rules belong in `AGENTS.md`.
- architecture, setup, and developer workflow belong in `DEVELOP.md`.
- Do not use archived documents as active requirements.
- When product direction changes, update the relevant authoritative file rather than creating another planning document.

## Verification

For documentation-only changes:

- verify that the WinPool root contains only the four approved Markdown files;
- verify all links and referenced paths;
- verify English and Chinese product statements describe the same stage and scope.

For application changes:

- build the affected project;
- run relevant tests;
- launch the unpackaged x64 application;
- confirm a responsive top-level window and the expected UI;
- use simulated data for frontend verification unless real read-only inventory is specifically needed.

Real hardware mutation is not an accepted verification method.
