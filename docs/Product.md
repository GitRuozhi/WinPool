# WinPool Product Direction

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
partitions, volumes, Storage Pools, Storage Tiers, or Virtual Disks. Adding such a
path requires a separately approved stage and a disposable hardware environment.

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

Architecture, database schema, algorithm, and IPC versions remain independent of
the product-facing source checkpoint.

## Roadmap

1. Complete V0.3 documentation, packaging, and manual acceptance without expanding
   the real-mutation boundary.
2. Converge compatibility evidence, native inventory parity, accessibility,
   long-running monitoring, external-tool, recovery, and data-migration debt.
3. Define a public developer or AI integration surface only after internal object
   and operation models are stable.
4. Consider real storage mutation only through a separately approved safety plan.
