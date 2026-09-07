# Storage-structure product decisions

[English](Storage-Structure-Product-Decisions.md) | [简体中文（仅供阅读）](Storage-Structure-Product-Decisions.zh-CN.md)

Working record of intentional product decisions for creating and editing
storage pools. It is not yet part of [Product](Product.md). UI layout, control
inventories, and recommended numeric defaults belong in the active Plan or in
the archived editor design, not here.

Date: 2026-09-07.

---

Other tools treat a storage pool as a capacity bucket that can be sliced into
many spaces, with important parameters left to PowerShell or a wizard.
WinPool treats one tiered pool as one disk and one volume: tiers follow disks,
and edits stay in a draft until apply.

## One pool, one virtual disk, one user partition

Settings, Control Panel, and Server Manager allow several virtual disks in one
pool and several partitions on one virtual disk. WinPool treats that as the
wrong model. Disks that must be isolated go in a **new pool**. The product does
not pick a subset of pool members for a virtual disk (no Manual allocation). A
pool that already has several virtual disks may only be reduced to one; the
surface must not create a second. Extra partitions are a different job, not
part of create-pool or edit-pool.

## Tiering is the intended path, not an option

Other surfaces hide tiering in PowerShell or behind one Server Manager
checkbox. Workstation Settings often omit it. WinPool’s default is a
performance tier plus a capacity tier. SSD and HDD join by media. Hot data uses
Windows Storage Spaces tiering. Journal disks and write-back cache are not the
product centre.

## Tiers follow disks; there is no Create-tier operation

Microsoft creates an empty pool, optionally enables tiers, then carves a
space. WinPool creates the performance tier when the first SSD joins, and
stops drawing that tier when the last SSD leaves. There is no Add-tier action.
Empty real tiers are not shown, so the UI does not pretend a memberless tier is
an object to click.

## Hot spare and retired are optional simulated layers

Server Manager uses an Allocation drop-down (Automatic / Hot Spare / Manual).
WinPool does not make hot spare the default, and does not hide retired as an
invisible disk flag. Two pool switches show **Hot spare** and **Retired**
(unallocated). Only when a switch is on does that simulated layer appear so
disks can be dragged into it. That is not the same kind of layer as
performance or capacity, which appear automatically when data disks of that
media are present.

## Structure edits are one draft, applied once

Microsoft wizards commit as they go. An earlier WinPool split submitted
membership and properties separately, which snapped the working copy back.
Create and edit stay in one draft (membership and parameters together). A
human-language preview runs, then one apply writes the simulation. Real
machines stay read-only until explicit authorization.

## Creating a disk need not create a partition

Other wizards often bind “create a space” to formatting a volume. WinPool may
create the virtual disk and leave it RAW, and finish partitioning on the
disk/partition surface. Auto-creating the single user partition is the usual
path, not a required one.

## Parameters that other GUIs hide are editable here; inspection stays on Manage

Settings and Control Panel hide interleave, column count, and media type, so
users fall back to PowerShell and hit unsafe defaults. WinPool exposes those
fields while **editing this pool**. Manage remains the place to read health,
bus, and cannot-pool reasons. The structure surface is not a second object
inspector.

## Research-backed combinations warn or block; Microsoft defaults are not recommendations

256K interleave is not a silent default. ReFS may be chosen but is not claimed
to have the same long-run evidence as NTFS 64K. A single-SSD Mirror is allowed
with an explicit no-redundancy warning. That is intentional opposition to
“wizard next step” defaults.

## Out of this product path

A second virtual disk; Manual allocation; Journal disks as a designed path;
free-form storage commands on this surface; treating the structure editor as
Manage.
