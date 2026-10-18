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
        return Create(CollectionPurpose.Storage, scope, discoverProviderCache || scope.DiscoverProviderCache);
    }

    private static string Create(CollectionPurpose purpose, StorageInventoryScope? scope, bool discoverProviderCache)
    {
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        var selectors = scope is null ? "$null" :
            "([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope.BeforeLocators)))
            + "')) | ConvertFrom-Json)";
        var component = scope?.BeforeComponentLocators is null ? "$null" : "([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope.BeforeComponentLocators))) + "')) | ConvertFrom-Json)";
        var fields = WindowsRealStorageReadProfile.ForId(scope?.EvidenceProfile)?.Fields;
        var encodedFields = fields is null ? "$null" : "([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields))) + "')) | ConvertFrom-Json)";
        return "$WinPoolIncludeHardware = " + (purpose == CollectionPurpose.Hardware ? "$true" : "$false")
            + "\n$WinPoolDiscoverProviderCache = " + (discoverProviderCache ? "$true" : "$false")
            + "\n$WinPoolScopeLocators = " + selectors + "\n$WinPoolComponentLocators = " + component + "\n$WinPoolEvidenceFields = " + encodedFields + "\n" + Template;
    }

    private static readonly string Template = Prelude + DeviceLocationReader + ScopeClosure + StorageClassQueries + ProviderDiscovery + StorageReads
        + "\n$collectionTimings['baseClasses'] = $collectionClock.Elapsed.TotalMilliseconds\n"
        + TierMemberAssociationHelpers + InventoryBody + OsDiskParentBinding + ReportBody;

    internal static string HostTemplate => "param($parameters)\n$WinPoolIncludeHardware = [bool]$parameters.IncludeHardware\n"
        + "$WinPoolDiscoverProviderCache = [bool]$parameters.DiscoverProviderCache\n$WinPoolScopeLocators = $parameters.ScopeLocators\n$WinPoolComponentLocators = $parameters.ComponentLocators\n$WinPoolEvidenceFields = $parameters.EvidenceFields\n" + Template;

    private const string Prelude = """
$ErrorActionPreference = 'Stop'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$collectionClock = [Diagnostics.Stopwatch]::StartNew()
$collectionTimings = [ordered]@{}

function Write-WinPoolInventoryStage([string]$stage, [string]$class = '', [string]$related = '') {
    if ($null -eq $WinPoolEvidenceFields) { return }
    # Diagnostics use the separately drained stderr pipe, never the framed result stream.
    try { [Console]::Error.WriteLine('winpool.inventory|' + $stage + '|' + $class + '|' + $related) } catch { }
}
Write-WinPoolInventoryStage 'begin'


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
    $selected = if ($null -eq $WinPoolEvidenceFields) { $null } else { $WinPoolEvidenceFields.PSObject.Properties[$className] }
    $fields = foreach ($property in $Item.CimInstanceProperties) {
        if ($null -ne $selected -and @($selected.Value) -notcontains [string]$property.Name) { continue }
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
    param([scriptblock]$Query, $Default = $null, [string]$TimingName = '')
    $classMatch = [regex]::Match($Query.ToString(), '(?:Win32|MSFT)_[A-Za-z0-9_]+|WmiMonitorID|BatteryStaticData|BatteryStatus|SoftwareLicensingProduct')
    $queryNamespace = if ($classMatch.Value.StartsWith('MSFT_')) { 'root/Microsoft/Windows/Storage' } elseif ($Query.ToString() -match 'root\\wmi') { 'root/wmi' } else { 'root/cimv2' }
    $timingKey = if ($TimingName -ne '') { $TimingName } elseif ($classMatch.Success) { $classMatch.Value } else { '' }
    $queryClock = [Diagnostics.Stopwatch]::StartNew()
    try {
        Write-WinPoolInventoryStage 'safe-query.begin' $timingKey
        $value = & $Query
        Write-WinPoolInventoryStage 'safe-query.returned' $timingKey
        $queryClock.Stop()
        if ($classMatch.Success) {
            [void]$sourceQuerySuccesses.Add([ordered]@{ ClassName=$classMatch.Value; Namespace=$queryNamespace })
        }
        $projectionClock = [Diagnostics.Stopwatch]::StartNew()
        foreach ($observed in @($value)) { Add-SourceObservation $observed }
        if ($timingKey -ne '') {
            $key = 'projection:' + $timingKey
            $collectionTimings[$key] = [double]$collectionTimings[$key] + $projectionClock.Elapsed.TotalMilliseconds
        }
        if ($null -eq $value) { return $Default }
        return $value
    } catch {
        if ($classMatch.Success) {
            [void]$sourceQueryFailures.Add([ordered]@{ ClassName=$classMatch.Value; Namespace=$queryNamespace; ReasonCode=[string]$_.FullyQualifiedErrorId })
        }
        return $Default
    } finally {
        $queryClock.Stop()
        if ($timingKey -ne '') {
            $key = 'query:' + $timingKey
            $collectionTimings[$key] = [double]$collectionTimings[$key] + $queryClock.Elapsed.TotalMilliseconds
        }
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
    if ($null -ne $WinPoolScopeLocators -and $null -ne $WinPoolEvidenceFields -and
        $ClassName.StartsWith('MSFT_') -and -not $WinPoolScopeQueried.ContainsKey($ClassName)) { return @() }
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

    internal const string DeviceLocationReader = """
function Read-WinPoolDeviceLocation([string]$instanceId) {
    if ([string]::IsNullOrWhiteSpace($instanceId)) { return $null }
    if ($null -eq ('WinPoolDeviceLocation' -as [type])) {
        # Define only the fixed native signatures; no compiler process or cached device values.
        $assembly = [AppDomain]::CurrentDomain.DefineDynamicAssembly([Reflection.AssemblyName]::new('WinPool.Native.DeviceLocation'), [Reflection.Emit.AssemblyBuilderAccess]::Run)
        $builder = $assembly.DefineDynamicModule('Native').DefineType('WinPoolDeviceLocation', [Reflection.TypeAttributes]'Public,Abstract,Sealed')
        $flags = [Reflection.MethodAttributes]'Public,Static,PinvokeImpl'
        $calling = [Reflection.CallingConventions]::Standard
        $nativeCalling = [Runtime.InteropServices.CallingConvention]::Winapi
        $charset = [Runtime.InteropServices.CharSet]::Unicode
        $method = $builder.DefinePInvokeMethod('Locate', 'cfgmgr32.dll', 'CM_Locate_DevNodeW', $flags, $calling, [uint32], [type[]]@([uint32].MakeByRefType(),[string],[uint32]), $nativeCalling, $charset)
        $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
        $method = $builder.DefinePInvokeMethod('Property', 'cfgmgr32.dll', 'CM_Get_DevNode_PropertyW', $flags, $calling, [uint32], [type[]]@([uint32],[byte[]],[uint32].MakeByRefType(),[byte[]],[uint32].MakeByRefType(),[uint32]), $nativeCalling, $charset)
        $null = $method.DefineParameter(4, [Reflection.ParameterAttributes]::Out, 'buffer')
        $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
        $null = $builder.CreateType()
    }
    [uint32]$device = 0
    $result = [WinPoolDeviceLocation]::Locate([ref]$device, $instanceId, 0)
    if ($result -ne 0) { throw ('native-location-device-unavailable:' + $result) }
    # DEVPROPKEY from devpkey.h: DEVPKEY_Device_LocationInfo, DEVPROP_TYPE_STRING.
    [byte[]]$key = ([guid]'a45c254e-df1c-4efd-8020-67d146a850e0').ToByteArray() + [BitConverter]::GetBytes([uint32]15)
    [uint32]$type = 0; [uint32]$length = 0
    $result = [WinPoolDeviceLocation]::Property($device, $key, [ref]$type, $null, [ref]$length, 0)
    if ($result -notin @(0,0x1a)) { throw ('native-location-property-unavailable:' + $result) }
    if ($length -gt 67108864) { throw 'native-location-exceeds-host-result-bound' }
    $bytes = [byte[]]::new([int]$length)
    $result = [WinPoolDeviceLocation]::Property($device, $key, [ref]$type, $bytes, [ref]$length, 0)
    if ($result -ne 0 -or $type -ne 0x12 -or ($length % 2) -ne 0) { throw ('native-location-property-invalid:' + $result) }
    return [Text.Encoding]::Unicode.GetString($bytes, 0, [int]$length).TrimEnd([char]0)
}

""";

    internal const string ScopeClosure = """
$WinPoolScopeNamespace = 'root/Microsoft/Windows/Storage'
$WinPoolScopeObjects = @{}
$WinPoolScopePrimordialMembers = @{}
$WinPoolScopePending = [System.Collections.Generic.Queue[object]]::new()
$WinPoolScopeState = @{ Failed=$false }
$WinPoolScopeQueried = @{}
$WinPoolScopeAssociations = @{}
function Get-WinPoolScopeAssociated($item, [string]$class) {
    $key = (Get-WinPoolScopeKey $item) + '|' + $class
    if (-not $WinPoolScopeAssociations.ContainsKey($key)) { throw 'scope-association-was-not-collected' }
    return @($WinPoolScopeAssociations[$key])
}
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
        Write-WinPoolInventoryStage 'association.begin' 'MSFT_StoragePool' 'MSFT_StorageSubSystem'
        $parents = @(Get-CimAssociatedInstance -InputObject $item -ResultClassName MSFT_StorageSubSystem -ErrorAction Stop)
        Write-WinPoolInventoryStage 'association.returned' 'MSFT_StoragePool' 'MSFT_StorageSubSystem'
        $WinPoolScopeQueried['MSFT_StorageSubSystem'] = $true
        $WinPoolScopeAssociations[$key + '|MSFT_StorageSubSystem'] = @($parents)
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
    $query = @{ Namespace=$WinPoolScopeNamespace; ClassName=$class; Filter=($property + " = '" + $escaped + "'"); ErrorAction='Stop' }
    if ($null -ne $WinPoolEvidenceFields -and $null -ne $WinPoolEvidenceFields.PSObject.Properties[$class]) {
        $query.Property = @($WinPoolEvidenceFields.PSObject.Properties[$class].Value | Where-Object {
            -not ($class -eq 'MSFT_PhysicalDisk' -and $_ -in @('IsBoot','IsSystem','IsPageFile','IsCrashDump')) -and
            -not ($class -eq 'MSFT_Partition' -and $_ -eq 'Type') })
    }
    Write-WinPoolInventoryStage 'identity.begin' $class $property
    $matches = @(Get-CimInstance @query)
    Write-WinPoolInventoryStage 'identity.returned' $class $property
    $WinPoolScopeQueried[$class] = $true
    if ($matches.Count -gt 1) { throw 'scope-selector-not-unique' }
    foreach ($match in $matches) {
        if (-not [string]::Equals([string]$match.$property, $value, [StringComparison]::Ordinal)) { throw 'scope-selector-not-exact' }
        Add-WinPoolScopeObject $match
    }
}
function Initialize-WinPoolScope {
if ($null -eq $WinPoolScopeLocators) { return }
Write-WinPoolInventoryStage 'closure.begin'
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
            Write-WinPoolInventoryStage 'association.begin' $class $resultClass
            $associations[$resultClass] = @(Get-CimAssociatedInstance -InputObject $item -ResultClassName $resultClass -ErrorAction Stop)
            Write-WinPoolInventoryStage 'association.returned' $class $resultClass
            $WinPoolScopeQueried[$resultClass] = $true
            $WinPoolScopeAssociations[(Get-WinPoolScopeKey $item) + '|' + $resultClass] = @($associations[$resultClass])
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
                Write-WinPoolInventoryStage 'association.begin' 'MSFT_VirtualDisk' 'MSFT_StoragePool'
                $virtualDiskPools = @(Get-CimAssociatedInstance -InputObject $tierVirtualDisk -ResultClassName MSFT_StoragePool -ErrorAction Stop |
                    Where-Object { -not $_.IsPrimordial })
                Write-WinPoolInventoryStage 'association.returned' 'MSFT_VirtualDisk' 'MSFT_StoragePool'
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
    # A vanished parent can make an old child unreachable. A query for another object
    # of the same class does not cover that old identity's absence.
    if ($null -ne $WinPoolComponentLocators) {
        foreach ($locator in @($WinPoolComponentLocators)) {
            $observed = @($WinPoolScopeObjects.Values | Where-Object {
                [string]$_.CimClass.CimClassName -eq [string]$locator.ClassName -and
                [string]::Equals([string]$_.($locator.IdentityProperty), [string]$locator.IdentityValue, [StringComparison]::Ordinal)
            })
            if ($observed.Count -gt 0) { continue }
            Find-WinPoolScopeIdentity $locator.ClassName $locator.IdentityProperty $locator.IdentityValue
            if ($WinPoolScopePending.Count -gt 0) { throw 'scope-old-child-now-outside-current-component' }
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
Write-WinPoolInventoryStage 'closure.ended'
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

Write-WinPoolInventoryStage 'class.begin' 'Win32_OperatingSystem'
$operatingSystem = if ($null -ne $WinPoolEvidenceFields) {
    Get-CimInstance -ClassName Win32_OperatingSystem -Property Caption,Version,BuildNumber,LastBootUpTime -ErrorAction Stop
} else { Get-CimInstance -ClassName Win32_OperatingSystem }
Write-WinPoolInventoryStage 'class.returned' 'Win32_OperatingSystem'
Write-WinPoolInventoryStage 'class.begin' 'Win32_ComputerSystem'
$computerSystem = if ($null -ne $WinPoolEvidenceFields) {
    Get-CimInstance -ClassName Win32_ComputerSystem -Property Name -ErrorAction Stop
} else { Get-CimInstance -ClassName Win32_ComputerSystem }
Write-WinPoolInventoryStage 'class.returned' 'Win32_ComputerSystem'
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
Write-WinPoolInventoryStage 'storage-projection.begin'
if ($null -ne $WinPoolScopeLocators) {
    $subsystemObjects = @(Get-SourceSet 'MSFT_StorageSubSystem' { Get-WinPoolScopeClass 'MSFT_StorageSubSystem' })
}
$physicalObjects = @(Get-SourceSet 'MSFT_PhysicalDisk' { Get-WinPoolStorageClass 'MSFT_PhysicalDisk' })
$poolObjects = @(Get-SourceSet 'MSFT_StoragePool' { Get-WinPoolStorageClass 'MSFT_StoragePool' })
$nonPrimordialPoolObjects = @($poolObjects | Where-Object { -not $_.IsPrimordial })
$diskObjects = @(Get-SourceSet 'MSFT_Disk' { Get-WinPoolStorageClass 'MSFT_Disk' })
$partitionObjects = @(Get-SourceSet 'MSFT_Partition' { Get-WinPoolStorageClass 'MSFT_Partition' })
$volumeObjects = @(Get-SourceSet 'MSFT_Volume' { Get-WinPoolStorageClass 'MSFT_Volume' })
$logicalDiskObjects = @(if ($null -eq $WinPoolScopeLocators -or $null -eq $WinPoolEvidenceFields) {
    Get-SourceSet 'Win32_LogicalDisk' { Get-CimInstance -ClassName Win32_LogicalDisk -ErrorAction Stop } 'root/cimv2'
})
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
$poolCimMembersByKey = @{}
foreach ($pool in @($nonPrimordialPoolObjects) + @($poolObjects | Where-Object { $_.IsPrimordial })) {
    $poolKey = Get-AssociationKey $pool ([string]$pool.FriendlyName)
    $members = @(Get-SourceSet 'MSFT_PhysicalDisk' { if ($null -ne $WinPoolScopeLocators -and $pool.IsPrimordial) { Get-WinPoolScopePrimordialMembers $pool } else { if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $pool 'MSFT_PhysicalDisk' } else { Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop } } })
    $poolCimMembersByKey[$poolKey] = @($members)
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

        $parents = @(if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $virtual 'MSFT_StoragePool' } else { Get-StoragePool -VirtualDisk $virtual -ErrorAction Stop })
        if ($parents.Count -ne 1 -or $parents[0].IsPrimordial -isnot [bool] -or $parents[0].IsPrimordial -or
            -not [string]::Equals([string]$parents[0].UniqueId, $poolUniqueId, [StringComparison]::Ordinal) -or
            -not [string]::Equals([string]$parents[0].ObjectId, $poolObjectId, [StringComparison]::Ordinal)) { return $null }

        $poolMembers = @(if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $pool 'MSFT_PhysicalDisk' }
            elseif ($null -ne $poolCimMembersByKey -and $poolCimMembersByKey.ContainsKey((Get-AssociationKey $pool ''))) { $poolCimMembersByKey[(Get-AssociationKey $pool '')] }
            else { Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop })
        if ($poolMembers.Count -ne 1) { return $null }
        $member = $poolMembers[0]
        $memberUniqueId = [string]$member.UniqueId
        $memberObjectId = [string]$member.ObjectId
        if ([string]::IsNullOrWhiteSpace($memberUniqueId) -or [string]::IsNullOrWhiteSpace($memberObjectId) -or
            -not [string]::Equals([string]$member.MediaType, 'HDD', [StringComparison]::OrdinalIgnoreCase)) { return $null }

        Write-WinPoolInventoryStage 'allocated-members.begin' 'MSFT_VirtualDisk' 'MSFT_PhysicalDisk'
        $allocatedMembers = @(Get-PhysicalDisk -VirtualDisk $virtual -HasAllocations $true -ErrorAction Stop)
        Write-WinPoolInventoryStage 'allocated-members.returned' 'MSFT_VirtualDisk' 'MSFT_PhysicalDisk'
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
    Write-WinPoolInventoryStage 'tier-extents.begin' 'MSFT_StorageTier' 'GetPhysicalExtent'
    $result = Invoke-CimMethod -InputObject $tier -MethodName GetPhysicalExtent -ErrorAction Stop
    Write-WinPoolInventoryStage 'tier-extents.returned' 'MSFT_StorageTier' 'GetPhysicalExtent'
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
    foreach ($virtual in @(Get-SourceSet 'MSFT_VirtualDisk' { if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $pool 'MSFT_VirtualDisk' } else { Get-VirtualDisk -StoragePool $pool -ErrorAction Stop } })) {
        Add-SourceObservation $virtual
        $virtualKey = Get-AssociationKey $virtual "$($virtual.FriendlyName)|$($virtual.Size)"
        $osDiskNumbers = @()
        foreach ($mappedDisk in @(Get-SourceSet 'MSFT_Disk' { if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $virtual 'MSFT_Disk' } else { Get-Disk -VirtualDisk $virtual -ErrorAction Stop } })) {
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
            $virtualTiers = @(Get-SourceSet 'MSFT_StorageTier' { if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $virtual 'MSFT_StorageTier' } else { Get-StorageTier -VirtualDisk $virtual -ErrorAction Stop } })
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
        if ($null -ne $WinPoolScopeLocators) {
            $physicalMatch = @(Get-WinPoolScopeAssociated $disk 'MSFT_PhysicalDisk')
        }
        if ($physicalMatch.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$disk.UniqueId)) {
            $physicalMatch = @($physicalObjects | Where-Object { [string]::Equals([string]$_.UniqueId, [string]$disk.UniqueId, [StringComparison]::Ordinal) })
        }
        if ($null -eq $WinPoolScopeLocators -and $physicalMatch.Count -eq 0 -and -not $virtualDiskKeyByOsDisk.ContainsKey([int]$disk.Number)) {
            $physicalMatch = @($disk | Get-PhysicalDisk -ErrorAction Stop)
        }
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
    foreach ($tier in @(Get-SourceSet 'MSFT_StorageTier' { if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $pool 'MSFT_StorageTier' } else { Get-StorageTier -StoragePool $pool -ErrorAction Stop } })) {
        $tierKey = Get-AssociationKey $tier ''
        if ($tierKeysSeen.ContainsKey($tierKey)) { continue }
        try {
            $parents = @($(if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $tier 'MSFT_StoragePool' } else { Get-StoragePool -StorageTier $tier -ErrorAction Stop }) | Where-Object { -not $_.IsPrimordial })
            if ($parents.Count -ne 1 -or
                -not [string]::Equals([string]$parents[0].UniqueId, [string]$pool.UniqueId, [StringComparison]::Ordinal) -or
                -not [string]::Equals([string]$parents[0].ObjectId, [string]$pool.ObjectId, [StringComparison]::Ordinal)) {
                throw 'tier-template-pool-not-exact'
            }
            if (@(if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $tier 'MSFT_VirtualDisk' } else { Get-VirtualDisk -StorageTier $tier -ErrorAction Stop }).Count -ne 0) { throw 'tier-owner-association-incomplete' }
            $members = @($poolCimMembersByKey[$poolKey])
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
    $associatedDisk = Get-Safe {
        if ($null -ne $WinPoolScopeLocators) { return Get-WinPoolScopeAssociated $partition 'MSFT_Disk' }
        $current = @($diskObjects | Where-Object { [uint32]$_.Number -eq [uint32]$partition.DiskNumber -and
            ([string]::Equals([string]$_.ObjectId, [string]$partition.DiskId, [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals([string]$_.Path, [string]$partition.DiskId, [StringComparison]::OrdinalIgnoreCase)) })
        if ($current.Count -eq 1) { return $current[0] }
        $partition | Get-Disk -ErrorAction Stop
    }
    $volume = $null
    $volumeReadState = 'Returned'
    try {
        $associatedVolume = @(if ($null -ne $WinPoolScopeLocators) { Get-WinPoolScopeAssociated $partition 'MSFT_Volume' }
            else {
                $paths = @($partition.AccessPaths | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | ForEach-Object { ([string]$_).TrimEnd('\') })
                $current = @($volumeObjects | Where-Object { $paths -contains ([string]$_.Path).TrimEnd('\') -or
                    $paths -contains ([string]$_.UniqueId).TrimEnd('\') })
                if ($current.Count -gt 0) { $current }
                elseif ([string]$partition.GptType -notmatch '(?i)e3c9e316-0b5c-4db8-817d-f92df00215ae') { $partition | Get-Volume }
            })
        if ($associatedVolume.Count -gt 1) { throw 'partition-volume-association-not-unique' }
        if ($associatedVolume.Count -gt 0) {
            $volume = $associatedVolume[0]
        }
    }
    catch {
        $volumeReadState = 'Failed'
        [void]$sourceQueryFailures.Add([ordered]@{
            ClassName='MSFT_Volume'; Namespace='root/Microsoft/Windows/Storage';
            ReasonCode=('PartitionVolumeAssociationFailed:' + [string]$_.Exception.Message)
        })
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
        AccessPaths = @($partition.AccessPaths)
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

$collectionTimings['storageAssociations'] = $collectionClock.Elapsed.TotalMilliseconds

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

Write-WinPoolInventoryStage 'storage-projection.ended'
Write-WinPoolInventoryStage 'roles.begin'
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

$diskDrives = foreach ($drive in @(Get-SafeList { @(if ($null -eq $WinPoolScopeLocators) { Get-CimInstance -ClassName Win32_DiskDrive -ErrorAction Stop }
    elseif ($null -eq $WinPoolEvidenceFields) { $diskObjects | ForEach-Object { Get-CimInstance -ClassName Win32_DiskDrive -Filter ('Index = ' + [uint32]$_.Number) -ErrorAction Stop } }) })) {
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

$collectionTimings['rolesAndSupplements'] = $collectionClock.Elapsed.TotalMilliseconds
Write-WinPoolInventoryStage 'roles.ended'
if ($WinPoolIncludeHardware) {
# ---------- Hardware inventory (read-only KS native collectors) ----------


$timeZone = Get-Safe { Get-CimInstance -ClassName Win32_TimeZone -ErrorAction Stop }
$licensedProduct = Get-Safe { @(Get-CimInstance -ClassName SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL" -ErrorAction Stop | Where-Object { $_.LicenseStatus -eq 1 } | Select-Object -First 1) }
$collectionTimings['hardwareLicense'] = $collectionClock.Elapsed.TotalMilliseconds
$adminUser = Get-Safe { Get-LocalUser -Name 'Administrator' -ErrorAction Stop }
$powerSchemeText = [string](Get-Safe { (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' -Name ActivePowerScheme -ErrorAction Stop).ActivePowerScheme })
$allProcesses = @(Get-SafeList { @(Get-Process -ErrorAction Stop) })
$processThreads = Get-Safe { [decimal]$total = 0; foreach ($process in $allProcesses) { $total += $process.Threads.Count }; $total } 0
$processHandles = Get-Safe { [decimal]$total = 0; foreach ($process in $allProcesses) { $total += $process.HandleCount }; $total } 0
$collectionTimings['hardwareSession'] = $collectionClock.Elapsed.TotalMilliseconds
$installedCulture = [System.Globalization.CultureInfo]::InstalledUICulture
$currentRegion = [System.Globalization.RegionInfo]::CurrentRegion

$bios = Get-Safe { Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop }
$baseBoard = Get-Safe { Get-CimInstance -ClassName Win32_BaseBoard -ErrorAction Stop }
$collectionTimings['hardwareContext'] = $collectionClock.Elapsed.TotalMilliseconds

$null = Get-SafeList { @(Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop) }
$null = Get-SafeList { @(Get-CimInstance -ClassName Win32_CacheMemory -ErrorAction Stop) }
$null = Get-SafeList { @(Get-CimInstance -ClassName Win32_PhysicalMemoryArray -ErrorAction Stop) }
$null = Get-SafeList { @(Get-CimInstance -ClassName Win32_PhysicalMemory -ErrorAction Stop) }

$collectionTimings['hardwareMemory'] = $collectionClock.Elapsed.TotalMilliseconds
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
foreach ($controller in @(Get-SafeList { @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop) })) {
    $pnpId = ([string]$controller.PNPDeviceID).Trim()
    $locationInfo = Get-Safe { Read-WinPoolDeviceLocation $pnpId } -TimingName 'PnpLocation'
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
$collectionTimings['hardwareVideo'] = $collectionClock.Elapsed.TotalMilliseconds
$networkSupplement = $null
try {
    $networkSupplement = [ordered]@{
        Adapters = @(Get-CimInstance -Namespace 'root/StandardCimv2' -ClassName MSFT_NetAdapter -Property InterfaceGuid,InterfaceIndex,Name,DriverDescription,Speed,PermanentAddress,ConnectorPresent,InterfaceType,InterfaceOperationalStatus,HardwareInterface,Virtual -ErrorAction Stop | Select-Object InterfaceGuid,InterfaceIndex,Name,DriverDescription,Speed,PermanentAddress,ConnectorPresent,InterfaceType,InterfaceOperationalStatus,HardwareInterface,Virtual)
        Addresses = @(Get-CimInstance -Namespace 'root/StandardCimv2' -ClassName MSFT_NetIPAddress -Property InterfaceIndex,AddressFamily,IPAddress -ErrorAction Stop | Select-Object InterfaceIndex,AddressFamily,IPAddress)
        Routes = @(Get-CimInstance -Namespace 'root/StandardCimv2' -ClassName MSFT_NetRoute -Filter "DestinationPrefix = '0.0.0.0/0' OR DestinationPrefix = '::/0'" -Property InterfaceIndex,DestinationPrefix -ErrorAction Stop | Select-Object InterfaceIndex)
    }
} catch { $networkSupplement = [ordered]@{ Error=[string]$_.Exception.Message } }
$collectionTimings['hardwareNetwork'] = $collectionClock.Elapsed.TotalMilliseconds
$null = Get-SafeList { @(Get-CimInstance -ClassName Win32_Battery -ErrorAction Stop) }
$null = Get-SafeList { @(Get-CimInstance -Namespace 'root\wmi' -ClassName BatteryStaticData -ErrorAction Stop) }
$null = Get-SafeList { @(Get-CimInstance -Namespace 'root\wmi' -ClassName BatteryStatus -ErrorAction Stop) }


Add-NativeObservation 'Windows.Session' 'machine' ([ordered]@{ InstalledUICulture=[string]$installedCulture.Name; RegionName=[string]$currentRegion.Name; AdministratorEnabled=if ($null -eq $adminUser) {$null} else {[bool]$adminUser.Enabled}; ActivePowerScheme=$powerSchemeText; Processes=[int]$allProcesses.Count; Threads=$processThreads; Handles=$processHandles })

}

$collectionTimings['completeBeforeEncoding'] = $collectionClock.Elapsed.TotalMilliseconds
Write-WinPoolInventoryStage 'encoding.begin'
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
    DiskDrives = @($diskDrives)
    SourceObservations = @($sourceObservations.ToArray())
    SourceQueryFailures = @($sourceQueryFailures.ToArray())
    SourceQuerySuccesses = @($sourceQuerySuccesses.ToArray())
    NetworkSupplement = if ($WinPoolIncludeHardware) { $networkSupplement } else { $null }
    ProviderPhaseElapsedMilliseconds = $collectionTimings
    ScopeQueriedClasses = @($WinPoolScopeQueried.Keys)
    ScopeClosureComplete = -not $WinPoolScopeState.Failed
    CollectionPurpose = if ($WinPoolIncludeHardware) { 'Hardware' } else { 'Storage' }
    Warnings = @($warnings)
} | ConvertTo-Json -Depth 12 -Compress
Write-WinPoolInventoryStage 'encoding.returned'



""";
}
