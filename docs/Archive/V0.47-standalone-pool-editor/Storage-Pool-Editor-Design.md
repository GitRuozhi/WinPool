# WinPool Standalone Storage-Pool Editor Design

[English](Storage-Pool-Editor-Design.md) | [简体中文（仅供阅读）](Storage-Pool-Editor-Design.zh-CN.md)

| Item | Value |
| --- | --- |
| Status | Accepted by the developer; frozen in this Archive folder. The active implementation Plan is `docs/Plan.md` |
| Date | 2026-09-07 |
| Scope | 1.x standalone editing on workstations and standalone servers. Expose Storage Spaces capabilities that PowerShell provides and the Microsoft GUIs omit |
| Out of scope | Cluster / Storage Spaces Direct / Windows Admin Center / cross-node fault domains; creating a second virtual disk |
| Volume | Storage structure editor: one virtual disk + one user data partition. Extra partitions belong on Disk/partition editor |

Confirmed boundaries:

1. Standalone only: workstation and standalone-server Storage Spaces in full. Fill GUI gaps from PowerShell. No cluster / S2D / WAC.
2. Workstation and standalone-server pools are first-class.
3. Pool structure plus the virtual disk's volume. Create/modify on this surface supports one disk and one partition; extra partitions go to another page. Checkbox "Create a partition when creating the disk", default on; partition creation may be skipped.
4. This accepted design is frozen here; the active Plan is `docs/Plan.md`.
5. Research conclusions are changeable defaults: prefill `64K + 64K`, SSD Mirror / HDD Parity; warn on `256K`.

Later amendments that are part of this accepted design:

1. Retire the current Edit page. The lower half becomes **Storage structure editor**; the upper half becomes **Disk/partition editor**.
2. Disk/partition editor: two control groups, one for disks and one for partitions.
3. Storage structure editor: topology on the left; structure operations upper-right; pool / virtual-disk / partition properties lower-right.
4. 1.0 must not create a second virtual disk. A pool that already has several may be reduced to one. Anything else belongs on the Development-page command line and is outside 1.0.
5. Virtual-disk volumes support NTFS and ReFS.
6. Topology does not draw empty tiers. Adding a tier is a structure operation; the strip appears only after it has members.
7. Apply preview uses a human-language step list. The 1.0 structure page does not expand raw commands.

---

## 1. Problem

Microsoft splits one Storage Spaces model across three incomplete GUIs. The full capability is PowerShell-only.

| Surface | What it can do | What it hides |
| --- | --- | --- |
| Windows 11 Settings → Storage Spaces | Create pool and space; Simple / two-way / three-way / parity / dual parity; optimize; replace disks | Tiers, interleave, columns, thin/fixed, write-back cache, media type, hot spare/journal, logical sector |
| Control Panel Storage Spaces | The same, plus rename and prepare-for-removal | The same |
| Server Manager File and Storage Services → Storage Pools | Thin/fixed, a tier checkbox, hot-spare Allocation, repair, jobs | Interleave, columns, per-tier resiliency, write-cache size; wizard column defaults are often wrong |
| PowerShell `Storage` module | Complete | Default Interleave **256K**, NTFS default **4K**; template tiers vs virtual-disk tiers are two objects; docs omit parameters |

Research conclusion (current tested recommendation; Windows 11 has not received equivalent testing):

```text
64K interleave + 64K NTFS cluster = current tested recommendation.
```

- `64K + 64K`: stable in current tests
- `64K + 256K`: two-stage NTFS corruption
- `256K + 64K`: a failure has occurred; not currently recommended
- `256K + 256K`: direct NTFS corruption, Event 55

The product must show the full standalone Storage Spaces object once, and create or modify it from one previewable draft. It must not become a fourth incomplete wizard.

---

## 2. Goals and non-goals

### Goals

- One editor model for workstation and standalone server.
- Expose parameters PowerShell can set and Microsoft GUIs cannot (write-back cache is an advanced field that follows Windows Auto; see §8.1).
- Tiers are first-class, not an advanced extra.
- The create path yields: one pool + media tiers + **one** virtual disk + (by default) one user volume.
- Prefill research defaults; allow change; warn on dangerous combinations; do not silently use Microsoft defaults.
- Operations are previewable and auditable. Simulation is the default. Real mutation needs in-session authorization.
- Structure and parameters belong to one draft and one apply.
- Retire Edit; split into Storage structure editor and Disk/partition editor.

### Non-goals

- Cluster, S2D, CSV, nested resiliency, WAC dashboards.
- Full disk management inside the structure editor (unpooled multi-partition, dynamic disks, Storage Replica).
- **Creating a second virtual disk.** This is a product rule, not a deferral. It is outside 1.0. If needed, use the Development-page command line.
- Freezing research conclusions as unchangeable.
- A "simple mode" that hides interleave and columns again.
- Showing raw PowerShell on the 1.0 structure page.

---

## 3. User model

Do not teach template tiers, then virtual-disk tiers, then deleting templates. That is a PowerShell ritual.

```text
Available disks (primordial / not in a pool)
        ↓ join the pool, with a role
Storage pool
        ↓ tiers by media (or Unallocated)
Tiers: Performance / Capacity / Dedicated (SCM) / Unallocated / Hot spare / Journal
        ↓ carve capacity
One virtual disk
        ↓ optional
One GPT user volume (plus a silent MSR)
```

Template tiers (size 0 on the pool) and virtual-disk tiers (sized, on the virtual disk) are one product concept: **specifications live on the pool; consumption lives on the virtual disk.**

| User term | Meaning | Do not call it |
| --- | --- | --- |
| Storage pool | Collection of physical disks | RAID card, array |
| Tier | Data placement by media and resiliency | Cache disk (a tier is not a write cache) |
| Unallocated | Pool member not in any data tier | Disconnected, unallocated (the latter sounds like a partition gap) |
| Hot spare | Usage=HotSpare; not in the current stripe | Unallocated |
| Journal | Usage=Journal for write-back cache | Performance tier |
| Virtual disk / storage space | A disk carved from the pool | Volume, partition |
| User volume | The only data partition plus filesystem on that virtual disk | A second virtual disk |
| Interleave | Bytes written to **one** physical disk per stripe | Full stripe width |
| Columns | Disks spanned by one full stripe; not disk count under mirror | "How many disks are used" |
| Resiliency | Simple / Mirror / Parity | Exact RAID 0/1/5 (comparison only) |
| Write-back cache | Storage Spaces `WriteCacheSize` on the virtual disk | The software/hardware disk cache in the V10 article |

---

## 4. Editing philosophy

1. Structure first, parameters second. Topology is the main surface.
2. One draft, one apply. Drags, tier changes, names, interleave, and the partition checkbox all enter one target state. The committed system does not change until Apply.
3. Recommendations are defaults, not locks. Warn on 256K combinations.
4. With stored data, separate "still allowed" from "needs rebuild". Refusals name the object and the reason; they must not silently revert the form.
5. One virtual disk is a product contract. Create yields one. Existing extras may be reduced to one.
6. Topology does not draw empty tiers. Adding a tier is an upper-right structure action; the strip appears after a disk joins it.

---

## 5. Information architecture

Retire the current Edit page.

```text
Navigation: …  Manage  Storage structure  Disk/partition  Monitor  Settings  …
```

### 5.1 Storage structure editor (former Edit lower)

```text
Left: topology of the pool
  pool
    virtual disk (target: one)
    performance tier · drawn only with members
    capacity tier · drawn only with members
    Unallocated · disks…
    hot spare / journal

Upper right: structure operations
  new/dissolve pool, apply draft
  add/remove tier
  add/evict/retire/hot-spare disk
  reduce to one virtual disk (when extras exist)
  optimize / repair

Lower right: properties of the selection
  pool / virtual disk / partition
```

A virtual disk with extra user partitions: lower-right partition properties are read-only, with a link to Disk/partition editor.

Several virtual disks: draw them all; upper right offers "reduce to one" (delete extras, confirm if they hold data). No "create another".

### 5.2 Disk/partition editor (former Edit upper)

Unpooled disks, and virtual disks that already have more than one user partition.

Two control groups: disk actions (online/offline, initialize, convert) and partition actions (new/delete/extend/shrink, format NTFS/ReFS, letter, label). Do not mix them into one list of sometimes-disabled buttons.

### 5.3 Draft bar

Each page has its own draft. Apply writes the simulation document or, when authorized, the real system. Preview: human-language steps, capacity, warnings. No raw commands.

---

## 6. Create

Fill one target on a blank pool draft.

- Media type must be editable before pooling. System/boot disks are excluded. Disks with user data require a second confirmation.
- Disk roles: data (auto), data (manual), hot spare, journal, retire (modify only).
- Prefill Performance for SSD, Capacity for HDD, Dedicated for SCM. Topology still hides a tier until it has a member.
- Defaults: SSD Mirror ×2 (Simple if one disk); HDD Parity redundancy 1; equal-capacity columns = n, mixed = n−1; Interleave 64K.
- Columns and interleave are first-class. Interleave choices include 16K/32K/64K/128K/256K. 256K warns.
- One virtual disk. Name follows the pool. Per-tier size defaults to nearly the tier maximum. Tiers force Fixed provisioning. Logical sector defaults to 4K. No second virtual disk.
- Checkbox **Create a partition when creating the disk**, default on: GPT, silent MSR, one user primary, format. Filesystems: NTFS and ReFS. Prefill NTFS 64K. ReFS is offered; SKUs that cannot create ReFS disable it with a reason. ReFS has no equivalent long-run evidence; say so when it is selected.
- Unchecked: RAW virtual disk; finish on Disk/partition editor.
- Preview: target topology, capacity, warnings, human-language steps. No PowerShell dump on this page.

---

## 7. Modify

- Join pool → matching tier, or Unallocated if that tier does not exist yet (then Add tier).
- Same-media tier moves only. Evict keeps the disk in the pool; if the tier then has no members, **the strip disappears** and the spec remains lower-right until the user deletes the tier.
- Unallocated → tier is the path that adds layers to an all-Unallocated pool. It must apply, not remain visual-only.
- Stored data = filesystem present and used space > 0. Rename, add disk, optimize, repair, evict still allowed. Interleave, columns, resiliency, provisioning, logical sector, format-equivalent field changes are refused with a reason.
- Reducing several virtual disks to one: delete extras. No silent data merge. Extras with data require confirmation.
- Maintenance: optimize pool, repair virtual disk, optimize volume (tier heat-map jobs are long-running and must be visible).

---

## 8. Defaults and warnings

| Parameter | Prefill | Note |
| --- | --- | --- |
| SSD resiliency | Mirror ×2; Simple if one disk | Warn if a single SSD uses Mirror |
| HDD resiliency | Parity, redundancy 1 | Dual parity is explicit, not default |
| HDD columns | n if equal size; n−1 if mixed | Do not use Windows' automatic 3 |
| Interleave | 64K | Do not treat 256K as the recommendation |
| Provisioning | Fixed when any tier exists | Thin only without tiers |
| Logical sector | 4K | 512 allowed |
| File system | NTFS | ReFS equally supported |
| Cluster size | 64K | Do not treat 4K as the recommendation |
| Auto-create partition | Checked | |
| Write-back cache | Windows Auto | **Not a research conclusion**; see §8.1 |

Warn, do not silently block except Thin+tiers (Microsoft also refuses that): 256K on either side; single-SSD Mirror; too few columns; too few disks for three-way mirror (at least five excluding hot spares).

Windows 11 has not received equivalent testing. ReFS has no equivalent long-run evidence to NTFS 64K.

### 8.1 Write-back cache vs the research article

`WriteCacheSize` is a Storage Spaces virtual-disk write-back region, usually on SSD or Journal disks. Settings and Control Panel barely expose it.

The V10 article's "Cache control" compares OS software cache and HDD hardware write cache on random-write numbers. It is not `New-VirtualDisk -WriteCacheSize`. The article's `New-VirtualDisk` does not set that parameter.

1.0: advanced field, Windows Auto, not a research prefill, not named "cache" in the same breath as the performance tier.

---

## 9. Capability matrix

The structure editor must cover standalone PowerShell capabilities the GUIs omit, except creating a second virtual disk. NTFS and ReFS are both supported. Multi-partition work belongs on Disk/partition editor. Cluster/S2D is out.

---

## 10. Workstation and standalone server

One editor. Probe SKU and features. Do not hide tiers on Windows 11. Disable ReFS creation when the SKU forbids it; do not remove ReFS from the product. Enclosure awareness appears only when an enclosure exists (not S2D fault domains). Real mutation still needs explicit authorization; UAC is not consent.

---

## 11. Handoff

| Case | Where |
| --- | --- |
| Unpooled disk partition/format/offline | Disk/partition editor |
| Virtual disk with **more than one user partition** | Structure page read-only → Disk/partition editor |
| Pool with **more than one virtual disk** | Structure page may reduce to one; cannot create another |
| Auto-create partition unchecked | Disk/partition editor to initialize |
| Free-form commands, second virtual disk | Development-page command line (the 1.x Development tab remains a placeholder and is not a 1.0 deliverable) |

User partitions exclude GPT MSR and EFI. MSR is created silently when auto-create partition is on.

---

## 12. Safety

- Simulation is the default path.
- Real structure mutation: not before V0.5, typed path only, named operation and targets, consent not preselected or persisted.
- The plan states which data will be deleted, whether a virtual disk is rebuilt, and whether a volume is formatted.
- Neither 1.0 editor page offers free-form PowerShell.
- Hardware serials in persistence/export pass redaction.

---

## 13. Key decisions

1. Retire Edit: lower → Storage structure editor; upper → Disk/partition editor.
2. Structure editor: left topology, upper-right structure, lower-right properties.
3. Disk/partition editor: disk controls vs partition controls.
4. One draft, one apply.
5. Topology does not draw empty tiers.
6. Hide the template-tier ritual: spec on the pool, size on the virtual disk.
7. **One virtual disk is the 1.0 contract.** No create-second. Existing extras may be reduced to one. Other cases: Development command line, not 1.0.
8. One user partition; auto-create default on; extra partitions on Disk/partition editor.
9. NTFS and ReFS both supported; default NTFS 64K.
10. `64K + 64K` is a changeable prefill; warn on 256K.
11. Columns, interleave, media type, and disk role are first-class.
12. Write-back cache is not the V10 cache study; advanced, Windows Auto.
13. Apply preview is human-language steps only.
14. Enclosure awareness only when an enclosure is present.
15. Frozen under `docs/Archive/V0.47-standalone-pool-editor/`.

---

## 14. Board decisions (this round)

| Question | Decision |
| --- | --- |
| Second virtual disk | Do not create. Existing extras may be reduced to one. Other needs: Development command line, outside 1.0. |
| Write-back cache | Not in the research article. V10 "cache" is OS/HDD cache. Advanced field, default Auto. |
| Empty tiers | Topology does not show memberless tiers. Adding a tier is a structure action. |
| File system | NTFS and ReFS both required. |
| "Equivalent command summary" | Apply preview does **not** expand PowerShell. Human-language steps only. |
