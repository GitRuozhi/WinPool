using System.Text;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>Only selectors are data. Query graph and all executable text stay fixed in the assembly.</summary>
internal static class ScopedStorageInventoryScript
{
    public static string Create(StorageInventoryScope scope)
    {
        scope.Validate();
        if (scope.Targets.Any(target => scope.BeforeLocators.Count(x => x.Target == target) != 1))
            throw new InvalidDataException("Every scoped target requires one exact provider locator.");
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope.BeforeLocators)));
        var source = EmbeddedStorageInventoryScript.ForPurpose(CollectionPurpose.Storage, discoverProviderCache: false);
        source = Replace(source, "$scannedAt = [DateTimeOffset]::Now", "$scannedAt = [DateTimeOffset]::Now\n"
            + "$WinPoolScopeLocators = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + Closure);
        foreach (var (variable, className, query) in new[]
        {
            ("subsystemObjects", "MSFT_StorageSubSystem", "Get-StorageSubSystem"),
            ("physicalObjects", "MSFT_PhysicalDisk", "Get-PhysicalDisk"),
            ("poolObjects", "MSFT_StoragePool", "Get-StoragePool"),
            ("diskObjects", "MSFT_Disk", "Get-Disk"),
            ("partitionObjects", "MSFT_Partition", "Get-Partition"),
            ("volumeObjects", "MSFT_Volume", "Get-Volume"),
            ("allVirtualDiskObjects", "MSFT_VirtualDisk", "Get-VirtualDisk"),
            ("allTierObjects", "MSFT_StorageTier", "Get-StorageTier")
        })
            source = Replace(source, "$" + variable + " = @(Get-SourceSet '" + className + "' { " + query + " -ErrorAction Stop })",
                "$" + variable + " = @(Get-SourceSet '" + className + "' { Get-WinPoolScopeClass '" + className + "' })");
        // Win32 supplement queries use numbers only after exact current provider identities were resolved.
        source = Replace(source, "@(Get-CimInstance -ClassName Win32_DiskDrive -ErrorAction Stop)",
            "@($diskObjects | ForEach-Object { Get-CimInstance -ClassName Win32_DiskDrive -Filter ('Index = ' + [uint32]$_.Number) -ErrorAction Stop })");
        // Report only the primordial memberships already returned for exact scoped
        // physical objects. Never enumerate the shared availability pool's siblings.
        source = Replace(source, "$members = @(Get-SourceSet 'MSFT_PhysicalDisk' { Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop })",
            "$members = @(Get-SourceSet 'MSFT_PhysicalDisk' { if ($pool.IsPrimordial) { Get-WinPoolScopePrimordialMembers $pool } else { Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop } })");
        ReadOnlyStorageCommandPolicy.EnsureSafe(source);
        return source;
    }

    private static string Replace(string source, string anchor, string replacement)
    {
        if (!source.Contains(anchor, StringComparison.Ordinal))
            throw new InvalidDataException("The fixed scoped inventory anchor was not found: " + anchor);
        return source.Replace(anchor, replacement, StringComparison.Ordinal);
    }

    internal const string Closure = """
$WinPoolScopeNamespace = 'root/Microsoft/Windows/Storage'
$WinPoolScopeObjects = @{}
$WinPoolScopePrimordialMembers = @{}
$WinPoolScopePending = [System.Collections.Generic.Queue[object]]::new()
$WinPoolScopeFailed = $false
$WinPoolScopeClasses = @('MSFT_StorageSubSystem','MSFT_StoragePool','MSFT_PhysicalDisk','MSFT_VirtualDisk','MSFT_StorageTier','MSFT_Disk','MSFT_Partition','MSFT_Volume')
function Get-WinPoolScopeKey($item) {
    if ($null -eq $item -or $null -eq $item.CimClass) { throw 'scope-non-cim-object' }
    $class = [string]$item.CimClass.CimClassName
    if ($WinPoolScopeClasses -notcontains $class -or
        -not [string]::Equals(([string]$item.CimSystemProperties.Namespace).Replace('\','/'), $WinPoolScopeNamespace, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$item.CimSystemProperties.ServerName, [Environment]::MachineName, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'scope-provider-not-exact-local'
    }
    $identity = if ($class -eq 'MSFT_Partition' -and -not [string]::IsNullOrWhiteSpace([string]$item.Guid)) { [string]$item.Guid }
        elseif (-not [string]::IsNullOrWhiteSpace([string]$item.UniqueId)) { [string]$item.UniqueId }
        else { [string]$item.ObjectId }
    if ([string]::IsNullOrWhiteSpace($identity) -or [string]::IsNullOrWhiteSpace([string]$item.ObjectId)) { throw 'scope-provider-identity-unavailable' }
    return $class + '|' + $identity
}
function Add-WinPoolScopeObject($item) {
    $key = Get-WinPoolScopeKey $item
    if ([string]$item.CimClass.CimClassName -eq 'MSFT_StoragePool' -and $item.IsPrimordial) {
        # Retain the observed parent identity without queueing the shared container.
        # It can report this target's membership, never a complete sibling list.
        if ($WinPoolScopeObjects.ContainsKey($key) -and
            -not [string]::Equals([string]$WinPoolScopeObjects[$key].ObjectId, [string]$item.ObjectId, [StringComparison]::Ordinal)) {
            throw 'scope-provider-identity-ambiguous'
        }
        $WinPoolScopeObjects[$key] = $item
        $parents = @(Get-CimAssociatedInstance -InputObject $item -ResultClassName MSFT_StorageSubSystem -ErrorAction Stop)
        if ($parents.Count -ne 1) { throw 'scope-primordial-subsystem-parent-unavailable' }
        foreach ($parent in $parents) { Add-WinPoolScopeObject $parent }
        return
    }
    if ($WinPoolScopeObjects.ContainsKey($key)) {
        if (-not [string]::Equals([string]$WinPoolScopeObjects[$key].ObjectId, [string]$item.ObjectId, [StringComparison]::Ordinal)) {
            throw 'scope-provider-identity-ambiguous'
        }
        return
    }
    $WinPoolScopeObjects[$key] = $item
    $WinPoolScopePending.Enqueue($item)
}
function Get-WinPoolScopePrimordialMembers($pool) {
    $key = Get-WinPoolScopeKey $pool
    if (-not $pool.IsPrimordial) { throw 'scope-not-primordial-pool' }
    if ($WinPoolScopePrimordialMembers.ContainsKey($key)) { return @($WinPoolScopePrimordialMembers[$key].Values) }
    return @()
}
function Find-WinPoolScopeIdentity([string]$class, [string]$property, [string]$value) {
    if ($WinPoolScopeClasses -notcontains $class -or @('UniqueId','ObjectId','Guid') -notcontains $property -or [string]::IsNullOrWhiteSpace($value)) {
        throw 'scope-invalid-selector'
    }
    # WQL escaping is independent from PowerShell parsing: selector text is decoded JSON data.
    $escaped = $value.Replace('\','\\').Replace("'","\'")
    $matches = @(Get-CimInstance -Namespace $WinPoolScopeNamespace -ClassName $class -Filter ($property + " = '" + $escaped + "'") -ErrorAction Stop)
    if ($matches.Count -gt 1) { throw 'scope-selector-not-unique' }
    foreach ($match in $matches) {
        if (-not [string]::Equals([string]$match.$property, $value, [StringComparison]::Ordinal)) { throw 'scope-selector-not-exact' }
        Add-WinPoolScopeObject $match
    }
}
try {
    foreach ($locator in @($WinPoolScopeLocators)) { Find-WinPoolScopeIdentity $locator.ClassName $locator.IdentityProperty $locator.IdentityValue }
    while ($WinPoolScopePending.Count -gt 0) {
        $item = $WinPoolScopePending.Dequeue()
        $class = [string]$item.CimClass.CimClassName
        $related = switch ($class) {
            'MSFT_PhysicalDisk' { @('MSFT_StorageSubSystem','MSFT_StoragePool','MSFT_Disk') }
            'MSFT_StoragePool' { @('MSFT_PhysicalDisk','MSFT_VirtualDisk','MSFT_StorageTier','MSFT_StorageSubSystem') }
            'MSFT_VirtualDisk' { @('MSFT_StoragePool','MSFT_PhysicalDisk','MSFT_StorageTier','MSFT_Disk') }
            'MSFT_StorageTier' { @('MSFT_StoragePool','MSFT_VirtualDisk') }
            'MSFT_Disk' { @('MSFT_PhysicalDisk','MSFT_VirtualDisk','MSFT_Partition') }
            'MSFT_Partition' { @('MSFT_Disk','MSFT_Volume') }
            'MSFT_Volume' { @('MSFT_Partition') }
            default { @() }
        }
        $associations = @{}
        foreach ($resultClass in $related) {
            $associations[$resultClass] = @(Get-CimAssociatedInstance -InputObject $item -ResultClassName $resultClass -ErrorAction Stop)
            foreach ($associated in $associations[$resultClass]) {
                Add-WinPoolScopeObject $associated
            }
        }
        if ($class -eq 'MSFT_PhysicalDisk') {
            $primordialParents = @($associations['MSFT_StoragePool'] | Where-Object { $_.IsPrimordial })
            if ($primordialParents.Count -gt 1) { throw 'scope-physical-primordial-parent-ambiguous' }
            if (@($associations['MSFT_StoragePool'] | Where-Object { -not $_.IsPrimordial }).Count -eq 0) {
                foreach ($parent in $primordialParents) {
                    $parentKey = Get-WinPoolScopeKey $parent
                    if (-not $WinPoolScopePrimordialMembers.ContainsKey($parentKey)) { $WinPoolScopePrimordialMembers[$parentKey] = @{} }
                    $WinPoolScopePrimordialMembers[$parentKey][(Get-WinPoolScopeKey $item)] = $item
                }
            }
        }
        # A successful empty parent query is not positive proof of a usable storage component.
        # Mandatory provider parent/member relationships must be present and unique.
        if ($class -eq 'MSFT_VirtualDisk' -and
            @($associations['MSFT_StoragePool'] | Where-Object { -not $_.IsPrimordial }).Count -ne 1) {
            throw 'scope-concrete-pool-parent-unavailable'
        }
        if ($class -eq 'MSFT_StorageTier') {
            # Templates have a direct pool parent; an attached tier may expose only its VD parent.
            # Resolve that exact VD's pool, never infer a parent from names or a pool-wide scan.
            $tierPools = @($associations['MSFT_StoragePool'] | Where-Object { -not $_.IsPrimordial })
            $tierVirtualDisks = @($associations['MSFT_VirtualDisk'])
            if ($tierPools.Count -gt 1 -or $tierVirtualDisks.Count -gt 1) { throw 'scope-tier-pool-parent-unavailable' }
            foreach ($tierVirtualDisk in $tierVirtualDisks) {
                $virtualDiskPools = @(Get-CimAssociatedInstance -InputObject $tierVirtualDisk -ResultClassName MSFT_StoragePool -ErrorAction Stop |
                    Where-Object { -not $_.IsPrimordial })
                if ($virtualDiskPools.Count -ne 1) { throw 'scope-tier-vdisk-pool-parent-unavailable' }
                foreach ($parent in $virtualDiskPools) { Add-WinPoolScopeObject $parent }
                $tierPools += $virtualDiskPools
            }
            $tierPoolKeys = @($tierPools | ForEach-Object { Get-WinPoolScopeKey $_ } | Select-Object -Unique)
            if ($tierPoolKeys.Count -ne 1) { throw 'scope-tier-pool-parent-unavailable' }
        }
        if ($class -eq 'MSFT_StoragePool' -and @($associations['MSFT_PhysicalDisk']).Count -eq 0) { throw 'scope-pool-members-unavailable' }
        if ($class -eq 'MSFT_StoragePool' -and @($associations['MSFT_StorageSubSystem']).Count -ne 1) { throw 'scope-pool-subsystem-parent-unavailable' }
        if ($class -eq 'MSFT_Partition' -and @($associations['MSFT_Disk']).Count -ne 1) { throw 'scope-partition-parent-unavailable' }
        if ($class -eq 'MSFT_Volume' -and @($associations['MSFT_Partition']).Count -ne 1) { throw 'scope-volume-parent-unavailable' }
        # Some providers have no physical -> OS disk CIM association. Match the current provider UID,
        # never PhysicalDisk.DeviceId to Disk.Number (different number spaces for Spaces disks).
        if ($class -eq 'MSFT_PhysicalDisk' -and -not [string]::IsNullOrWhiteSpace([string]$item.UniqueId)) {
            Find-WinPoolScopeIdentity 'MSFT_Disk' 'UniqueId' ([string]$item.UniqueId)
            $currentPoolCount = @($associations['MSFT_StoragePool'] | Where-Object { -not $_.IsPrimordial }).Count
            $currentDiskCount = @($WinPoolScopeObjects.Values | Where-Object {
                [string]$_.CimClass.CimClassName -eq 'MSFT_Disk' -and
                [string]::Equals([string]$_.UniqueId, [string]$item.UniqueId, [StringComparison]::Ordinal)
            }).Count
            if ($currentPoolCount -eq 0 -and @($associations['MSFT_Disk']).Count -eq 0 -and $currentDiskCount -ne 1) {
                throw 'scope-physical-disk-parent-and-role-evidence-unavailable'
            }
        }
    }
    # Concrete pools are queued before their subsystem parent is visited. Validate only
    # after the closure is fully expanded, including either direct or exact pool parents.
    $physical = @($WinPoolScopeObjects.Values | Where-Object { [string]$_.CimClass.CimClassName -eq 'MSFT_PhysicalDisk' })
    $subsystems = @($WinPoolScopeObjects.Values | Where-Object { [string]$_.CimClass.CimClassName -eq 'MSFT_StorageSubSystem' })
    if ($physical.Count -gt 0 -and $subsystems.Count -ne 1) { throw 'scope-physical-subsystem-parent-unavailable' }
} catch {
    $WinPoolScopeFailed = $true
    foreach ($class in $WinPoolScopeClasses) {
        [void]$sourceQueryFailures.Add([ordered]@{ ClassName=$class; Namespace=$WinPoolScopeNamespace; ReasonCode=('ScopeClosureIncomplete:' + [string]$_.Exception.Message) })
    }
}
function Get-WinPoolScopeClass([string]$class) {
    return @($WinPoolScopeObjects.Values | Where-Object { [string]$_.CimClass.CimClassName -eq $class })
}

""";
}
