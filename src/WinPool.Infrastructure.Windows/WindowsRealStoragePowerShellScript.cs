namespace WinPool.Infrastructure.Windows;

// Fixed script shipped in the assembly. Only JSON data is read from stdin.
// No command text, executable path, CimSession, wildcard identity or script fragment
// is accepted from a plan. Returned identities are evidence for a later fresh scan.
internal static class WindowsRealStoragePowerShellScript
{
    internal const string Source = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        Set-StrictMode -Version Latest
        $invoked = $false
        $code = 'adapter.preflight-rejected'
        $outputObject = $null
        $tieredCreationInput = $null
        $capabilityEvidence = $null
        $request = $null

        function Assert-Exact($actual, $expected, $reason) {
            if ([string]::IsNullOrWhiteSpace([string]$expected) -or
                -not [string]::Equals([string]$actual, [string]$expected, [StringComparison]::Ordinal)) {
                throw $reason
            }
        }
        function Assert-One($values, $reason) {
            $items = @($values)
            if ($items.Count -ne 1) { throw $reason }
            return $items[0]
        }
        function Read-Property($object, $name) {
            if ($null -eq $object) { return $null }
            $property = $object.PSObject.Properties[$name]
            if ($null -eq $property) { return $null }
            return $property.Value
        }
        function Exact-Subsystem($t) {
            return Assert-One @(Get-StorageSubSystem -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.StorageSubsystemUniqueId, [StringComparison]::Ordinal)
            }) 'subsystem-not-unique'
        }
        function Test-UnassignedDriveLetter($value) {
            return ($null -eq $value) -or
                ($value -is [char] -and [int]$value -eq 0) -or
                ($value -is [string] -and $value.Length -eq 0)
        }
        function Test-TierCreationSize($range, [long]$size) {
            if ($null -eq $range -or $size -le 0) { throw 'tier-creation-size-method-failed' }
            $returnProperty = $range.PSObject.Properties['ReturnValue']
            $sizesProperty = $range.PSObject.Properties['SupportedSizes']
            if ($null -eq $returnProperty -or $returnProperty.Value -isnot [uint32] -or
                $returnProperty.Value -ne 0 -or $null -eq $sizesProperty) { throw 'tier-creation-size-method-failed' }
            $rawSizes = $sizesProperty.Value
            if ($null -ne $rawSizes -and $rawSizes.GetType() -ne [uint64[]]) { throw 'tier-creation-size-enumeration-invalid' }
            $sizes = New-Object 'System.Collections.Generic.HashSet[long]'
            foreach ($value in $rawSizes) {
                if ($value -eq 0 -or $value -gt [long]::MaxValue -or -not $sizes.Add([long]$value)) {
                    throw 'tier-creation-size-enumeration-invalid'
                }
            }
            $rangeValues = @()
            foreach ($name in @('TierSizeMin','TierSizeMax','TierSizeDivisor')) {
                $property = $range.PSObject.Properties[$name]
                if ($null -eq $property) { throw 'tier-creation-size-range-unavailable' }
                if ($null -eq $property.Value) { $rangeValues += [long]0 }
                elseif ($property.Value -isnot [uint64] -or $property.Value -gt [long]::MaxValue) {
                    throw 'tier-creation-size-range-invalid'
                } else { $rangeValues += [long]$property.Value }
            }
            $minimum = $rangeValues[0]; $maximum = $rangeValues[1]; $increment = $rangeValues[2]
            if ($sizes.Count -gt 0) {
                if ($minimum -gt 0 -and $maximum -gt 0 -and $increment -gt 0) {
                    foreach ($supported in $sizes) {
                        if ($maximum -lt $minimum -or $supported -lt $minimum -or $supported -gt $maximum -or
                            ($supported - $minimum) % $increment -ne 0) { throw 'tier-creation-size-evidence-inconsistent' }
                    }
                }
                return $sizes.Contains($size)
            }
            if ($minimum -le 0 -or $maximum -lt $minimum -or $increment -le 0) { throw 'tier-creation-size-range-invalid' }
            return $size -ge $minimum -and $size -le $maximum -and ($size - $minimum) % $increment -eq 0
        }
        function Exact-Disk($t) {
            if ($null -eq $t.DiskNumber -or [int]$t.DiskNumber -lt 0) { throw 'disk-number-missing' }
            $disk = Assert-One @(Get-Disk -Number ([uint32]$t.DiskNumber) -ErrorAction Stop) 'disk-not-unique'
            Assert-Exact $disk.UniqueId $t.OsDiskUniqueId 'disk-unique-id-changed'
            Assert-Exact $disk.Path $t.OsDiskPath 'disk-path-changed'
            return $disk
        }
        function Get-ExactDiskPartitions($disk) {
            # Get-Partition -DiskNumber can throw ObjectNotFound for a valid GPT
            # disk with no partitions. Enumerating the provider class distinguishes
            # that empty result from an actual read failure.
            $byDiskNumber = @(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName 'MSFT_Partition' -ErrorAction Stop | Where-Object {
                $null -ne $_.DiskNumber -and [uint32]$_.DiskNumber -eq [uint32]$disk.Number
            })
            foreach ($partition in $byDiskNumber) {
                if (-not [string]::Equals([string]$partition.DiskId, [string]$disk.Path, [StringComparison]::Ordinal)) {
                    throw 'partition-disk-identity-ambiguous'
                }
            }
            $partitions = @($byDiskNumber | Where-Object {
                [string]::Equals([string]$_.DiskId, [string]$disk.Path, [StringComparison]::Ordinal) -and
                [uint32]$_.DiskNumber -eq [uint32]$disk.Number
            })
            $partitionNumbers = @($partitions | ForEach-Object { [uint32]$_.PartitionNumber })
            $partitionGuids = @($partitions | ForEach-Object { ([Guid]$_.Guid).ToString('D') })
            if (@($partitionNumbers | Sort-Object -Unique).Count -ne $partitionNumbers.Count -or
                @($partitionGuids | Sort-Object -Unique).Count -ne $partitionGuids.Count) {
                throw 'disk-partition-identity-ambiguous'
            }

            $ranges = @()
            foreach ($partition in $partitions) {
                $number = [uint32]$partition.PartitionNumber
                $guid = [Guid]$partition.Guid
                $offset = [decimal]$partition.Offset
                $size = [decimal]$partition.Size
                $end = $offset + $size
                if ($number -eq 0 -or $guid -eq [Guid]::Empty -or $offset -lt 0 -or
                    $size -le 0 -or $end -gt [decimal]$disk.Size) {
                    throw 'disk-partition-geometry-invalid'
                }
                foreach ($range in $ranges) {
                    if ($offset -lt $range.End -and $range.Start -lt $end) { throw 'partition-overlap' }
                }
                $ranges += [pscustomobject]@{ Start = $offset; End = $end }
            }
            return $partitions
        }
        function Exact-Partition($t) {
            $disk = Exact-Disk $t
            if ($null -eq $t.PartitionNumber -or [int]$t.PartitionNumber -le 0) { throw 'partition-number-missing' }
            $partition = Assert-One @(Get-Partition -DiskNumber ([uint32]$t.DiskNumber) -PartitionNumber ([uint32]$t.PartitionNumber) -ErrorAction Stop) 'partition-not-unique'
            if (-not [string]::Equals([string]$partition.Guid, [string]$t.PartitionGuid, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'partition-guid-changed'
            }
            if ([uint64]$partition.Offset -ne [uint64]$t.OffsetBytes -or [uint64]$partition.Size -ne [uint64]$t.SizeBytes) {
                throw 'partition-geometry-changed'
            }
            if ([Guid]$partition.GptType -ne [Guid]$t.PartitionTypeGuid) { throw 'partition-type-changed' }
            if ([string]$t.Kind -eq 'Volume') {
                Assert-Exact $partition.Guid $t.ParentUniqueId 'volume-parent-changed'
            } else {
                Assert-Exact $disk.UniqueId $t.ParentUniqueId 'partition-parent-changed'
            }
            return $partition
        }
        function Exact-Physical($t) {
            $subsystem = Exact-Subsystem $t
            $physical = Assert-One @(Get-PhysicalDisk -StorageSubsystem $subsystem -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.SerialNumber, [string]$t.SerialNumber, [StringComparison]::Ordinal)
            }) 'physical-disk-not-unique'
            return $physical
        }
        function Exact-Pool($t) {
            $subsystem = Exact-Subsystem $t
            Assert-Exact $subsystem.UniqueId $t.ParentUniqueId 'pool-parent-changed'
            $pool = Assert-One @(Get-StoragePool -StorageSubSystem $subsystem -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal) -and
                -not $_.IsPrimordial
            }) 'pool-not-unique'
            $members = @(Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop)
            if ($members.Count -ne 1) { throw 'pool-member-count-changed' }
            Assert-Exact $members[0].UniqueId $t.PhysicalMemberUniqueId 'pool-member-changed'
            return $pool
        }
        function Exact-VirtualDisk($t) {
            $subsystem = Exact-Subsystem $t
            $pool = Assert-One @(Get-StoragePool -StorageSubSystem $subsystem -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.ParentUniqueId, [StringComparison]::Ordinal) -and -not $_.IsPrimordial
            }) 'virtual-disk-pool-not-unique'
            $members = @(Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop)
            if ($members.Count -ne 1) { throw 'pool-member-count-changed' }
            Assert-Exact $members[0].UniqueId $t.PhysicalMemberUniqueId 'pool-member-changed'
            return Assert-One @(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal)
            }) 'virtual-disk-not-unique'
        }
        function Exact-Tier($t) {
            $subsystem = Exact-Subsystem $t
            $pool = Assert-One @(Get-StoragePool -StorageSubSystem $subsystem -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.ParentUniqueId, [StringComparison]::Ordinal) -and -not $_.IsPrimordial
            }) 'tier-pool-not-unique'
            $members = @(Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop)
            if ($members.Count -ne 1) { throw 'pool-member-count-changed' }
            Assert-Exact $members[0].UniqueId $t.PhysicalMemberUniqueId 'pool-member-changed'
            Assert-Exact $members[0].ObjectId $t.PhysicalMemberObjectId 'pool-member-object-id-changed'
            Assert-Exact $members[0].SerialNumber $t.SerialNumber 'pool-member-serial-changed'
            $templates = @(Get-StorageTier -StoragePool $pool -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal)
            })
            if ($templates.Count -gt 1) { throw 'tier-not-unique' }
            if ([string]::IsNullOrEmpty([string]$t.RelatedUniqueId) -and
                [string]::IsNullOrEmpty([string]$t.RelatedObjectId)) {
                $template = Assert-One $templates 'tier-not-unique'
                if (@(Get-VirtualDisk -StorageTier $template -ErrorAction Stop).Count -ne 0) { throw 'tier-template-has-virtual-disk' }
                return $template
            }
            if ($templates.Count -ne 0) { throw 'tier-instance-is-pool-template' }
            $virtual = Assert-One @(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop) 'tier-virtual-disk-not-unique'
            Assert-Exact $virtual.UniqueId $t.RelatedUniqueId 'tier-virtual-disk-changed'
            Assert-Exact $virtual.ObjectId $t.RelatedObjectId 'tier-virtual-disk-object-id-changed'
            $parent = Assert-One @(Get-StoragePool -VirtualDisk $virtual -ErrorAction Stop) 'tier-virtual-disk-pool-not-unique'
            Assert-Exact $parent.UniqueId $pool.UniqueId 'tier-virtual-disk-pool-changed'
            Assert-Exact $parent.ObjectId $pool.ObjectId 'tier-virtual-disk-pool-object-id-changed'
            $tier = Assert-One @(Get-StorageTier -VirtualDisk $virtual -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal)
            }) 'tier-not-unique'
            $owner = Assert-One @(Get-VirtualDisk -StorageTier $tier -ErrorAction Stop) 'tier-owner-not-unique'
            Assert-Exact $owner.UniqueId $virtual.UniqueId 'tier-owner-changed'
            Assert-Exact $owner.ObjectId $virtual.ObjectId 'tier-owner-object-id-changed'
            # The same exact extent contract is used by the read-only collector.
            $extentResult = Invoke-CimMethod -InputObject $tier -MethodName GetPhysicalExtent -ErrorAction Stop
            if ($null -eq (Read-Property $extentResult 'ReturnValue') -or
                [uint32](Read-Property $extentResult 'ReturnValue') -ne 0) { throw 'tier-extent-method-failed' }
            $extents = @((Read-Property $extentResult 'PhysicalExtents'))
            if ($extents.Count -eq 0) { throw 'tier-extent-members-missing' }
            foreach ($extent in $extents) {
                $extentSize = Read-Property $extent 'Size'
                if ($extentSize -isnot [uint64] -or $extentSize -eq 0) { throw 'tier-extent-size-unknown' }
                Assert-Exact (Read-Property $extent 'StorageTierUniqueId') $tier.UniqueId 'tier-extent-tier-changed'
                Assert-Exact (Read-Property $extent 'VirtualDiskUniqueId') $virtual.UniqueId 'tier-extent-owner-changed'
                Assert-Exact (Read-Property $extent 'PhysicalDiskUniqueId') $members[0].UniqueId 'tier-extent-member-changed'
            }
            return $tier
        }
        function Exact-Volume($t) {
            $partition = Exact-Partition $t
            Assert-Exact $partition.Guid $t.ParentUniqueId 'volume-parent-changed'
            return Assert-One @(Get-Volume -Partition $partition -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal)
            }) 'volume-not-unique'
        }
        function Require-Refs($partition, $t, [ref]$evidence) {
            $volume = Assert-One @(Get-Volume -Partition $partition -ErrorAction Stop) 'refs-volume-not-unique'
            Assert-Exact $volume.UniqueId $t.RelatedUniqueId 'refs-volume-unique-id-changed'
            Assert-Exact $volume.ObjectId $t.RelatedObjectId 'refs-volume-object-id-changed'
            $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            if ([string]$os.Caption -notmatch 'Pro for Workstations|Enterprise|Server') { throw 'refs-sku-not-enabled' }
            $formats = Invoke-CimMethod -InputObject $volume -MethodName GetSupportedFileSystems -ErrorAction Stop
            $clusters = Invoke-CimMethod -InputObject $volume -MethodName GetSupportedClusterSizes -Arguments @{ FileSystem = 'ReFS' } -ErrorAction Stop
            $evidence.Value = [ordered]@{
                VolumeUniqueId = [string]$volume.UniqueId; VolumeObjectId = [string]$volume.ObjectId;
                PartitionGuid = [string]$partition.Guid; OperatingSystemCaption = [string]$os.Caption;
                FileSystems = $formats; ReFsClusterSizes = $clusters
            }
            if ((Read-Property $formats 'ReturnValue') -ne 0 -or (Read-Property $clusters 'ReturnValue') -ne 0 -or
                'ReFS' -notin @(Read-Property $formats 'SupportedFileSystems') -or
                65536 -notin @(Read-Property $clusters 'SupportedClusterSizes')) { throw 'refs-live-capability-not-verified' }
            return $volume
        }
        function Require-TierCapability($t, $field, [ref]$evidence) {
            $subsystem = Exact-Subsystem $t
            Assert-Exact $subsystem.ObjectId $t.StorageSubsystemObjectId 'tier-subsystem-object-id-changed'
            $member = Assert-One @(Get-PhysicalDisk -StorageSubsystem $subsystem -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.PhysicalMemberUniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.PhysicalMemberObjectId, [StringComparison]::Ordinal)
            }) 'tier-physical-member-not-exact'
            Assert-Exact $member.SerialNumber $t.SerialNumber 'tier-physical-serial-changed'
            $evidence.Value = [ordered]@{
                SubsystemUniqueId = [string]$subsystem.UniqueId; SubsystemObjectId = [string]$subsystem.ObjectId;
                RequiredField = $field; RequiredValue = (Read-Property $subsystem $field);
                PhysicalDisksPerStoragePoolMin = (Read-Property $subsystem 'PhysicalDisksPerStoragePoolMin')
                PhysicalMemberUniqueId = [string]$member.UniqueId; PhysicalMemberObjectId = [string]$member.ObjectId
            }
            if ((Read-Property $subsystem $field) -ne $true -or
                (Read-Property $subsystem 'PhysicalDisksPerStoragePoolMin') -ne 1) { throw 'single-hdd-tier-capability-not-verified' }
        }

        try {
            $request = [Console]::In.ReadToEnd() | ConvertFrom-Json -ErrorAction Stop
            if ($null -eq $request -or $null -eq $request.Target -or $null -eq $request.Command -or
                [string]::IsNullOrWhiteSpace([string]$request.Target.ExpectedFingerprint)) {
                throw 'invalid-fixed-payload'
            }
            Import-Module Storage -ErrorAction Stop
            $t = $request.Target
            $c = $request.Command
            switch ([string]$request.CommandKind) {
                'SetDiskOnline' {
                    $disk = Exact-Disk $t
                    if (-not [bool]$c.Online -and ($disk.IsBoot -or $disk.IsSystem)) { throw 'protected-os-disk' }
                    $invoked = $true
                    Set-Disk -InputObject $disk -IsOffline (-not [bool]$c.Online) -ErrorAction Stop | Out-Null
                    $outputObject = $disk
                    break
                }
                'InitializeGpt' {
                    $disk = Exact-Disk $t
                    if ($disk.PartitionStyle -ne 'RAW' -or $disk.IsBoot -or $disk.IsSystem -or $disk.IsReadOnly -or $disk.IsOffline -or
                        @(Get-ExactDiskPartitions $disk).Count -ne 0) { throw 'disk-not-raw-empty' }
                    $invoked = $true
                    $outputObject = Initialize-Disk -InputObject $disk -PartitionStyle GPT -PassThru -ErrorAction Stop
                    break
                }
                'ClearDisk' {
                    $disk = Exact-Disk $t
                    if ($disk.IsBoot -or $disk.IsSystem -or $disk.IsReadOnly -or $disk.IsOffline -or [bool]$c.RemoveOem) { throw 'disk-clear-protected' }
                    $invoked = $true
                    Clear-Disk -InputObject $disk -RemoveData -Confirm:$false -ErrorAction Stop | Out-Null
                    $outputObject = $disk
                    break
                }
                'CreatePartition' {
                    $disk = Exact-Disk $t
                    if ($disk.PartitionStyle -ne 'GPT' -or $disk.IsOffline -or $disk.IsReadOnly) { throw 'disk-not-online-gpt' }
                    $offset = [uint64]$c.OffsetBytes
                    $size = [uint64]$c.SizeBytes
                    if ($offset -lt 1048576 -or $size -eq 0 -or
                        ([decimal]$offset + [decimal]$size + 1048576) -gt [decimal]$disk.Size) { throw 'partition-out-of-disk' }
                    foreach ($existing in @(Get-ExactDiskPartitions $disk)) {
                        if ([decimal]$offset -lt ([decimal]$existing.Offset + [decimal]$existing.Size) -and
                            [decimal]$existing.Offset -lt ([decimal]$offset + [decimal]$size)) { throw 'partition-overlap' }
                    }
                    $gpt = switch ([string]$c.Role) {
                        'BasicData' { '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' }
                        'Efi' { '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}' }
                        'Msr' { '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' }
                        'Recovery' { '{de94bba4-06d1-4d40-a16a-bfd50179d6ac}' }
                        default { throw 'partition-role-unsupported' }
                    }
                    $invoked = $true
                    $outputObject = New-Partition -InputObject $disk -Offset $offset -Size $size -GptType $gpt -ErrorAction Stop
                    break
                }
                'DeletePartition' {
                    $partition = Exact-Partition $t
                    if ($partition.IsBoot -or $partition.IsSystem) { throw 'protected-partition' }
                    $invoked = $true
                    Remove-Partition -InputObject $partition -Confirm:$false -ErrorAction Stop | Out-Null
                    $outputObject = $partition
                    break
                }
                'ResizePartition' {
                    $partition = Exact-Partition $t
                    if ($partition.IsBoot -or $partition.IsSystem) { throw 'protected-partition' }
                    if ([Guid]$partition.GptType -ne [Guid]'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7') { throw 'resize-non-data-partition' }
                    # Successful exact CIM association enumeration distinguishes
                    # an unformatted partition with no volume from a failed read.
                    $associatedVolumes = @(Get-CimAssociatedInstance -InputObject $partition -Namespace 'root/Microsoft/Windows/Storage' -Association 'MSFT_PartitionToVolume' -ResultClassName 'MSFT_Volume' -ErrorAction Stop)
                    if ($associatedVolumes.Count -gt 1 -or
                        ($associatedVolumes.Count -eq 1 -and [string]$associatedVolumes[0].FileSystem -notin @('NTFS','RAW','ReFS',''))) {
                        throw 'resize-file-system-not-enabled'
                    }
                    $size = [uint64]$c.SizeBytes
                    if ($associatedVolumes.Count -eq 1 -and [string]$associatedVolumes[0].FileSystem -eq 'ReFS') {
                        if ($size -le [uint64]$partition.Size) { throw 'refs-shrink-not-enabled' }
                        $null = Require-Refs $partition $t ([ref]$capabilityEvidence)
                    }
                    $range = Get-PartitionSupportedSize -InputObject $partition -ErrorAction Stop
                    if ($size -lt [uint64]$range.SizeMin -or $size -gt [uint64]$range.SizeMax) { throw 'partition-size-unsupported' }
                    $invoked = $true
                    Resize-Partition -InputObject $partition -Size $size -ErrorAction Stop | Out-Null
                    $outputObject = $partition
                    break
                }
                'FormatVolume' {
                    $partition = Exact-Partition $t
                    if ($partition.IsBoot -or $partition.IsSystem) { throw 'protected-partition' }
                    $fs = [string]$c.FileSystem
                    $cluster = [uint32]$c.ClusterBytes
                    $role = [Guid]$partition.GptType
                    $basic = [Guid]'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7'
                    $efi = [Guid]'c12a7328-f81f-11d2-ba4b-00a0c93ec93b'
                    $recovery = [Guid]'de94bba4-06d1-4d40-a16a-bfd50179d6ac'
                    $validFormat = ($role -eq $basic -and $fs -in @('Ntfs','ExFat','ReFs') -and $cluster -eq 65536 -and
                        -not ($fs -eq 'ReFs' -and [bool]$c.Full)) -or
                        ($role -eq $efi -and [bool]$t.CreatedInThisPlan -and $fs -eq 'Fat32' -and $cluster -eq 4096 -and -not [bool]$c.Full) -or
                        ($role -eq $recovery -and [bool]$t.CreatedInThisPlan -and $fs -eq 'Ntfs' -and $cluster -eq 4096 -and -not [bool]$c.Full)
                    if (-not $validFormat) { throw 'format-parameters-unsupported' }
                    if ($fs -eq 'ReFs') {
                        if ([bool]$t.CreatedInThisPlan -or $null -eq $c.Partition.Existing) { throw 'refs-existing-volume-required' }
                        $null = Require-Refs $partition $t ([ref]$capabilityEvidence)
                    }
                    $formatArgs = @{ Partition=$partition; FileSystem=$fs; AllocationUnitSize=$cluster; Full=[bool]$c.Full; ErrorAction='Stop' }
                    if ($null -ne $c.Label) { $formatArgs.NewFileSystemLabel = [string]$c.Label }
                    $invoked = $true
                    $outputObject = Format-Volume @formatArgs
                    break
                }
                'SetDriveLetter' {
                    $partition = Exact-Partition $t
                    if ([Guid]$partition.GptType -ne [Guid]'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7') {
                        throw 'drive-letter-non-data-partition'
                    }
                    $old = [string]$c.PreviousLetter
                    $new = [string]$c.NewLetter
                    if (($old.Length -eq 0 -and $new.Length -eq 0) -or
                        ($old.Length -gt 0 -and $old -notmatch '^[D-Z]$') -or
                        ($new.Length -gt 0 -and $new -notmatch '^[D-Z]$') -or
                        ($old.Length -gt 0 -and $old -eq $new)) { throw 'drive-letter-parameters-invalid' }
                    if ($old.Length -gt 0 -and -not [string]::Equals([string]$partition.DriveLetter, $old, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'old-drive-letter-changed'
                    }
                    if ($old.Length -eq 0 -and -not (Test-UnassignedDriveLetter $partition.DriveLetter)) {
                        throw 'old-drive-letter-changed'
                    }
                    if ($new.Length -gt 0 -and @(Get-Partition -ErrorAction Stop | Where-Object { [string]::Equals([string]$_.DriveLetter, $new, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                        throw 'new-drive-letter-in-use'
                    }
                    if ($new.Length -gt 0 -and @(Get-PSDrive -Name $new -ErrorAction SilentlyContinue).Count -gt 0) {
                        throw 'new-drive-letter-mapped'
                    }
                    $invoked = $true
                    if ($old.Length -eq 0) {
                        Add-PartitionAccessPath -InputObject $partition -AccessPath ($new + ':') -ErrorAction Stop | Out-Null
                    } elseif ($new.Length -eq 0) {
                        Remove-PartitionAccessPath -InputObject $partition -AccessPath ($old + ':') -Confirm:$false -ErrorAction Stop | Out-Null
                    } else {
                        Set-Partition -InputObject $partition -NewDriveLetter ([char]$new) -ErrorAction Stop | Out-Null
                    }
                    $outputObject = $partition
                    break
                }
                'RenameVolume' {
                    $volume = Exact-Volume $t
                    $invoked = $true
                    Set-Volume -InputObject $volume -NewFileSystemLabel ([string]$c.Label) -ErrorAction Stop | Out-Null
                    $outputObject = $volume
                    break
                }
                'CreatePool' {
                    $physical = Exact-Physical $t
                    $subsystem = Exact-Subsystem $t
                    if (-not $physical.CanPool) { throw 'physical-disk-cannot-pool' }
                    $invoked = $true
                    $outputObject = New-StoragePool -InputObject $subsystem -FriendlyName ([string]$c.Name) -PhysicalDisks @($physical) -ErrorAction Stop
                    break
                }
                'DeletePool' {
                    $pool = Exact-Pool $t
                    if (@(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop).Count -ne 0 -or
                        @(Get-StorageTier -StoragePool $pool -ErrorAction Stop).Count -ne 0) { throw 'pool-still-has-children' }
                    $invoked = $true
                    Remove-StoragePool -InputObject $pool -Confirm:$false -ErrorAction Stop | Out-Null
                    $outputObject = $pool
                    break
                }
                'RenamePool' {
                    $pool = Exact-Pool $t
                    $invoked = $true
                    Set-StoragePool -InputObject $pool -NewFriendlyName ([string]$c.Name) -ErrorAction Stop | Out-Null
                    $outputObject = $pool
                    break
                }
                'CreateVirtualDisk' {
                    $pool = Exact-Pool $t
                    if (@(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop).Count -ne 0 -or
                        [int]$c.InterleaveBytes -ne 65536 -or [int]$c.DataColumns -ne 1) { throw 'vd-layout-not-enabled' }
                    $invoked = $true
                    $outputObject = New-VirtualDisk -InputObject $pool -FriendlyName ([string]$c.Name) -Size ([uint64]$c.SizeBytes) -ResiliencySettingName 'Simple' -ProvisioningType Fixed -NumberOfColumns 1 -Interleave 65536 -ErrorAction Stop
                    break
                }
                'CreateTieredVirtualDisk' {
                    $pool = Exact-Pool $t
                    Require-TierCapability $t 'SupportsStorageTieredVirtualDiskCreation' ([ref]$capabilityEvidence)
                    if (@(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop).Count -ne 0) { throw 'tiered-pool-not-empty' }
                    $tier = Assert-One @(Get-StorageTier -StoragePool $pool -ErrorAction Stop | Where-Object {
                        [string]::Equals([string]$_.UniqueId, [string]$t.RelatedUniqueId, [StringComparison]::Ordinal) -and
                        [string]::Equals([string]$_.ObjectId, [string]$t.RelatedObjectId, [StringComparison]::Ordinal)
                    }) 'tier-not-unique'
                    if ([string]$tier.MediaType -ne 'HDD' -or [string]$tier.ResiliencySettingName -ne 'Simple' -or
                        [uint64]$tier.Interleave -ne 65536 -or [uint16]$tier.NumberOfColumns -ne 1 -or
                        @(Get-VirtualDisk -StorageTier $tier -ErrorAction Stop).Count -ne 0) { throw 'tier-layout-not-enabled' }
                    $range = Invoke-CimMethod -InputObject $tier -MethodName GetSupportedSize -Arguments @{ ResiliencySettingName = 'Simple' } -ErrorAction Stop
                    $capabilityEvidence['CreationSize'] = $range
                    if (-not (Test-TierCreationSize $range ([long]$c.SizeBytes))) { throw 'tier-creation-size-not-supported' }
                    $tieredCreationInput = [ordered]@{
                        TemplateUniqueId = [string]$tier.UniqueId; TemplateObjectId = [string]$tier.ObjectId;
                        PoolUniqueId = [string]$pool.UniqueId; PhysicalMemberUniqueId = [string]$t.PhysicalMemberUniqueId;
                        MediaType = [string]$tier.MediaType; ResiliencySettingName = 'Simple'; ProvisioningType = 'Fixed';
                        NumberOfColumns = 1; Interleave = 65536; SizeBytes = [long]$c.SizeBytes
                    }
                    $invoked = $true
                    $outputObject = New-VirtualDisk -InputObject $pool -FriendlyName ([string]$c.Name) -StorageTiers @($tier) -StorageTierSizes @([uint64]$c.SizeBytes) -ResiliencySettingName Simple -ProvisioningType Fixed -NumberOfColumns 1 -Interleave 65536 -ErrorAction Stop
                    break
                }
                'DeleteVirtualDisk' {
                    $vd = Exact-VirtualDisk $t
                    $invoked = $true
                    Remove-VirtualDisk -InputObject $vd -Confirm:$false -ErrorAction Stop | Out-Null
                    $outputObject = $vd
                    break
                }
                'ResizeVirtualDisk' {
                    $vd = Exact-VirtualDisk $t
                    $invoked = $true
                    Resize-VirtualDisk -InputObject $vd -Size ([uint64]$c.SizeBytes) -ErrorAction Stop | Out-Null
                    $outputObject = $vd
                    break
                }
                'RenameVirtualDisk' {
                    $vd = Exact-VirtualDisk $t
                    $invoked = $true
                    Set-VirtualDisk -InputObject $vd -NewFriendlyName ([string]$c.Name) -ErrorAction Stop | Out-Null
                    $outputObject = $vd
                    break
                }
                'CreateTier' {
                    $pool = Exact-Pool $t
                    Require-TierCapability $t 'SupportsStorageTierCreation' ([ref]$capabilityEvidence)
                    $member = Assert-One @(Get-PhysicalDisk -StoragePool $pool -ErrorAction Stop) 'tier-member-count-changed'
                    if ([int]$c.InterleaveBytes -ne 65536 -or [int]$c.DataColumns -ne 1 -or
                        [string]$member.MediaType -ne 'HDD' -or
                        @(Get-StorageTier -StoragePool $pool -ErrorAction Stop).Count -ne 0 -or
                        @(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop).Count -ne 0) { throw 'tier-layout-not-enabled' }
                    $invoked = $true
                    $outputObject = New-StorageTier -InputObject $pool -FriendlyName ([string]$c.Name) -MediaType HDD -ResiliencySettingName 'Simple' -NumberOfColumns 1 -Interleave 65536 -ErrorAction Stop
                    break
                }
                'DeleteTier' {
                    $tier = Exact-Tier $t
                    Require-TierCapability $t 'SupportsStorageTierDeletion' ([ref]$capabilityEvidence)
                    if (@(Get-VirtualDisk -StorageTier $tier -ErrorAction Stop).Count -ne 0) { throw 'tier-still-in-use' }
                    $pool = Assert-One @(Get-StoragePool -StorageSubSystem (Exact-Subsystem $t) -ErrorAction Stop | Where-Object {
                        [string]::Equals([string]$_.UniqueId, [string]$t.ParentUniqueId, [StringComparison]::Ordinal) -and -not $_.IsPrimordial
                    }) 'tier-pool-not-unique'
                    if (@(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop).Count -ne 0) { throw 'remove-pool-virtual-disk-before-template' }
                    $invoked = $true
                    Remove-StorageTier -InputObject $tier -Confirm:$false -ErrorAction Stop | Out-Null
                    $outputObject = $tier
                    break
                }
                'ResizeTier' {
                    $tier = Exact-Tier $t
                    $invoked = $true
                    Resize-StorageTier -InputObject $tier -Size ([uint64]$c.SizeBytes) -ErrorAction Stop | Out-Null
                    $outputObject = $tier
                    break
                }
                'RenameTier' {
                    $tier = Exact-Tier $t
                    Require-TierCapability $t 'SupportsStorageTierFriendlyNameModification' ([ref]$capabilityEvidence)
                    $invoked = $true
                    Set-StorageTier -InputObject $tier -NewFriendlyName ([string]$c.Name) -ErrorAction Stop | Out-Null
                    $outputObject = $tier
                    break
                }
                default { throw 'command-not-in-closed-adapter' }
            }
            $code = 'provider.returned'
            $result = [ordered]@{
                ProviderReturned = $invoked; Code = $code;
                UniqueId = [string](Read-Property $outputObject 'UniqueId');
                ObjectId = [string](Read-Property $outputObject 'ObjectId');
                PartitionGuid = [string](Read-Property $outputObject 'Guid');
                DiskNumber = $(if ($null -ne (Read-Property $outputObject 'DiskNumber')) { [int](Read-Property $outputObject 'DiskNumber') } elseif ($null -ne (Read-Property $outputObject 'Number') -and $request.CommandKind -in @('InitializeGpt','SetDiskOnline','ClearDisk')) { [int](Read-Property $outputObject 'Number') } else { $null });
                PartitionNumber = $(if ($null -ne (Read-Property $outputObject 'PartitionNumber')) { [int](Read-Property $outputObject 'PartitionNumber') } else { $null });
                ProviderJobId = $null; ProviderError = $null; TieredCreationInput = $tieredCreationInput;
                LiveCapabilityEvidence = $capabilityEvidence
            }
        } catch {
            $result = [ordered]@{
                ProviderReturned = $invoked;
                Code = $(if ($invoked) { 'provider.error-outcome-unknown' } else { 'adapter.preflight-rejected' });
                UniqueId = $null; ObjectId = $null; PartitionGuid = $null;
                DiskNumber = $null; PartitionNumber = $null; ProviderJobId = $null;
                ProviderError = [string]$_.Exception.Message; LiveCapabilityEvidence = $capabilityEvidence
            }
        }
        [Console]::Out.WriteLine(($result | ConvertTo-Json -Compress -Depth 8))
        """;
}
