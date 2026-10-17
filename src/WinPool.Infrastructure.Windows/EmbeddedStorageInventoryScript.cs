using System.Text;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>One fixed read-only collector. Purpose, discovery and exact selectors are data, never executable input.</summary>
internal static class EmbeddedStorageInventoryScript
{
    public static string Source => ForPurpose(CollectionPurpose.Hardware);

    public static string ForPurpose(CollectionPurpose purpose, bool discoverProviderCache = false) =>
        Create(purpose, null, discoverProviderCache);

    public static string ForScope(StorageInventoryScope scope, bool discoverProviderCache = false)
    {
        scope.Validate();
        return Create(CollectionPurpose.Storage, scope, discoverProviderCache);
    }

    private static string Create(CollectionPurpose purpose, StorageInventoryScope? scope, bool discoverProviderCache)
    {
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        var selectors = scope is null ? "$null" :
            "([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope.BeforeLocators)))
            + "')) | ConvertFrom-Json)";
        return "$WinPoolIncludeHardware = " + (purpose == CollectionPurpose.Hardware ? "$true" : "$false")
            + "\n$WinPoolDiscoverProviderCache = " + (discoverProviderCache ? "$true" : "$false")
            + "\n$WinPoolScopeLocators = " + selectors + "\n" + Template;
    }

    private static readonly string Template = Prelude + ScopeClosure + StorageClassQueries + ProviderDiscovery + StorageReads + TierMemberAssociationHelpers + InventoryBody + OsDiskParentBinding + ReportBody;

    private const string Prelude = """
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)


$sourceObservations = [System.Collections.Generic.List[object]]::new()
$sourceQueryFailures = [System.Collections.Generic.List[object]]::new()
$sourceQuerySuccesses = [System.Collections.Generic.List[object]]::new()
$sourceSeen = @{}
function Add-SourceObservation {
    param($Item)
    if ($null -eq $Item -or $null -eq $Item.CimClass) { return }
    $className = [string]$Item.CimClass.CimClassName
    $namespace = [string]$Item.CimSystemProperties.Namespace
    $identity = ''
    foreach ($name in @('UniqueId','ObjectId','Guid','DeviceID','Tag','InstanceName')) {
        $property = $Item.CimInstanceProperties[$name]
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            $identity = $name + ':' + [string]$property.Value
            break
        }
    }
    $key = $namespace + '|' + $className + '|' + $identity
    if ($identity -ne '' -and $sourceSeen.ContainsKey($key)) { return }
    if ($identity -ne '') { $sourceSeen[$key] = $true }
    $fields = foreach ($property in $Item.CimInstanceProperties) {
        $type = [string]$property.CimType
        $state = 'Returned'
        $reason = $null
        $value = $property.Value
        if ($type -match 'Instance|Reference') {
            $state = 'Unavailable'
            $reason = 'StructuredCimValueNotSupported'
            $value = $null
        }
        if ($type -eq 'DateTime' -and $null -ne $value) { $value = ([DateTimeOffset]$value).ToString('o') }
        [ordered]@{ Name=[string]$property.Name; CimType=$type; Value=$value; ReadState=$state; ReasonCode=$reason }
    }
    [void]$sourceObservations.Add([ordered]@{
        ClassName=$className; Namespace=$namespace; Identity=$identity; Fields=@($fields)
    })
}


function Add-NativeObservation {
    param([string]$ClassName, [string]$Identity, $Values)
    $fields = foreach ($name in $Values.Keys) {
        $value = $Values[$name]
        $type = if ($value -is [bool]) { 'Boolean' } elseif ($value -is [uint64]) { 'UInt64' } elseif ($value -is [int] -or $value -is [long]) { 'SInt64' } elseif ($value -is [decimal] -or $value -is [double]) { 'Real64' } else { 'String' }
        [ordered]@{ Name=$name; CimType=$type; Value=$value; ReadState=if ($null -eq $value) { 'Unavailable' } else { 'Returned' }; ReasonCode=if ($null -eq $value) { 'NativeValueUnavailable' } else { $null } }
    }
    [void]$sourceObservations.Add([ordered]@{ ClassName=$ClassName; Namespace='WinPool/Native'; Identity=$Identity; Fields=@($fields) })
    [void]$sourceQuerySuccesses.Add([ordered]@{ ClassName=$ClassName; Namespace='WinPool/Native' })
}

function Get-AssociationKey {
    param($Item, [string]$Fallback)
    if ($null -ne $Item -and -not [string]::IsNullOrWhiteSpace([string]$Item.UniqueId)) {
        return "uid:$([string]$Item.UniqueId)"
    }
    if ($null -ne $Item -and -not [string]::IsNullOrWhiteSpace([string]$Item.ObjectId)) {
        return "oid:$([string]$Item.ObjectId)"
    }
    return "fallback:$Fallback"
}

function Get-ScalarText {
    param($Value)
    if ($null -eq $Value) { return '' }
    return (@($Value) | ForEach-Object { [string]$_ }) -join ', '
}

function Get-Safe {
    param([scriptblock]$Query, $Default = $null)
    $classMatch = [regex]::Match($Query.ToString(), '(?:Win32|MSFT)_[A-Za-z0-9_]+|WmiMonitorID|BatteryStaticData|BatteryStatus|SoftwareLicensingProduct')
    $queryNamespace = if ($Query.ToString() -match 'root\\wmi') { 'root/wmi' } else { 'root/cimv2' }
    try {
        $value = & $Query
        if ($classMatch.Success) {
            [void]$sourceQuerySuccesses.Add([ordered]@{ ClassName=$classMatch.Value; Namespace=$queryNamespace })
        }
        foreach ($observed in @($value)) { Add-SourceObservation $observed }
        if ($null -eq $value) { return $Default }
        return $value
    } catch {
        if ($classMatch.Success) {
            [void]$sourceQueryFailures.Add([ordered]@{ ClassName=$classMatch.Value; Namespace=$queryNamespace; ReasonCode=[string]$_.FullyQualifiedErrorId })
        }
        return $Default
    }
}

function Get-SafeList {
    param([scriptblock]$Query)
    $value = Get-Safe $Query
    if ($null -eq $value) { return @() }
    return @($value | Where-Object { $null -ne $_ })
}

function ConvertFrom-U16 {
    param($Value)
    if ($null -eq $Value) { return '' }
    return ((-join (@($Value) | ForEach-Object { if ([int]$_ -gt 0) { [char][int]$_ } }))).Trim()
}

function Get-SourceSet {
    param([string]$ClassName, [scriptblock]$Query, [string]$Namespace = 'root/Microsoft/Windows/Storage')
    try {
        $result = @(& $Query)
        [void]$sourceQuerySuccesses.Add([ordered]@{ ClassName=$ClassName; Namespace=$Namespace })
        foreach ($observed in $result) { Add-SourceObservation $observed }
        return $result
    } catch {
        [void]$sourceQueryFailures.Add([ordered]@{ ClassName=$ClassName; Namespace=$Namespace; ReasonCode=[string]$_.FullyQualifiedErrorId })
        return @()
    }
}

$scannedAt = [DateTimeOffset]::Now

""";

    internal const string ScopeClosure = """
$WinPoolScopeNamespace = 'root/Microsoft/Windows/Storage'
$WinPoolScopeObjects = @{}
$WinPoolScopePrimordialMembers = @{}
$WinPoolScopePending = [System.Collections.Generic.Queue[object]]::new()
$WinPoolScopeState = @{ Failed=$false }
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
function Initialize-WinPoolScope {
if ($null -eq $WinPoolScopeLocators) { return }
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
    $WinPoolScopeState.Failed = $true
    foreach ($class in $WinPoolScopeClasses) {
        [void]$sourceQueryFailures.Add([ordered]@{ ClassName=$class; Namespace=$WinPoolScopeNamespace; ReasonCode=('ScopeClosureIncomplete:' + [string]$_.Exception.Message) })
    }
}
}
function Get-WinPoolScopeClass([string]$class) {
    return @($WinPoolScopeObjects.Values | Where-Object { [string]$_.CimClass.CimClassName -eq $class })
}


""";

    private const string StorageClassQueries = """
function Get-WinPoolStorageClass([string]$class) {
    if ($null -ne $WinPoolScopeLocators) { return Get-WinPoolScopeClass $class }
    switch ($class) {
        'MSFT_StorageSubSystem' { Get-StorageSubSystem -ErrorAction Stop; break }
        'MSFT_PhysicalDisk' { Get-PhysicalDisk -ErrorAction Stop; break }
        'MSFT_StoragePool' { Get-StoragePool -ErrorAction Stop; break }
        'MSFT_Disk' { Get-Disk -ErrorAction Stop; break }
        'MSFT_Partition' { Get-Partition -ErrorAction Stop; break }
        'MSFT_Volume' { Get-Volume -ErrorAction Stop; break }
        'MSFT_VirtualDisk' { Get-VirtualDisk -ErrorAction Stop; break }
        'MSFT_StorageTier' { Get-StorageTier -ErrorAction Stop; break }
        default { throw 'inventory-class-not-supported' }
    }
}

$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
$computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem
$subsystemObjects = @(if ($WinPoolDiscoverProviderCache -or $null -eq $WinPoolScopeLocators) {
    Get-SourceSet 'MSFT_StorageSubSystem' { Get-StorageSubSystem -ErrorAction Stop }
})

""";



    internal const string ProviderDiscovery = """
if ($WinPoolDiscoverProviderCache) {
    try {
        if ($subsystemObjects.Count -ne 1) { throw 'subsystem-not-unique' }
        $subsystem = $subsystemObjects[0]
        if (-not [string]::Equals([string]$subsystem.CimClass.CimClassName, 'MSFT_StorageSubSystem', [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals(([string]$subsystem.CimSystemProperties.Namespace).Replace('\','/'), 'root/Microsoft/Windows/Storage', [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$subsystem.CimSystemProperties.ServerName, [Environment]::MachineName, [StringComparison]::OrdinalIgnoreCase) -or
            [string]::IsNullOrWhiteSpace([string]$subsystem.UniqueId) -or
            [string]::IsNullOrWhiteSpace([string]$subsystem.ObjectId)) {
            throw 'subsystem-identity-not-exact-local'
        }
        # Service-cache discovery only; Level3 includes physical disks.
        # https://learn.microsoft.com/powershell/module/storage/update-storageprovidercache
        Update-StorageProviderCache -StorageSubSystem $subsystem -DiscoveryLevel Level3 -ErrorAction Stop | Out-Null
    } catch {
        [void]$sourceQueryFailures.Add([ordered]@{ ClassName='Windows.StorageProviderDiscovery'; Namespace='WinPool/Native'; ReasonCode=('StorageProviderCacheDiscoveryFailed:' + [string]$_.Exception.Message) })
    }
}

""";

    private const string StorageReads = """
Initialize-WinPoolScope
if ($null -ne $WinPoolScopeLocators) {
    $subsystemObjects = @(Get-SourceSet 'MSFT_StorageSubSystem' { Get-WinPoolScopeClass 'MSFT_StorageSubSystem' })
}
$physicalObjects = @(Get-SourceSet 'MSFT_PhysicalDisk' { Get-WinPoolStorageClass 'MSFT_PhysicalDisk' })
$poolObjects = @(Get-SourceSet 'MSFT_StoragePool' { Get-WinPoolStorageClass 'MSFT_StoragePool' })
$nonPrimordialPoolObjects = @($poolObjects | Where-Object { -not $_.IsPrimordial })
$diskObjects = @(Get-SourceSet 'MSFT_Disk' { Get-WinPoolStorageClass 'MSFT_Disk' })
$partitionObjects = @(Get-SourceSet 'MSFT_Partition' { Get-WinPoolStorageClass 'MSFT_Partition' })
$volumeObjects = @(Get-SourceSet 'MSFT_Volume' { Get-WinPoolStorageClass 'MSFT_Volume' })
$logicalDiskObjects = @(Get-SourceSet 'Win32_LogicalDisk' { Get-CimInstance -ClassName Win32_LogicalDisk -ErrorAction Stop } 'root/cimv2')
$networkDiskObjects = @($logicalDiskObjects | Where-Object { $_.DriveType -eq 4 })
foreach ($className in @('Win32_OperatingSystem','Win32_ComputerSystem')) {
    [void]$sourceQuerySuccesses.Add([ordered]@{ ClassName=$className; Namespace='root/cimv2' })
}
# Standalone collections retain tiers or virtual disks even if their associations cannot be read.
$allVirtualDiskObjects = @(Get-SourceSet 'MSFT_VirtualDisk' { Get-WinPoolStorageClass 'MSFT_VirtualDisk' })
$allTierObjects = @(Get-SourceSet 'MSFT_StorageTier' { Get-WinPoolStorageClass 'MSFT_StorageTier' })


foreach ($observed in @($operatingSystem,$computerSystem) + $subsystemObjects + $physicalObjects + $poolObjects + $diskObjects + $partitionObjects + $volumeObjects + $networkDiskObjects) {
    Add-SourceObservation $observed
}

$subsystems = foreach ($subsystem in $subsystemObjects) {
    [ordered]@{
        AssociationKey = Get-AssociationKey $subsystem ([string]$subsystem.FriendlyName)
        UniqueId = [string]$subsystem.UniqueId
        ObjectId = [string]$subsystem.ObjectId
        FriendlyName = [string]$subsystem.FriendlyName
        HealthStatus = Get-ScalarText $subsystem.HealthStatus
        OperationalStatus = Get-ScalarText $subsystem.OperationalStatus
    }
}

$poolMembership = @{}
$poolMembersByKey = @{}
foreach ($pool in @($nonPrimordialPoolObjects) + @($poolObjects | Where-Object { $_.IsPrimordial })) {
    $poolKey = Get-AssociationKey $pool ([string]$pool.FriendlyName)
    $members = @(Get-SourceSet 'MSFT_PhysicalDisk' { if ($null -ne $WinPoolScopeLocators -and $pool.IsPrimordial) { Get-WinPoolScopePrimordialMembers $pool } else { Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop } })
    $memberKeys = foreach ($member in $members) {
        $memberKey = Get-AssociationKey $member "$($member.DeviceId)|$($member.FriendlyName)|$($member.Size)"
        if (-not $pool.IsPrimordial -or -not $poolMembership.ContainsKey($memberKey)) {
            $poolMembership[$memberKey] = $poolKey
            $memberKey
        }
    }
    $poolMembersByKey[$poolKey] = @($memberKeys)
}



""";



    internal const string TierMemberAssociationHelpers = """
function Test-EqualPositiveUInt64([object[]]$Values) {
    if ($null -eq $Values -or $Values.Count -lt 2) { return $false }
    $first = $Values[0]
    if ($first -isnot [uint64] -or $first -eq 0) { return $false }
    foreach ($value in $Values) {
        if ($value -isnot [uint64] -or $value -ne $first) { return $false }
    }
    return $true
}

function Test-ExactLayoutValue($Item, [string]$Name, [uint64]$Expected) {
    $value = $Item.$Name
    if ($null -eq $value -or
        ($value -isnot [byte] -and $value -isnot [sbyte] -and
         $value -isnot [int16] -and $value -isnot [uint16] -and
         $value -isnot [int32] -and $value -isnot [uint32] -and
         $value -isnot [int64] -and $value -isnot [uint64])) { return $false }
    try { return ([decimal]$value -eq [decimal]$Expected) } catch { return $false }
}

function Test-ExactNullableLayoutString($Item, [string]$Name, [string]$Expected) {
    $property = $Item.PSObject.Properties[$Name]
    if ($null -eq $property) { return $false }
    $value = $property.Value
    if ($null -eq $value) { return $true }
    return $value -is [string] -and
        [string]::Equals([string]$value, $Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Test-ExactNullableLayoutValue($Item, [string]$Name, [uint64]$Expected) {
    $property = $Item.PSObject.Properties[$Name]
    if ($null -eq $property) { return $false }
    if ($null -eq $property.Value) { return $true }
    return Test-ExactLayoutValue $Item $Name $Expected
}

function Test-ExactNullableProvisioningType($Item) {
    $property = $Item.PSObject.Properties['ProvisioningType']
    if ($null -eq $property) { return $false }
    $value = $property.Value
    if ($null -eq $value) { return $true }
    if ($value -is [string]) {
        return [string]::Equals($value, 'Fixed', [StringComparison]::OrdinalIgnoreCase)
    }
    return Test-ExactLayoutValue $Item 'ProvisioningType' 2
}

function Test-SupportedSingleHddTierLayout($tier, $virtual) {
    if (-not [string]::Equals([string]$tier.MediaType, 'HDD', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$tier.ResiliencySettingName, 'Simple', [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([string]$tier.ProvisioningType, 'Fixed', [StringComparison]::OrdinalIgnoreCase) -or
        $virtual.WriteCacheSize -isnot [uint64] -or $virtual.WriteCacheSize -ne 0) { return $false }
    return (Test-ExactNullableLayoutString $virtual 'ResiliencySettingName' 'Simple') -and
        (Test-ExactNullableProvisioningType $virtual) -and
        (Test-ExactNullableLayoutValue $virtual 'NumberOfColumns' 1) -and
        (Test-ExactNullableLayoutValue $virtual 'Interleave' ([uint64]$tier.Interleave)) -and
        (Test-ExactNullableLayoutValue $virtual 'NumberOfDataCopies' 1) -and
        (Test-ExactNullableLayoutValue $virtual 'PhysicalDiskRedundancy' 0) -and
        (Test-ExactLayoutValue $tier 'NumberOfColumns' 1) -and
        ($tier.Interleave -is [uint64] -and $tier.Interleave -in @(16384,32768,65536,131072,262144)) -and
        (Test-ExactLayoutValue $tier 'NumberOfDataCopies' 1) -and
        (Test-ExactLayoutValue $tier 'PhysicalDiskRedundancy' 0)
}

function Get-ExactSingleHddTierMemberKey($tier, $virtual, $pool, $virtualTiers) {
    try {
        $tierUniqueId = [string]$tier.UniqueId
        $tierObjectId = [string]$tier.ObjectId
        $virtualUniqueId = [string]$virtual.UniqueId
        $virtualObjectId = [string]$virtual.ObjectId
        $poolUniqueId = [string]$pool.UniqueId
        $poolObjectId = [string]$pool.ObjectId
        if ([string]::IsNullOrWhiteSpace($tierUniqueId) -or [string]::IsNullOrWhiteSpace($tierObjectId) -or
            [string]::IsNullOrWhiteSpace($virtualUniqueId) -or [string]::IsNullOrWhiteSpace($virtualObjectId) -or
            [string]::IsNullOrWhiteSpace($poolUniqueId) -or [string]::IsNullOrWhiteSpace($poolObjectId) -or
            $pool.IsPrimordial -isnot [bool] -or $pool.IsPrimordial) { return $null }

        $actualTiers = @($virtualTiers)
        if ($actualTiers.Count -ne 1 -or
            -not [string]::Equals([string]$actualTiers[0].UniqueId, $tierUniqueId, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$actualTiers[0].ObjectId, $tierObjectId, [StringComparison]::Ordinal)) { return $null }

        $parents = @(Get-StoragePool -VirtualDisk $virtual -ErrorAction Stop)
        if ($parents.Count -ne 1 -or $parents[0].IsPrimordial -isnot [bool] -or $parents[0].IsPrimordial -or
            -not [string]::Equals([string]$parents[0].UniqueId, $poolUniqueId, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$parents[0].ObjectId, $poolObjectId, [StringComparison]::Ordinal)) { return $null }

        $poolMembers = @(Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop)
        if ($poolMembers.Count -ne 1) { return $null }
        $member = $poolMembers[0]
        $memberUniqueId = [string]$member.UniqueId
        $memberObjectId = [string]$member.ObjectId
        if ([string]::IsNullOrWhiteSpace($memberUniqueId) -or [string]::IsNullOrWhiteSpace($memberObjectId) -or
            -not [string]::Equals([string]$member.MediaType, 'HDD', [StringComparison]::OrdinalIgnoreCase)) { return $null }

        $allocatedMembers = @(Get-PhysicalDisk -VirtualDisk $virtual -HasAllocations $true -ErrorAction Stop)
        if ($allocatedMembers.Count -ne 1 -or
            -not [string]::Equals([string]$allocatedMembers[0].UniqueId, $memberUniqueId, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$allocatedMembers[0].ObjectId, $memberObjectId, [StringComparison]::Ordinal)) { return $null }

        $physicalMatches = @($physicalObjects | Where-Object {
            [string]::Equals([string]$_.UniqueId, $memberUniqueId, [StringComparison]::Ordinal) -and
            [string]::Equals([string]$_.ObjectId, $memberObjectId, [StringComparison]::Ordinal) -and
            [string]::Equals([string]$_.MediaType, 'HDD', [StringComparison]::OrdinalIgnoreCase)
        })
        if ($physicalMatches.Count -ne 1 -or -not (Test-SupportedSingleHddTierLayout $tier $virtual)) { return $null }
        if (-not (Test-EqualPositiveUInt64 @(
            $tier.Size, $tier.AllocatedSize, $tier.FootprintOnPool,
            $virtual.Size, $virtual.AllocatedSize, $virtual.FootprintOnPool))) { return $null }

        $key = [string](Get-AssociationKey $physicalMatches[0] '')
        if ([string]::IsNullOrWhiteSpace($key) -or $key.StartsWith('fallback:', [StringComparison]::Ordinal)) { return $null }
        return $key
    } catch {
        return $null
    }
}

function Get-ExactTierMemberKeysByExtent($tier, $virtual) {
    try {
    if (@($physicalObjects).Count -eq 0 -or @($physicalObjects | Where-Object {
        [string]::IsNullOrWhiteSpace([string]$_.UniqueId) -or [string]::IsNullOrWhiteSpace([string]$_.ObjectId)
    }).Count -gt 0) { throw 'tier-extent-physical-facts-unavailable' }
    $result = Invoke-CimMethod -InputObject $tier -MethodName GetPhysicalExtent -ErrorAction Stop
    if ($null -eq $result.ReturnValue -or [uint32]$result.ReturnValue -ne 0) { throw 'tier-extent-method-failed' }
    $keys = @()
    foreach ($extent in @($result.PhysicalExtents)) {
        if ($null -eq $extent -or
            -not [string]::Equals([string]$extent.StorageTierUniqueId, [string]$tier.UniqueId, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$extent.VirtualDiskUniqueId, [string]$virtual.UniqueId, [StringComparison]::Ordinal)) {
            throw 'tier-extent-owner-not-exact'
        }
        $matches = @($physicalObjects | Where-Object {
            [string]::Equals([string]$_.UniqueId, [string]$extent.PhysicalDiskUniqueId, [StringComparison]::Ordinal)
        })
        if ($matches.Count -ne 1) { throw 'tier-extent-physical-not-unique' }
        $keys += Get-AssociationKey $matches[0] ''
    }
    return @($keys | Select-Object -Unique)
    } catch {
        [void]$sourceQueryFailures.Add([ordered]@{
            ClassName = 'MSFT_StorageTier'; Namespace = 'root/Microsoft/Windows/Storage';
            ReasonCode = 'ExactTierExtentFailed:' + [string]$_.Exception.Message
        })
        throw
    }
}

function Get-ExactTierMemberKeys($tier, $virtual, $pool, $virtualTiers) {
    $fastMemberKey = Get-ExactSingleHddTierMemberKey $tier $virtual $pool $virtualTiers
    if ($null -ne $fastMemberKey) { return $fastMemberKey }
    return Get-ExactTierMemberKeysByExtent $tier $virtual
}
""";

    private const string InventoryBody = """

$virtualDiskKeyByOsDisk = @{}
$virtualDisks = @()
$storageTiers = @()
$tierKeysSeen = @{}
foreach ($pool in $nonPrimordialPoolObjects) {
    $poolKey = Get-AssociationKey $pool ([string]$pool.FriendlyName)
    foreach ($virtual in @(Get-SourceSet 'MSFT_VirtualDisk' { Get-VirtualDisk -StoragePool $pool -ErrorAction Stop })) {
        Add-SourceObservation $virtual
        $virtualKey = Get-AssociationKey $virtual "$($virtual.FriendlyName)|$($virtual.Size)"
        $osDiskNumbers = @()
        foreach ($mappedDisk in @(Get-SourceSet 'MSFT_Disk' { Get-Disk -VirtualDisk $virtual -ErrorAction Stop })) {
            $exactDisks = @($diskObjects | Where-Object {
                [int]$_.Number -eq [int]$mappedDisk.Number -and
                [string]::Equals([string]$_.UniqueId, [string]$mappedDisk.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$mappedDisk.ObjectId, [StringComparison]::Ordinal)
            })
            if ($exactDisks.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$mappedDisk.UniqueId) -or
                [string]::IsNullOrWhiteSpace([string]$mappedDisk.ObjectId) -or
                ($virtualDiskKeyByOsDisk.ContainsKey([int]$mappedDisk.Number) -and
                    $virtualDiskKeyByOsDisk[[int]$mappedDisk.Number] -ne $virtualKey)) {
                [void]$sourceQueryFailures.Add([ordered]@{
                    ClassName='MSFT_Disk'; Namespace='root/Microsoft/Windows/Storage';
                    ReasonCode='VirtualDiskToCurrentOsDiskAssociationNotUnique'
                })
                continue
            }
            $osDiskNumbers += [int]$mappedDisk.Number
            $virtualDiskKeyByOsDisk[[int]$mappedDisk.Number] = $virtualKey
        }

        $tierKeys = @()
        try {
            $virtualTiers = @(Get-SourceSet 'MSFT_StorageTier' { Get-StorageTier -VirtualDisk $virtual -ErrorAction Stop })
            foreach ($tier in $virtualTiers) {
                Add-SourceObservation $tier
                $tierKey = Get-AssociationKey $tier "$($tier.FriendlyName)|$($tier.MediaType)|$($tier.Size)"
                $tierKeys += $tierKey
                if (-not $tierKeysSeen.ContainsKey($tierKey)) {
                    $tierKeysSeen[$tierKey] = $true
                    $storageTiers += [ordered]@{
                        AssociationKey = $tierKey
                        UniqueId = [string]$tier.UniqueId
                        ObjectId = [string]$tier.ObjectId
                        FriendlyName = [string]$tier.FriendlyName
                        MediaType = [string]$tier.MediaType
                        ResiliencySettingName = [string]$tier.ResiliencySettingName
                        Size = [decimal]$tier.Size
                        FootprintOnPool = [decimal]$tier.FootprintOnPool
                        NumberOfColumns = if ($null -eq $tier.NumberOfColumns) { $null } else { [int]$tier.NumberOfColumns }
                        Interleave = if ($null -eq $tier.Interleave) { $null } else { [decimal]$tier.Interleave }
                        NumberOfDataCopies = if ($null -eq $tier.NumberOfDataCopies) { $null } else { [int]$tier.NumberOfDataCopies }
                        PhysicalDiskRedundancy = if ($null -eq $tier.PhysicalDiskRedundancy) { $null } else { [int]$tier.PhysicalDiskRedundancy }
                        PoolAssociationKey = $poolKey
                        VirtualDiskAssociationKey = $virtualKey
                        MemberPhysicalDiskKeys = @(Get-ExactTierMemberKeys $tier $virtual $pool $virtualTiers)
                    }
                }
            }
        }
        catch {
        }

        $virtualDisks += [ordered]@{
            AssociationKey = $virtualKey
            UniqueId = [string]$virtual.UniqueId
            ObjectId = [string]$virtual.ObjectId
            FriendlyName = [string]$virtual.FriendlyName
            HealthStatus = Get-ScalarText $virtual.HealthStatus
            OperationalStatus = Get-ScalarText $virtual.OperationalStatus
            ResiliencySettingName = [string]$virtual.ResiliencySettingName
            ProvisioningType = [string]$virtual.ProvisioningType
            NumberOfDataCopies = if ($null -eq $virtual.NumberOfDataCopies) { $null } else { [int]$virtual.NumberOfDataCopies }
        PhysicalDiskRedundancy = if ($null -eq $virtual.PhysicalDiskRedundancy) { $null } else { [int]$virtual.PhysicalDiskRedundancy }
        AllocatedSize = if ($null -eq $virtual.AllocatedSize) { $null } else { [decimal]$virtual.AllocatedSize }
        NumberOfColumns = if ($null -eq $virtual.NumberOfColumns) { $null } else { [int]$virtual.NumberOfColumns }
            Interleave = if ($null -eq $virtual.Interleave) { $null } else { [decimal]$virtual.Interleave }
            Size = [decimal]$virtual.Size
            FootprintOnPool = [decimal]$virtual.FootprintOnPool
            PoolAssociationKey = $poolKey
            TierAssociationKeys = @($tierKeys)
            OsDiskNumbers = @($osDiskNumbers)
        }
    }
}

$physicalByDeviceNumber = @{}
foreach ($physical in $physicalObjects) {
    $number = 0
    if ([int]::TryParse([string]$physical.DeviceId, [ref]$number)) {
        if (-not $physicalByDeviceNumber.ContainsKey($number)) {
            $physicalByDeviceNumber[$number] = @()
        }
        $physicalByDeviceNumber[$number] += $physical
    }
}

$diskPhysicalMap = @{}
$diskPhysicalMapSource = @{}
foreach ($disk in $diskObjects) {
    $physicalMatch = @()
    $matchSource = 'ProviderUniqueId'
    try {
        $physicalMatch = @($disk | Get-PhysicalDisk -ErrorAction Stop)
    }
    catch {
    }
    if ($physicalMatch.Count -gt 1 -or ($physicalMatch.Count -eq 1 -and
        -not [string]::Equals([string]$physicalMatch[0].UniqueId, [string]$disk.UniqueId, [StringComparison]::Ordinal))) {
        [void]$sourceQueryFailures.Add([ordered]@{
            ClassName='MSFT_Disk'; Namespace='root/Microsoft/Windows/Storage';
            ReasonCode='PhysicalDiskProviderIdentityNotUnique'
        })
        continue
    }
    # Removable USB media can lack this provider association even when its disk number is known.
    if ($physicalMatch.Count -eq 0 -and -not $virtualDiskKeyByOsDisk.ContainsKey([int]$disk.Number) -and
        $physicalByDeviceNumber.ContainsKey([int]$disk.Number)) {
        $candidates = @($physicalByDeviceNumber[[int]$disk.Number])
        if ($candidates.Count -eq 1) {
            $physicalMatch = $candidates
            $matchSource = 'DeviceNumberFallback'
        }
    }
    if ($physicalMatch.Count -eq 1) {
        $exactPhysical = @($physicalObjects | Where-Object {
            [string]::Equals([string]$_.UniqueId, [string]$physicalMatch[0].UniqueId, [StringComparison]::Ordinal) -and
            [string]::Equals([string]$_.ObjectId, [string]$physicalMatch[0].ObjectId, [StringComparison]::Ordinal)
        })
        if ($exactPhysical.Count -ne 1) {
            [void]$sourceQueryFailures.Add([ordered]@{
                ClassName='MSFT_Disk'; Namespace='root/Microsoft/Windows/Storage';
                ReasonCode='PhysicalDiskCurrentIdentityNotUnique'
            })
            continue
        }
        $diskPhysicalMap[[int]$disk.Number] = Get-AssociationKey $physicalMatch[0] "$($physicalMatch[0].DeviceId)|$($physicalMatch[0].FriendlyName)|$($physicalMatch[0].Size)"
        $diskPhysicalMapSource[[int]$disk.Number] = $matchSource
    }
}

# Pool-level tiers have no allocation or Size. Read their exact provider pool
# association and eligible pool members separately from instance allocations.
foreach ($pool in $nonPrimordialPoolObjects) {
    $poolKey = Get-AssociationKey $pool ''
    foreach ($tier in @(Get-SourceSet 'MSFT_StorageTier' { Get-StorageTier -StoragePool $pool -ErrorAction Stop })) {
        $tierKey = Get-AssociationKey $tier ''
        if ($tierKeysSeen.ContainsKey($tierKey)) { continue }
        try {
            $parents = @(Get-StoragePool -StorageTier $tier -ErrorAction Stop | Where-Object { -not $_.IsPrimordial })
            if ($parents.Count -ne 1 -or
                -not [string]::Equals([string]$parents[0].UniqueId, [string]$pool.UniqueId, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$parents[0].ObjectId, [string]$pool.ObjectId, [StringComparison]::Ordinal)) {
                throw 'tier-template-pool-not-exact'
            }
            if (@(Get-VirtualDisk -StorageTier $tier -ErrorAction Stop).Count -ne 0) { throw 'tier-owner-association-incomplete' }
            $members = @(Get-PhysicalDisk -StoragePool $parents[0] -ErrorAction Stop)
            $memberKeys = @()
            foreach ($member in $members) {
                $exact = @($physicalObjects | Where-Object {
                    [string]::Equals([string]$_.UniqueId, [string]$member.UniqueId, [StringComparison]::Ordinal) -and
                    [string]::Equals([string]$_.ObjectId, [string]$member.ObjectId, [StringComparison]::Ordinal)
                })
                if ($exact.Count -ne 1) { throw 'tier-template-pool-member-not-exact' }
                $memberKeys += Get-AssociationKey $exact[0] ''
            }
            $tierKeysSeen[$tierKey] = $true
            $storageTiers += [ordered]@{
                AssociationKey = $tierKey; UniqueId = [string]$tier.UniqueId; ObjectId = [string]$tier.ObjectId;
                FriendlyName = [string]$tier.FriendlyName; MediaType = [string]$tier.MediaType;
                ResiliencySettingName = [string]$tier.ResiliencySettingName;
                Size = $null; FootprintOnPool = $null;
                NumberOfColumns = $tier.NumberOfColumns; Interleave = $tier.Interleave;
                NumberOfDataCopies = $tier.NumberOfDataCopies; PhysicalDiskRedundancy = $tier.PhysicalDiskRedundancy;
                PoolAssociationKey = $poolKey; VirtualDiskAssociationKey = '';
                MemberPhysicalDiskKeys = @(); TemplatePhysicalDiskKeys = @($memberKeys)
            }
        } catch {
            [void]$sourceQueryFailures.Add([ordered]@{
                ClassName = 'MSFT_StorageTier'; Namespace = 'root/Microsoft/Windows/Storage';
                ReasonCode = 'ExactTemplateAssociationFailed:' + [string]$_.Exception.Message
            })
        }
    }
}

$physicalDisks = foreach ($physical in $physicalObjects) {
    $physicalKey = Get-AssociationKey $physical "$($physical.DeviceId)|$($physical.FriendlyName)|$($physical.Size)"
    $matchingDisk = $diskObjects | Where-Object { $diskPhysicalMap[[int]$_.Number] -eq $physicalKey } | Select-Object -First 1
    [ordered]@{
        AssociationKey = $physicalKey
        UniqueId = [string]$physical.UniqueId
        ObjectId = [string]$physical.ObjectId
        FriendlyName = [string]$physical.FriendlyName
        Model = [string]$physical.Model
        SerialNumber = [string]$physical.SerialNumber
        BusType = [string]$physical.BusType
        MediaType = [string]$physical.MediaType
        Size = [decimal]$physical.Size
        LogicalSectorSize = [decimal]$physical.LogicalSectorSize
        PhysicalSectorSize = [decimal]$physical.PhysicalSectorSize
        HealthStatus = Get-ScalarText $physical.HealthStatus
        OperationalStatus = Get-ScalarText $physical.OperationalStatus
        CanPool = [bool]$physical.CanPool
        CannotPoolReason = Get-ScalarText $physical.CannotPoolReason
        DeviceId = if ([string]$physical.DeviceId -match '^\d{1,9}$') { [int]$physical.DeviceId } else { $null }
        DeviceIdentifier = [string]$physical.DeviceId
        FirmwareVersion = [string]$physical.FirmwareVersion
        ProvisioningType = [string]$physical.ProvisioningType
        PhysicalLocation = [string]$physical.PhysicalLocation
        IsBoot = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsBoot }
        IsSystem = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsSystem }
        IsPageFile = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsPageFile }
        IsCrashDump = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsCrashDump }
        Usage = Get-ScalarText $physical.Usage
        PoolAssociationKey = if ($poolMembership.ContainsKey($physicalKey)) { [string]$poolMembership[$physicalKey] } else { '' }
    }
}

$storagePools = foreach ($pool in $poolObjects) {
    $poolKey = Get-AssociationKey $pool ([string]$pool.FriendlyName)
    [ordered]@{
        AssociationKey = $poolKey
        UniqueId = [string]$pool.UniqueId
        ObjectId = [string]$pool.ObjectId
        FriendlyName = [string]$pool.FriendlyName
        IsPrimordial = [bool]$pool.IsPrimordial
        HealthStatus = Get-ScalarText $pool.HealthStatus
        OperationalStatus = Get-ScalarText $pool.OperationalStatus
        Size = [decimal]$pool.Size
        AllocatedSize = [decimal]$pool.AllocatedSize
        LogicalSectorSize = if ($null -eq $pool.LogicalSectorSize) { $null } else { [decimal]$pool.LogicalSectorSize }
        PhysicalSectorSize = if ($null -eq $pool.PhysicalSectorSize) { $null } else { [decimal]$pool.PhysicalSectorSize }
        ProvisioningTypeDefault = [string]$pool.ProvisioningTypeDefault
        SubsystemAssociationKey = if ($subsystemObjects.Count -eq 1) {
            Get-AssociationKey $subsystemObjects[0] ([string]$subsystemObjects[0].FriendlyName)
        } else { '' }
        MemberPhysicalDiskKeys = @($poolMembersByKey[$poolKey])
    }
}

$partitions = foreach ($partition in $partitionObjects) {
    $associatedDisk = Get-Safe { $partition | Get-Disk -ErrorAction Stop }
    $volume = $null
    $volumeReadState = 'Returned'
    try {
        $associatedVolume = @($partition | Get-Volume | Select-Object -First 1)
        if ($associatedVolume.Count -gt 0) {
            $volume = $associatedVolume[0]
        }
    }
    catch {
        $volumeReadState = 'Failed'
    }
    [ordered]@{
        DiskNumber = [int]$partition.DiskNumber
        PartitionNumber = [int]$partition.PartitionNumber
        VolumeReadState = $volumeReadState
        VolumeSize = if ($null -eq $volume) { $null } else { [decimal]$volume.Size }
        Guid = [string]$partition.Guid
        ObjectId = [string]$partition.ObjectId
        OsDiskUniqueId = [string]$associatedDisk.UniqueId
        OsDiskObjectId = [string]$associatedDisk.ObjectId
        Type = [string]$partition.Type
        MbrType = [string]$partition.MbrType
        GptType = [string]$partition.GptType
        Offset = [decimal]$partition.Offset
        Size = [decimal]$partition.Size
        IsBoot = [bool]$partition.IsBoot
        IsSystem = [bool]$partition.IsSystem
        DriveLetter = [string]$partition.DriveLetter
        FileSystemLabel = if ($null -eq $volume) { '' } else { [string]$volume.FileSystemLabel }
        FileSystem = if ($null -eq $volume) { '' } else { [string]$volume.FileSystem }
        AllocationUnitSize = if ($null -eq $volume -or $null -eq $volume.AllocationUnitSize) { $null } else { [decimal]$volume.AllocationUnitSize }
        SizeRemaining = if ($null -eq $volume) { 0 } else { [decimal]$volume.SizeRemaining }
        HealthStatus = if ($null -eq $volume) { '' } else { Get-ScalarText $volume.HealthStatus }
        OperationalStatus = if ($null -eq $volume) { '' } else { Get-ScalarText $volume.OperationalStatus }
        Path = if ($null -eq $volume) { '' } else { [string]$volume.Path }
        IsHidden = [bool]$partition.IsHidden
        DriveType = if ($null -eq $volume) { '' } else { [string]$volume.DriveType }
        VolumeUniqueId = if ($null -eq $volume) { '' } else { [string]$volume.UniqueId }
        VolumeObjectId = if ($null -eq $volume) { '' } else { [string]$volume.ObjectId }
        AccessPaths = @(Get-SafeList {
            if ($null -eq $volume) { return @() }
            @($volume | Get-VolumeAccessPath -ErrorAction Stop | ForEach-Object { [string]$_.AccessPath })
        })
    }
}

$networkDisks = foreach ($networkDisk in $networkDiskObjects) {
    [ordered]@{
        DeviceId = [string]$networkDisk.DeviceID
        VolumeName = [string]$networkDisk.VolumeName
        ProviderName = [string]$networkDisk.ProviderName
        FileSystem = [string]$networkDisk.FileSystem
        Size = if ($null -eq $networkDisk.Size) { 0 } else { [decimal]$networkDisk.Size }
        FreeSpace = if ($null -eq $networkDisk.FreeSpace) { 0 } else { [decimal]$networkDisk.FreeSpace }
    }
}


""";

    internal const string OsDiskParentBinding = """
function Resolve-OsDiskDeviceParents($disk) {
    $number = [int]$disk.Number
    $physicalKey = ''
    $virtualKey = ''
    if ($virtualDiskKeyByOsDisk.ContainsKey($number)) {
        $virtualKey = [string]$virtualDiskKeyByOsDisk[$number]
        if ($diskPhysicalMap.ContainsKey($number) -and $diskPhysicalMapSource[$number] -eq 'ProviderUniqueId') {
            [void]$sourceQueryFailures.Add([ordered]@{
                ClassName='MSFT_Disk'; Namespace='root/Microsoft/Windows/Storage';
                ReasonCode='ConflictingPhysicalAndVirtualDiskParents'
            })
        }
        # An OS disk's provider VD association determines its parent. Physical
        # DeviceId and OS DiskNumber use different number spaces for pooled VDs.
    } elseif ([string]$disk.BusType -in @('16','Spaces')) {
        [void]$sourceQueryFailures.Add([ordered]@{
            ClassName='MSFT_Disk'; Namespace='root/Microsoft/Windows/Storage';
            ReasonCode='SpacesDiskLacksExactVirtualParent'
        })
    } elseif ($diskPhysicalMap.ContainsKey($number)) {
        $physicalKey = [string]$diskPhysicalMap[$number]
    }
    if ([string]::IsNullOrWhiteSpace($physicalKey) -and [string]::IsNullOrWhiteSpace($virtualKey)) {
        [void]$sourceQueryFailures.Add([ordered]@{
            ClassName='MSFT_Disk'; Namespace='root/Microsoft/Windows/Storage';
            ReasonCode='OsDiskParentAssociationUnavailable'
        })
    }
    return [ordered]@{ PhysicalDiskAssociationKey=$physicalKey; VirtualDiskAssociationKey=$virtualKey }
}

""";

    private const string ReportBody = """

$osDisks = foreach ($disk in $diskObjects) {
    $deviceParents = Resolve-OsDiskDeviceParents $disk
    [ordered]@{
        Number = [int]$disk.Number
        FriendlyName = [string]$disk.FriendlyName
        UniqueId = [string]$disk.UniqueId
        ObjectId = [string]$disk.ObjectId
        PartitionStyle = [string]$disk.PartitionStyle
        Size = [decimal]$disk.Size
        IsBoot = [bool]$disk.IsBoot
        IsSystem = [bool]$disk.IsSystem
        IsOffline = [bool]$disk.IsOffline
        NumberOfPartitions = if ($null -eq $disk.NumberOfPartitions) { $null } else { [int]$disk.NumberOfPartitions }
        Path = [string]$disk.Path
        PhysicalDiskAssociationKey = $deviceParents.PhysicalDiskAssociationKey
        VirtualDiskAssociationKey = $deviceParents.VirtualDiskAssociationKey
    }
}

$warnings = foreach ($tier in $storageTiers) {
    if (@($tier.MemberPhysicalDiskKeys).Count -eq 0) {
        [ordered]@{
            Code = 'TierDiskMappingUnavailable'
            Message = "No reliable physical-disk match was found for tier '$($tier.FriendlyName)'."
            AssociationKey = [string]$tier.AssociationKey
        }
    }
}

$winNt = Get-Safe { Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop }
Add-NativeObservation 'Registry.CurrentVersion' 'machine' ([ordered]@{ DisplayVersion=$winNt.DisplayVersion; ReleaseId=$winNt.ReleaseId; UBR=$winNt.UBR })
$pageFileSettings = foreach ($setting in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_PageFileSetting -ErrorAction Stop) })) {
    [ordered]@{
        Name = [string]$setting.Name
        InitialSize = if ($null -eq $setting.InitialSize) { $null } else { [decimal]$setting.InitialSize }
        MaximumSize = if ($null -eq $setting.MaximumSize) { $null } else { [decimal]$setting.MaximumSize }
    }
}

$pageFileUsages = foreach ($usage in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_PageFileUsage -ErrorAction Stop) })) {
    [ordered]@{
        Name = [string]$usage.Name
        AllocBaseSize = if ($null -eq $usage.AllocBaseSize) { $null } else { [decimal]$usage.AllocBaseSize }
        CurrentUsage = if ($null -eq $usage.CurrentUsage) { $null } else { [decimal]$usage.CurrentUsage }
    }
}

$diskDrives = foreach ($drive in @(Get-SafeList { @(if ($null -eq $WinPoolScopeLocators) { Get-CimInstance -ClassName Win32_DiskDrive -ErrorAction Stop } else { $diskObjects | ForEach-Object { Get-CimInstance -ClassName Win32_DiskDrive -Filter ('Index = ' + [uint32]$_.Number) -ErrorAction Stop } }) })) {
    [ordered]@{
        Index = if ($null -eq $drive.Index) { $null } else { [int]$drive.Index }
        InterfaceType = [string]$drive.InterfaceType
        PNPDeviceID = [string]$drive.PNPDeviceID
        SCSIBus = if ($null -eq $drive.SCSIBus) { $null } else { [int]$drive.SCSIBus }
        SCSILogicalUnit = if ($null -eq $drive.SCSILogicalUnit) { $null } else { [int]$drive.SCSILogicalUnit }
        SCSIPort = if ($null -eq $drive.SCSIPort) { $null } else { [int]$drive.SCSIPort }
        SCSITargetId = if ($null -eq $drive.SCSITargetId) { $null } else { [int]$drive.SCSITargetId }
    }
}


$crashControl = Get-Safe { Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -ErrorAction Stop }
foreach ($disk in $diskObjects) {
    $letters = @($partitionObjects | Where-Object { $_.DiskNumber -eq $disk.Number -and [string]$_.DriveLetter -match '^[A-Za-z]$' } | ForEach-Object { ([string]$_.DriveLetter).ToUpperInvariant() })
    $pageKnown = @($sourceQueryFailures | Where-Object { $_.ClassName -eq 'Win32_PageFileUsage' }).Count -eq 0
    $page = if ($pageKnown) { @($pageFileUsages | Where-Object { $_.Name -match '^[A-Za-z]:' -and $letters -contains $_.Name.Substring(0,1).ToUpperInvariant() }).Count -gt 0 } else { $null }
    $dump = $null
    if ($null -ne $crashControl -and $null -ne $crashControl.CrashDumpEnabled) {
        if ([int]$crashControl.CrashDumpEnabled -eq 0) { $dump = $false }
        else {
            $paths = @($crashControl.DumpFile, $crashControl.MinidumpDir, $crashControl.DedicatedDumpFile) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { [Environment]::ExpandEnvironmentVariables([string]$_) }
            if ($pageKnown -and @($paths | Where-Object { $_ -notmatch '^[A-Za-z]:' }).Count -eq 0 -and $paths.Count -gt 0) {
                $dump = [bool]($page -or @($paths | Where-Object { $letters -contains $_.Substring(0,1).ToUpperInvariant() }).Count -gt 0)
            }
        }
    }
    Add-NativeObservation 'Windows.DiskRoles' (Get-AssociationKey $disk '') ([ordered]@{ DiskNumber=[int]$disk.Number; IsPageFile=$page; IsCrashDump=$dump })
}

if ($WinPoolIncludeHardware) {
# ---------- Hardware inventory (read-only KS native collectors) ----------


$timeZone = Get-Safe { Get-CimInstance -ClassName Win32_TimeZone -ErrorAction Stop }
$licensedProduct = Get-Safe { @(Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL" -ErrorAction Stop | Where-Object { $_.LicenseStatus -eq 1 } | Select-Object -First 1) }
$adminUser = Get-Safe { Get-LocalUser -Name 'Administrator' -ErrorAction Stop }
$powerSchemeText = [string](Get-Safe { (& powercfg.exe /getactivescheme 2>$null) -join ' ' })
$powerPlan = ''
$powerMatches = [regex]::Matches($powerSchemeText, '\(([^()]*)\)')
if ($powerMatches.Count -gt 0) { $powerPlan = $powerMatches[$powerMatches.Count - 1].Groups[1].Value }
$biosMode = if (Get-Safe { Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State' -ErrorAction Stop } $false) { 'UEFI' } else { 'Legacy' }
$allProcesses = @(Get-SafeList { @(Get-Process -ErrorAction Stop) })
$processThreads = Get-Safe { [decimal]($allProcesses | ForEach-Object { $_.Threads.Count } | Measure-Object -Sum).Sum } 0
$processHandles = Get-Safe { [decimal]($allProcesses | Measure-Object -Property Handles -Sum).Sum } 0
$installedCulture = [System.Globalization.CultureInfo]::InstalledUICulture
$currentRegion = [System.Globalization.RegionInfo]::CurrentRegion

$bios = Get-Safe { Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop }
$baseBoard = Get-Safe { Get-CimInstance -ClassName Win32_BaseBoard -ErrorAction Stop }

$processors = foreach ($cpu in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop) })) {
    [ordered]@{
        Name = [string]$cpu.Name
        MaxClockSpeed = if ($null -eq $cpu.MaxClockSpeed) { $null } else { [decimal]$cpu.MaxClockSpeed }
        NumberOfCores = if ($null -eq $cpu.NumberOfCores) { $null } else { [int]$cpu.NumberOfCores }
        NumberOfLogicalProcessors = if ($null -eq $cpu.NumberOfLogicalProcessors) { $null } else { [int]$cpu.NumberOfLogicalProcessors }
        L2CacheSize = if ($null -eq $cpu.L2CacheSize) { $null } else { [decimal]$cpu.L2CacheSize }
        L3CacheSize = if ($null -eq $cpu.L3CacheSize) { $null } else { [decimal]$cpu.L3CacheSize }
    }
}

$cpuCaches = foreach ($cache in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_CacheMemory -ErrorAction Stop) })) {
    [ordered]@{
        Level = if ($null -eq $cache.Level) { $null } else { [int]$cache.Level }
        MaxCacheSize = if ($null -eq $cache.MaxCacheSize) { $null } else { [decimal]$cache.MaxCacheSize }
        Purpose = [string]$cache.Purpose
    }
}

$memoryArrays = foreach ($array in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_PhysicalMemoryArray -ErrorAction Stop) })) {
    [ordered]@{
        MemoryDevices = if ($null -eq $array.MemoryDevices) { $null } else { [int]$array.MemoryDevices }
        MemoryErrorCorrection = if ($null -eq $array.MemoryErrorCorrection) { $null } else { [int]$array.MemoryErrorCorrection }
    }
}

$memoryDevices = foreach ($stick in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_PhysicalMemory -ErrorAction Stop) })) {
    [ordered]@{
        Capacity = if ($null -eq $stick.Capacity) { $null } else { [decimal]$stick.Capacity }
        Speed = if ($null -eq $stick.Speed) { $null } else { [decimal]$stick.Speed }
        ConfiguredClockSpeed = if ($null -eq $stick.ConfiguredClockSpeed) { $null } else { [decimal]$stick.ConfiguredClockSpeed }
        SMBIOSMemoryType = if ($null -eq $stick.SMBIOSMemoryType) { $null } else { [int]$stick.SMBIOSMemoryType }
        PartNumber = ([string]$stick.PartNumber).Trim()
        DeviceLocator = [string]$stick.DeviceLocator
        BankLabel = [string]$stick.BankLabel
        Manufacturer = [string]$stick.Manufacturer
        SerialNumber = ([string]$stick.SerialNumber).Trim()
    }
}

$logicalVolumes = foreach ($logical in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_LogicalDisk -ErrorAction Stop) })) {
    [ordered]@{
        DeviceID = [string]$logical.DeviceID
        DriveType = if ($null -eq $logical.DriveType) { $null } else { [int]$logical.DriveType }
        VolumeSerialNumber = [string]$logical.VolumeSerialNumber
        Compressed = if ($null -eq $logical.Compressed) { $null } else { [bool]$logical.Compressed }
        ProviderName = [string]$logical.ProviderName
    }
}

$videoMemoryByDeviceId = @{}
$videoRegistryRoot = Get-Safe { [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Video') }
if ($null -ne $videoRegistryRoot) {
    foreach ($subKeyName in $videoRegistryRoot.GetSubKeyNames()) {
        $settingsKey = Get-Safe { $videoRegistryRoot.OpenSubKey("$subKeyName\0000") }
        if ($null -eq $settingsKey) { continue }
        $matchingDeviceId = ([string](Get-Safe { $settingsKey.GetValue('MatchingDeviceId') })).Trim()
        $memoryBytes = Get-Safe { [int64]($settingsKey.GetValue('HardwareInformation.qwMemorySize')) }
        $settingsKey.Close()
        if ([string]::IsNullOrWhiteSpace($matchingDeviceId) -or $null -eq $memoryBytes -or $memoryBytes -le 0) { continue }
        $videoMemoryByDeviceId[$matchingDeviceId.ToLowerInvariant()] = $memoryBytes
    }
    $videoRegistryRoot.Close()
}
$videoControllers = foreach ($controller in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop) })) {
    $pnpId = ([string]$controller.PNPDeviceID).Trim()
    $locationInfo = [string](Get-Safe { (Get-PnpDeviceProperty -InstanceId $pnpId -KeyName 'DEVPKEY_Device_LocationInfo' -ErrorAction Stop).Data })
    $dedicatedBytes = $null
    if (-not [string]::IsNullOrWhiteSpace($pnpId)) {
        $pnpIdLower = $pnpId.ToLowerInvariant()
        foreach ($knownDeviceId in $videoMemoryByDeviceId.Keys) {
            if ($pnpIdLower.StartsWith($knownDeviceId)) {
                $dedicatedBytes = $videoMemoryByDeviceId[$knownDeviceId]
                break
            }
        }
    }
    Add-NativeObservation 'Registry.VideoMemory' $pnpId ([ordered]@{ PNPDeviceID=$pnpId; DedicatedMemoryBytes=$dedicatedBytes; LocationInfo=$locationInfo })
    [ordered]@{
        Name = [string]$controller.Name
        DriverVersion = [string]$controller.DriverVersion
        AdapterRAM = if ($null -eq $controller.AdapterRAM) { $null } else { [decimal]$controller.AdapterRAM }
        DedicatedMemoryBytes = $dedicatedBytes
        CurrentHorizontalResolution = if ($null -eq $controller.CurrentHorizontalResolution) { $null } else { [int]$controller.CurrentHorizontalResolution }
        CurrentVerticalResolution = if ($null -eq $controller.CurrentVerticalResolution) { $null } else { [int]$controller.CurrentVerticalResolution }
        CurrentRefreshRate = if ($null -eq $controller.CurrentRefreshRate) { $null } else { [int]$controller.CurrentRefreshRate }
        CurrentBitsPerPixel = if ($null -eq $controller.CurrentBitsPerPixel) { $null } else { [int]$controller.CurrentBitsPerPixel }
        PNPDeviceID = $pnpId
        LocationInfo = $locationInfo
        Status = [string]$controller.Status
    }
}

$wmiMonitors = @(Get-SafeList { @(Get-CimInstance -Namespace 'root\wmi' -ClassName WmiMonitorID -ErrorAction Stop) })
$screens = @(Get-SafeList {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    [System.Windows.Forms.Screen]::AllScreens
})
foreach ($screen in $screens) {
    Add-NativeObservation 'WindowsForms.Screen' ([string]$screen.DeviceName) ([ordered]@{
        Name=[string]$screen.DeviceName; Width=[int]$screen.Bounds.Width; Height=[int]$screen.Bounds.Height; BitsPerPixel=[int]$screen.BitsPerPixel; Primary=[bool]$screen.Primary
    })
}
$networkAdapters = foreach ($adapter in @(Get-SafeList { @(Get-NetAdapter -ErrorAction Stop) })) {
    $interfaceIndex = [int]$adapter.ifIndex
    $ipv4Addresses = @($allNetIpAddresses | Where-Object { $_.InterfaceIndex -eq $interfaceIndex -and $_.AddressFamily -eq 'IPv4' } | ForEach-Object { [string]$_.IPAddress })
    $ipv6Addresses = @($allNetIpAddresses | Where-Object { $_.InterfaceIndex -eq $interfaceIndex -and $_.AddressFamily -eq 'IPv6' } | ForEach-Object { [string]$_.IPAddress })
    $dhcp = [string]($allNetInterfaces | Where-Object { $_.InterfaceIndex -eq $interfaceIndex } | Select-Object -First 1 | ForEach-Object { $_.Dhcp })
    $dnsServers = @($allDnsClients | Where-Object { $_.InterfaceIndex -eq $interfaceIndex } | ForEach-Object { @($_.ServerAddresses) } | ForEach-Object { $_ })
    $gateways = @($allDefaultRoutes | Where-Object { $_.InterfaceIndex -eq $interfaceIndex } | ForEach-Object { [string]$_.NextHop })
    [ordered]@{
        Name = [string]$adapter.Name
        InterfaceDescription = [string]$adapter.InterfaceDescription
        LinkSpeed = [string]$adapter.LinkSpeed
        Status = [string]$adapter.Status
        MacAddress = ([string]$adapter.MacAddress) -replace '-', ''
        IPv4Addresses = @($ipv4Addresses)
        IPv6Addresses = @($ipv6Addresses)
        Dhcp = $dhcp
        DnsServers = @($dnsServers)
        DefaultGateways = @($gateways)
    }
}

$winBatteries = @(Get-SafeList { @(Get-CimInstance -ClassName Win32_Battery -ErrorAction Stop) })
$batteryStatic = @(Get-SafeList { @(Get-CimInstance -Namespace 'root\wmi' -ClassName BatteryStaticData -ErrorAction Stop) })
$batteryRuntime = @(Get-SafeList { @(Get-CimInstance -Namespace 'root\wmi' -ClassName BatteryStatus -ErrorAction Stop) })
$batteries = for ($batteryIndex = 0; $batteryIndex -lt $winBatteries.Count; $batteryIndex++) {
    $battery = $winBatteries[$batteryIndex]
    $static = if ($batteryIndex -lt $batteryStatic.Count) { $batteryStatic[$batteryIndex] } else { $null }
    $runtime = if ($batteryIndex -lt $batteryRuntime.Count) { $batteryRuntime[$batteryIndex] } else { $null }
    $designCapacity = if ($null -ne $static -and $null -ne $static.DesignedCapacity) { [decimal]$static.DesignedCapacity }
        elseif ($null -ne $battery.DesignCapacity) { [decimal]$battery.DesignCapacity }
        else { $null }
    [ordered]@{
        Name = [string]$battery.Name
        DesignCapacity = $designCapacity
        RemainingCapacity = if ($null -eq $runtime -or $null -eq $runtime.RemainingCapacity) { $null } else { [decimal]$runtime.RemainingCapacity }
        BatteryStatus = if ($null -eq $battery.BatteryStatus) { $null } else { [int]$battery.BatteryStatus }
        EstimatedChargeRemaining = if ($null -eq $battery.EstimatedChargeRemaining) { $null } else { [int]$battery.EstimatedChargeRemaining }
    }
}


Add-NativeObservation 'Windows.Session' 'machine' ([ordered]@{ InstalledUICulture=[string]$installedCulture.Name; RegionName=[string]$currentRegion.Name; AdministratorEnabled=if ($null -eq $adminUser) {$null} else {[bool]$adminUser.Enabled}; ActivePowerScheme=$powerSchemeText; Processes=[int]$allProcesses.Count; Threads=$processThreads; Handles=$processHandles })

} else {
    $hardware = [ordered]@{}
    $logicalVolumes = @()

}

[ordered]@{
    ScannedAt = $scannedAt.ToString('o')
    Computer = [ordered]@{
        Name = [string]$computerSystem.Name
        WindowsProductName = [string]$operatingSystem.Caption
        WindowsVersion = [string]$operatingSystem.Version
        OsBuild = [string]$operatingSystem.BuildNumber
        LastBootTime = ([DateTimeOffset]$operatingSystem.LastBootUpTime).ToString('o')
    }
    StorageSubsystems = @($subsystems)
    PhysicalDisks = @($physicalDisks)
    StoragePools = @($storagePools)
    StorageTiers = @($storageTiers)
    VirtualDisks = @($virtualDisks)
    OsDisks = @($osDisks)
    Partitions = @($partitions)
    NetworkDisks = @($networkDisks)
    LogicalVolumes = @($logicalVolumes)
    DiskDrives = @($diskDrives)
    SourceObservations = @($sourceObservations.ToArray())
    SourceQueryFailures = @($sourceQueryFailures.ToArray())
    SourceQuerySuccesses = @($sourceQuerySuccesses.ToArray())
    CollectionPurpose = if ($WinPoolIncludeHardware) { 'Hardware' } else { 'Storage' }
    Warnings = @($warnings)
} | ConvertTo-Json -Depth 12 -Compress



""";
}
