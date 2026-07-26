# Development Roadmap

## Gate 0 — Product design package

Deliverables:

- approved single-window, two-area information architecture;
- text descriptions for every horizontal category's operation area;
- execution-mode, administrator, settings, topology, and storage-pool safety rules;
- visual system and localization terminology;
- operation-safety contract;
- verified environment-readiness record.

Exit: the documents are internally consistent, superseded UI images are archived, and no application code exists.

Status: complete for the third-edition text specification and UI reference set. This does not start Gate 1.

## Gate 1 — Environment and scaffold

- Install the Windows App SDK C# workload and WinUI templates.
- Create an unpackaged C# WinUI 3 project.
- Establish an unpackaged WinUI solution under `Program\WinPool`, a single-window two-area shell, title-bar execution controls, a full-workspace Settings page, theme resources, localization resources, diagnostics, and test projects.
- Decide and record whether existing Python capabilities are ported, invoked, embedded, or replaced.

Exit: clean build, verified top-level window, System/Light/Dark and high-contrast behavior, Chinese/English switching, no outer navigation.

## Gate 2 — Read-only discovery MVP

- Implement physical disk, pool, tier, virtual disk, and volume discovery.
- Correlate objects with stable identifiers.
- Add the upper operation area, horizontal categories, dynamic vertical object selector, lower complete topology, object details, refresh, warnings, and snapshot export.
- Add the title-bar Simulation/Real selector, administrator title suffix, mocked privilege state, and forced Simulation startup.
- Do not expose mutating commands.

Exit: repeatable discovery on the target machine, topology-to-operation selection synchronization, no storage changes, verified bilingual UI and keyboard navigation.

## Gate 3 — Storage-pool planning

- Implement `OperationPlan`, pool-creation preflight results, generated-command review, plan hashing, and audit storage.
- Add storage-pool creation planning without executing a real storage command.
- Validate system-disk protection and state-change invalidation.
- Validate that mode changes invalidate draft plans and Simulation never invokes the mutating executor.

Exit: pool creation can be previewed and blocked safely using fake inventory and mocked executors.

## Gate 4 — Storage-pool creation implementation

- Add administrator-state enforcement, exact-plan execution, state verification, and append-only audit history for `New-StoragePool`.
- Keep the confirmed UAC restart flow separate from storage execution: elevation can enter Real, but it never bypasses planning, preflight, confirmation, verification, or audit.
- Compile and perform offline verification only on the current machine.
- Do not execute the feature until a separate disposable disk test environment is explicitly approved.

Exit: code is complete and offline checks pass; real-hardware validation remains an explicit unresolved gate.

## Gate 5 — Testing and reports

- Integrate Dite through a separately reviewed boundary.
- Add test orchestration, result import, charts, and report generation.
- Preserve raw evidence under `Tests`; keep runtime output and formal evidence boundaries explicit.

Exit: management, testing, and reporting share object identities without mixing program source and evidence storage.

## Gate 6 — Packaging and release

- Reassess unpackaged deployment, runtime prerequisites, signing, update channel, and support boundaries.
- Produce public documentation and release validation.

No release, tag, installer, or GitHub publication is part of the current phase.
