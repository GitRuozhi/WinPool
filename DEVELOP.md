# WinPool Development Guide

## Development status

WinPool is currently in the **requirements-confirmed and frontend-visual-design stage**.

The immediate objective is a coherent, modern Windows storage interface. Backend completeness is not an acceptance requirement for this stage. Minimal read-only services, fake providers, and simulated storage graphs are appropriate when they allow frontend work to proceed safely.

The current executable should be treated as a frontend and interaction prototype with a small read-only backend, not as a completed storage-management product.

## Product goal

WinPool is a third-party Windows storage-system GUI intended to replace the aging Disk Management and Storage Spaces graphical interfaces.

The intended product will:

- show storage objects as one understandable hierarchy;
- create storage objects with complete, explicit, and reviewable parameters;
- provide modern testing and monitoring workflows;
- expose extension points for developers;
- make structured operations convenient for AI agents to inspect and invoke.

The current stage defines how these capabilities should appear and interact. It does not require all of them to be connected to a complete backend.

## Technology and deployment

The current solution uses:

- C#;
- WinUI 3;
- .NET 9;
- Windows App SDK;
- CommunityToolkit where an existing dependency already fills a specific gap;
- unpackaged, self-contained, Windows x64 deployment.

The SDK version used by the repository is pinned in `global.json`.

## Repository structure

```text
WinPool.slnx
src/
  WinPool.App/                    WinUI shell, pages, controls, view models, simulation data
  WinPool.Core/                   Domain records, selection state, topology and layout rules
  WinPool.Infrastructure.Windows/ Minimal read-only Windows services and inventory script
tests/
  WinPool.Core.Tests/
  WinPool.Infrastructure.Tests/
Release/                          Current test archive and checksum
```

The repository root intentionally contains only four Markdown documents:

```text
README.md
README_CN.md
AGENTS.md
DEVELOP.md
```

Historical documents are stored under the parent project's `Old` directory and are not active constraints.

## Architecture

### WinPool.App

`WinPool.App` owns:

- application startup and the main window;
- title-bar navigation and execution-mode presentation;
- Manage and Settings pages;
- frontend view models;
- localization and appearance behavior;
- storage-topology controls and layout panels;
- the fixed simulation snapshot;
- frontend export and notification services.

Frontend code may use simulation-specific objects and presentation models when this keeps visual iteration clear and safe.

### WinPool.Core

`WinPool.Core` contains the current shared domain records and pure behavior:

- storage-unit identities;
- storage snapshots;
- workspace selection;
- topology projection;
- flow and weighted layout calculations;
- execution-mode state;
- service interfaces used by the current application.

These types are internal architecture, not a frozen public SDK or serialization contract.

### WinPool.Infrastructure.Windows

`WinPool.Infrastructure.Windows` currently provides:

- privilege detection;
- the confirmed elevation-restart handoff;
- local preference persistence;
- a fixed read-only PowerShell inventory provider.

The inventory script may query Windows storage state, but it must remain read-only and must not accept free-form user commands.

## Frontend baseline

The current shell uses title-bar destinations for:

- Manage;
- Create;
- Test;
- Monitor;
- Development;
- Settings.

Only the frontend behavior required for current design validation needs to be functional. Create, Test, Monitor, and Development may remain prototypes or placeholders.

The Manage page is based on:

- an upper object-focused operation workspace;
- a lower complete storage-topology workspace;
- a horizontal splitter between them;
- System, Pool, Tier, Disk, and Partition categories;
- a category-dependent vertical object selector;
- nested enclosure blocks for relationships, without connector lines.

The frontend should continue to support:

- simulation and real-system presentation;
- Chinese and English;
- System, Light, and Dark themes;
- Windows and preset accent colors;
- keyboard and accessible names;
- high-contrast-safe presentation;
- adaptive behavior as the window narrows.

Use standard WinUI controls and theme resources first. Keep custom controls narrowly focused on topology or verified layout needs.

## Simulation and backend boundaries

Simulation is a normal development mode, not a fallback of last resort.

Use simulation when:

- a complex storage graph is needed for visual design;
- the local computer lacks a representative Storage Spaces configuration;
- a frontend workflow has no backend yet;
- testing the real system would introduce risk or unnecessary coupling.

Backend work should be limited to the smallest change needed for the current frontend task. Do not implement complete command execution, monitoring infrastructure, Dite integration, databases, services, or plug-in systems merely because the frontend shows their intended location.

Real mode currently demonstrates execution-mode and privilege UX. It does not authorize or enable storage mutation.

## Build and test

From the WinPool repository root:

```powershell
dotnet test WinPool.slnx -c Release
dotnet build src\WinPool.App\WinPool.App.csproj -c Release -p:Platform=x64
```

To produce the unpackaged self-contained build:

```powershell
dotnet publish src\WinPool.App\WinPool.App.csproj `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  --self-contained true
```

For frontend changes, build success alone is not sufficient. Launch the generated `WinPool.App.exe` and confirm:

- a responsive top-level window appears;
- the intended page and topology render;
- selection and navigation work;
- the relevant theme and language state remain usable;
- the layout remains understandable at the widths affected by the change.

Use simulation for normal frontend verification. Run real inventory only when the task specifically requires read-only Windows integration.

## Testing policy

Current tests cover pure Core behavior and the fixed read-only Windows inventory boundary.

New frontend work should favor:

- pure tests for topology, selection, and layout rules;
- fake inventory providers;
- deterministic simulation snapshots;
- mocked privilege and service state;
- visual and runtime checks for theme, localization, accessibility, and resizing.

Tests must not create or modify storage objects.

Hardware-specific inventory assertions should be separated from portable unit tests when the test suite is expanded.

## Near-term priorities

1. Refine requirements and remove ambiguity from the four authoritative root documents.
2. Stabilize frontend information architecture and visual behavior.
3. Improve responsive, bilingual, keyboard, and high-contrast behavior.
4. Expand simulation scenarios to cover representative storage hierarchies and abnormal states.
5. Keep the real Windows backend read-only and make only frontend-driven minimal changes.
6. Define developer and AI integration concepts only after the frontend object and operation model is stable.
7. Begin mutating backend design only after a separate user-approved plan and disposable hardware environment exist.

## Contribution boundaries

- Do not add storage mutation in the current stage.
- Do not treat visible prototype functions as authorization to implement their backend.
- Do not create new documentation directories or planning files when one of the four root documents is the correct home.
- Do not use archived documents as current requirements.
- Do not commit, push, tag, or publish unless explicitly requested.
