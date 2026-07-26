# Operation Safety Model

Storage management changes can destroy data or make a pool unavailable. WinPool therefore treats every mutation as an auditable operation plan.

## Execution-mode gate

```text
ExecutionMode = Simulation | Real
PrivilegeState = StandardUser | Administrator
```

- Normal launches start in Simulation. Real is never persisted.
- A standard-user process selecting Real receives an explicit restart confirmation before WinPool requests UAC.
- If the user approves both prompts, the elevated successor uses a one-time startup argument and enters Real directly; the original process closes only after the successor starts.
- Cancelling either prompt leaves the original process in Simulation without treating cancellation as an error.
- A manual administrator launch still starts in Simulation and requires an explicit user switch.
- Returning to Simulation is always allowed.
- Any mode change invalidates every unexecuted plan and its confirmation.
- Administrator privilege never bypasses preflight, impact review, confirmation, plan-hash validation, verification, or audit.

Simulation and Real use the same typed plan. Simulation performs validation and generates the same review representation but never invokes a mutating PowerShell command. Its result state is `Simulated`, never `Succeeded`.

## Required lifecycle

```text
Scan → Plan → Preflight → Review impact → Confirm → Privilege gate
     → Final preflight → Execute or Simulate → Verify → Audit
```

No step may be skipped for a mutating operation.

## Plan

An `OperationPlan` must contain:

- stable target identifiers, not display names alone;
- requested execution mode and privilege state at plan creation;
- the source snapshot version;
- intended final state;
- generated PowerShell command or command sequence;
- known affected objects and dependencies;
- destructive and irreversible effects;
- required privilege state;
- a plan hash that changes whenever the plan changes.

## Preflight

Preflight runs immediately before confirmation and again immediately before Real execution. Because WinPool must already be elevated before Real can be selected, the second check does not trigger an elevation transition.

Blocking conditions include:

- any selected target is the system, boot, page-file, crash-dump, or otherwise protected disk;
- a disk is not eligible for the intended pool operation;
- health or operational state makes the command unsafe;
- target identity changed since the plan was created;
- free capacity, supported tier size, column count, or resiliency constraints are not satisfied;
- dependent objects would be lost but are not explicitly included in the impact review;
- current state cannot be scanned or verified reliably.

Warnings do not silently downgrade blocking conditions.

## Confirmation

- Show object identities, capacities, generated commands, and data-loss impact.
- Non-destructive changes require an explicit confirmation dialog.
- Destructive changes require typed confirmation using the target name plus a second explicit confirmation.
- Confirmation is invalidated when the plan hash or target snapshot changes.

## Execution

- Real execution requires an administrator process. WinPool may create that process only through the explicit confirmation and UAC flow described above.
- Execute the exact reviewed plan; never regenerate different commands after confirmation.
- Capture timestamps, command text, standard output, standard error, process exit code, and cancellation state.
- Do not claim transactional rollback unless the complete operation is verifiably reversible.
- If only part of a sequence succeeds, stop, mark partial failure, preserve evidence, and present manual recovery guidance.
- In Simulation, stop before process creation and record that no mutating command was started.

## Verification and audit

- Rescan affected objects after execution.
- Compare actual state with the intended state.
- Store before/after snapshots and verification differences.
- An `AuditRecord` contains plan hash, confirmation type, privilege state, command evidence, result, and verification.
- The record also contains requested mode, effective mode, privilege state, mode-change time, and whether a mutating process was actually started.
- History is append-only from the UI. Export does not alter the source record.

## Required safety scenarios

1. A system disk is selected for pool creation: blocked before confirmation.
2. A disk becomes unavailable after planning: plan invalidated at second preflight.
3. A tier size is unsupported or zero: creation blocked with the underlying evidence shown.
4. A multi-command operation partially succeeds: execution stops and records partial failure.
5. A command exits successfully but state verification fails: result is “verification failed”, not “success”.
6. The user changes execution mode after confirmation: the plan and confirmation are invalidated and no command runs.

## Current-machine prohibition

The current development machine has no spare storage test environment and every disk is in active use.

Until a separate disposable test environment is explicitly provided:

- do not execute `New-StoragePool`;
- do not change `CanPool`, disk initialization, partitions, volumes, tiers, virtual disks, or pool membership;
- do not run an elevated storage-operation integration test;
- do not use a currently attached disk as a “temporary” test target;
- do not simulate safety by selecting a real disk and cancelling at the final step.

Allowed verification is limited to compilation, static analysis, pure unit tests with fake inventory objects, serialization tests, command-generation snapshots, and mocked executor behavior.

## Storage-pool creation contract

The initial mutation is storage-pool creation only.

Required input:

- new pool friendly name;
- selected storage subsystem identity;
- two or more explicitly selected `PhysicalDisk` objects;
- stable disk unique IDs captured by the current scan.

Preflight must re-query every disk and block when:

- `CanPool` is not true;
- health or operational status is unsuitable;
- the disk is a boot, system, page-file, crash-dump, or protected disk;
- the disk already belongs to a non-primordial pool;
- the current unique ID, capacity, or bus/media identity differs from the reviewed snapshot;
- the requested pool name already exists;
- the selected storage subsystem changed or cannot be identified uniquely.

The reviewed command is based on:

```powershell
New-StoragePool -FriendlyName <name> `
  -StorageSubsystemFriendlyName <subsystem> `
  -PhysicalDisks <explicitly re-resolved disk objects>
```

The implementation must not concatenate unescaped user input into a free-form command string. The operation plan stores typed parameters and generates a review representation separately from execution.

Confirmation requires the pool name and selected disk count. Immediately before Real execution, preflight runs again, the reviewed plan hash must still match, and the exact plan is executed. Success requires a post-operation rescan that finds the new pool with the expected disk identities; otherwise the result is verification failure.

On the current machine, the Real branch may be compiled and tested only through a mocked privilege service and mocked executor. No test may launch `New-StoragePool`.
