# Visual System

## Current design intent

The first implementation deliberately does **not** migrate the existing frontend's button geometry, dark industrial palette, gold accent, or custom chrome. It does inherit the storage topology's block-enclosure relationship model.

Build and stabilize information architecture, selection behavior, topology, commands, accessibility, and resize behavior with stock WinUI controls and system theme resources first. The existing frontend remains a later style reference, not the current implementation template.

## Theme model

- Settings exposes System, Light, and Dark; System is the persisted default.
- Apply theme changes immediately. Follow Windows changes while System is selected.
- High contrast remains controlled by Windows and must override decorative theme choices.
- Do not choose a branded default until the UI structure is approved and stable.
- Use Mica for the long-lived window background when supported.
- Use Acrylic only for transient WinUI surfaces such as flyouts.
- Centralize tokens in shared resource dictionaries.

## Deferred legacy-style references

- Dark blue-gray branded surfaces.
- Gold research accent.
- Square/cut-corner buttons and custom button silhouettes.
- Dense custom panel chrome.
- Legacy hover colors and specialized control decoration.

These items may be reconsidered only after the neutral WinUI version is implemented and approved at wide, medium, and narrow widths. Migration must be token-based and must not change selection behavior, control semantics, safety hierarchy, focus, or accessibility.

## Geometry and density

- Use stock WinUI control geometry during the structural phase.
- Use spacing and typography for ordinary operation content.
- In the logic area, use square bordered parent/child containers because containment expresses the storage relationship.
- Do not use connector, branch, leader, callout, or arrow lines in the logic area.
- Keep technical tables dense but preserve 32-pixel minimum interactive row height and visible focus.
- Use 8-pixel spacing increments: 4, 8, 12, 16, 24, 32.

## Typography and icons

- Use Segoe UI Variable or the Windows platform default.
- Page title: 28–32 effective pixels.
- Section heading: 20–22.
- Body and table content: 14–16.
- Monospace text is reserved for commands, IDs, hashes, and raw diagnostic output.
- Use Fluent icons with accessible names; do not use unlabeled icon-only destructive actions.

## Native-control policy

- Use a single-page `Grid`; do not add a `NavigationView` or outer application navigation.
- Use a horizontal `GridSplitter` between the operation and logic areas.
- Use `CommandBar` for the selected object's commands.
- Use `ListView` for the vertical object selector.
- Use `ItemsRepeater`, `TreeView`, or a narrowly scoped topology control for the nested logic area.
- `InfoBar` for persistent warnings.
- `ContentDialog` for confirmations.
- Use a full-workspace Settings page opened by the sole title-bar Settings button. It replaces the category tabs, operation area, splitter, and topology until Back is invoked.
- `TeachingTip` only for contextual onboarding.
- CommunityToolkit is considered only after a verified native-control gap.
- Horizontal categories use a native horizontal tab surface.
- Vertical object navigation uses a single-selection `ListView`, not a custom vertical tab control.
- The nesting view uses purpose-built bordered grouped surfaces because containment is meaningful; its blocks should read as topology units rather than decorative cards.

## Safety-state presentation

- Execution mode stays visible in the title bar.
- Administrator state is represented only by the localized `[Administrator]` / `[管理员]` suffix in the title when elevated.
- Real remains selectable for a standard-user process and opens a localized confirmation for a one-time administrator restart.
- Simulation and Real must not rely on color alone; use text, control state, and an accessible status description.
- A mode change visibly invalidates an existing draft plan.
