# WinPool Product Documentation

WinPool is a Windows Storage Spaces inspection and management project. `V0.1` is an unpackaged, x64, read-only WinUI 3 test preview.

## Current product decision

- Location: all future application work remains under `Program\WinPool`.
- Application direction: C# WinUI 3, unpackaged desktop deployment.
- Window model: one window with title-bar tabs for Manage, Create, Test, Monitor, Development, and Settings. Only Manage and Settings currently contain content.
- Main layout: an upper operation area and a lower logic/topology area separated by a horizontal `GridSplitter`.
- Operation-area categories: `System`, `Pool`, `Tier`, `Disk`, and `Partition`, with a category-dependent vertical object selector and separate property and command columns.
- Logic area: always shows the complete storage graph through nested bordered containers; relationship lines are not used.
- Execution modes: normal launches start in Simulation; selecting Real as a standard user offers a one-time UAC restart that enters Real after elevation. Manual administrator launches still start in Simulation, and execution mode is never persisted.
- Future mutation model: storage-pool creation through planning, preflight, command review, confirmation, execution, verification, and audit.
- Settings: a full-workspace page containing theme, Windows/preset accent color, Chinese/English language, the synchronized Simulation/Real switch, About, and an external GitHub updates link.
- Product version: the single product-facing value is `V0.1`.
- Current visual implementation: stock theme-aware WinUI controls. Legacy button geometry and branded colors remain deferred.
- No real storage-pool creation or disk integration test may run on the current machine because every disk is in active use.

## Documents

1. [Product specification](01_Product_Specification.md)
2. [Information architecture and wireframes](02_Information_Architecture.md)
3. [Visual system](03_Visual_System.md)
4. [Operation safety model](04_Operation_Safety.md)
5. [Bilingual terminology](05_Localization_Terminology.md)
6. [Development roadmap](06_Development_Roadmap.md)
7. [Environment readiness](07_Environment_Readiness.md)
8. [Program implementation plan](08_Program_Implementation_Plan.md)

## Boundary of this phase

The source and tests live in the sibling `src` and `tests` directories. Historical implementation diaries and obsolete display references are archived outside this public repository under the parent project's `Old` directory.

The current release authorizes read-only discovery and presentation only. It does not authorize or implement real storage modification.
