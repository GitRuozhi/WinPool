$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

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

$scannedAt = [DateTimeOffset]::Now
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
$computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem
$subsystemObjects = @(Get-StorageSubSystem)
$physicalObjects = @(Get-PhysicalDisk)
$poolObjects = @(Get-StoragePool)
$nonPrimordialPoolObjects = @($poolObjects | Where-Object { -not $_.IsPrimordial })
$diskObjects = @(Get-Disk)
$partitionObjects = @(Get-Partition)
$volumeObjects = @(Get-Volume)
$networkDiskObjects = @(Get-CimInstance -ClassName Win32_LogicalDisk -Filter 'DriveType = 4')

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
    $members = @(Get-PhysicalDisk -StoragePool $pool)
    $memberKeys = foreach ($member in $members) {
        $memberKey = Get-AssociationKey $member "$($member.DeviceId)|$($member.FriendlyName)|$($member.Size)"
        if (-not $pool.IsPrimordial -or -not $poolMembership.ContainsKey($memberKey)) {
            $poolMembership[$memberKey] = $poolKey
            $memberKey
        }
    }
    $poolMembersByKey[$poolKey] = @($memberKeys)
}

$diskPhysicalMap = @{}
foreach ($disk in $diskObjects) {
    try {
        $physicalMatch = @($disk | Get-PhysicalDisk | Select-Object -First 1)
        if ($physicalMatch.Count -gt 0) {
            $diskPhysicalMap[[int]$disk.Number] = Get-AssociationKey $physicalMatch[0] "$($physicalMatch[0].DeviceId)|$($physicalMatch[0].FriendlyName)|$($physicalMatch[0].Size)"
        }
    }
    catch {
    }
}

$virtualDiskKeyByOsDisk = @{}
$virtualDisks = @()
$storageTiers = @()
$tierKeysSeen = @{}
foreach ($pool in $nonPrimordialPoolObjects) {
    $poolKey = Get-AssociationKey $pool ([string]$pool.FriendlyName)
    foreach ($virtual in @(Get-VirtualDisk -StoragePool $pool)) {
        $virtualKey = Get-AssociationKey $virtual "$($virtual.FriendlyName)|$($virtual.Size)"
        $osDiskNumbers = @()
        try {
            foreach ($mappedDisk in @(Get-Disk -VirtualDisk $virtual)) {
                $osDiskNumbers += [int]$mappedDisk.Number
                $virtualDiskKeyByOsDisk[[int]$mappedDisk.Number] = $virtualKey
            }
        }
        catch {
        }

        $tierKeys = @()
        try {
            foreach ($tier in @(Get-StorageTier -VirtualDisk $virtual)) {
                $tierKey = Get-AssociationKey $tier "$($tier.FriendlyName)|$($tier.MediaType)|$($tier.Size)"
                $tierKeys += $tierKey
                if (-not $tierKeysSeen.ContainsKey($tierKey)) {
                    $tierKeysSeen[$tierKey] = $true
                    $matchingMemberKeys = @()
                    foreach ($memberKey in @($poolMembersByKey[$poolKey])) {
                        $member = $physicalObjects | Where-Object {
                            (Get-AssociationKey $_ "$($_.DeviceId)|$($_.FriendlyName)|$($_.Size)") -eq $memberKey
                        } | Select-Object -First 1
                        $tierMedia = [string]$tier.MediaType
                        $memberMedia = if ($null -eq $member) { '' } else { [string]$member.MediaType }
                        if (
                            -not [string]::IsNullOrWhiteSpace($tierMedia) -and
                            $tierMedia -ne 'Unspecified' -and
                            -not [string]::IsNullOrWhiteSpace($memberMedia) -and
                            $memberMedia -ne 'Unspecified' -and
                            $memberMedia -eq $tierMedia
                        ) {
                            $matchingMemberKeys += $memberKey
                        }
                    }
                    $storageTiers += [ordered]@{
                        AssociationKey = $tierKey
                        UniqueId = [string]$tier.UniqueId
                        ObjectId = [string]$tier.ObjectId
                        FriendlyName = [string]$tier.FriendlyName
                        MediaType = [string]$tier.MediaType
                        ResiliencySettingName = [string]$tier.ResiliencySettingName
                        Size = [long]$tier.Size
                        FootprintOnPool = [long]$tier.FootprintOnPool
                        PoolAssociationKey = $poolKey
                        VirtualDiskAssociationKey = $virtualKey
                        MemberPhysicalDiskKeys = @($matchingMemberKeys)
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
            NumberOfColumns = if ($null -eq $virtual.NumberOfColumns) { $null } else { [int]$virtual.NumberOfColumns }
            Interleave = if ($null -eq $virtual.Interleave) { $null } else { [long]$virtual.Interleave }
            Size = [long]$virtual.Size
            FootprintOnPool = [long]$virtual.FootprintOnPool
            PoolAssociationKey = $poolKey
            TierAssociationKeys = @($tierKeys)
            OsDiskNumbers = @($osDiskNumbers)
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
        Size = [long]$physical.Size
        LogicalSectorSize = [long]$physical.LogicalSectorSize
        PhysicalSectorSize = [long]$physical.PhysicalSectorSize
        HealthStatus = Get-ScalarText $physical.HealthStatus
        OperationalStatus = Get-ScalarText $physical.OperationalStatus
        CanPool = [bool]$physical.CanPool
        CannotPoolReason = Get-ScalarText $physical.CannotPoolReason
        DeviceId = if ($null -eq $physical.DeviceId) { $null } else { [int]$physical.DeviceId }
        IsBoot = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsBoot }
        IsSystem = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsSystem }
        IsPageFile = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsPageFile }
        IsCrashDump = if ($null -eq $matchingDisk) { $false } else { [bool]$matchingDisk.IsCrashDump }
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
        Size = [long]$pool.Size
        AllocatedSize = [long]$pool.AllocatedSize
        SubsystemAssociationKey = if ($subsystemObjects.Count -eq 1) {
            Get-AssociationKey $subsystemObjects[0] ([string]$subsystemObjects[0].FriendlyName)
        } else { '' }
        MemberPhysicalDiskKeys = @($poolMembersByKey[$poolKey])
    }
}

$partitions = foreach ($partition in $partitionObjects) {
    $volume = $null
    try {
        $associatedVolume = @($partition | Get-Volume | Select-Object -First 1)
        if ($associatedVolume.Count -gt 0) {
            $volume = $associatedVolume[0]
        }
    }
    catch {
    }
    [ordered]@{
        DiskNumber = [int]$partition.DiskNumber
        PartitionNumber = [int]$partition.PartitionNumber
        Guid = [string]$partition.Guid
        Type = [string]$partition.Type
        MbrType = [string]$partition.MbrType
        GptType = [string]$partition.GptType
        Offset = [long]$partition.Offset
        Size = [long]$partition.Size
        IsBoot = [bool]$partition.IsBoot
        IsSystem = [bool]$partition.IsSystem
        DriveLetter = [string]$partition.DriveLetter
        FileSystemLabel = if ($null -eq $volume) { '' } else { [string]$volume.FileSystemLabel }
        FileSystem = if ($null -eq $volume) { '' } else { [string]$volume.FileSystem }
        AllocationUnitSize = if ($null -eq $volume -or $null -eq $volume.AllocationUnitSize) { $null } else { [long]$volume.AllocationUnitSize }
        SizeRemaining = if ($null -eq $volume) { 0 } else { [long]$volume.SizeRemaining }
        HealthStatus = if ($null -eq $volume) { '' } else { Get-ScalarText $volume.HealthStatus }
        OperationalStatus = if ($null -eq $volume) { '' } else { Get-ScalarText $volume.OperationalStatus }
        Path = if ($null -eq $volume) { '' } else { [string]$volume.Path }
    }
}

$networkDisks = foreach ($networkDisk in $networkDiskObjects) {
    [ordered]@{
        DeviceId = [string]$networkDisk.DeviceID
        VolumeName = [string]$networkDisk.VolumeName
        ProviderName = [string]$networkDisk.ProviderName
        FileSystem = [string]$networkDisk.FileSystem
        Size = if ($null -eq $networkDisk.Size) { 0 } else { [long]$networkDisk.Size }
        FreeSpace = if ($null -eq $networkDisk.FreeSpace) { 0 } else { [long]$networkDisk.FreeSpace }
    }
}

$osDisks = foreach ($disk in $diskObjects) {
    [ordered]@{
        Number = [int]$disk.Number
        FriendlyName = [string]$disk.FriendlyName
        UniqueId = [string]$disk.UniqueId
        PartitionStyle = [string]$disk.PartitionStyle
        Size = [long]$disk.Size
        IsBoot = [bool]$disk.IsBoot
        IsSystem = [bool]$disk.IsSystem
        IsOffline = [bool]$disk.IsOffline
        PhysicalDiskAssociationKey = if ($diskPhysicalMap.ContainsKey([int]$disk.Number)) { [string]$diskPhysicalMap[[int]$disk.Number] } else { '' }
        VirtualDiskAssociationKey = if ($virtualDiskKeyByOsDisk.ContainsKey([int]$disk.Number)) { [string]$virtualDiskKeyByOsDisk[[int]$disk.Number] } else { '' }
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
    Warnings = @($warnings)
} | ConvertTo-Json -Depth 12 -Compress
