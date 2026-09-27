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
        function Exact-Disk($t) {
            if ($null -eq $t.DiskNumber -or [int]$t.DiskNumber -lt 0) { throw 'disk-number-missing' }
            $disk = Assert-One @(Get-Disk -Number ([uint32]$t.DiskNumber) -ErrorAction Stop) 'disk-not-unique'
            Assert-Exact $disk.UniqueId $t.OsDiskUniqueId 'disk-unique-id-changed'
            Assert-Exact $disk.Path $t.OsDiskPath 'disk-path-changed'
            return $disk
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
            return Assert-One @(Get-StorageTier -StoragePool $pool -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal)
            }) 'tier-not-unique'
        }
        function Exact-Volume($t) {
            $partition = Exact-Partition $t
            Assert-Exact $partition.Guid $t.ParentUniqueId 'volume-parent-changed'
            return Assert-One @(Get-Volume -Partition $partition -ErrorAction Stop | Where-Object {
                [string]::Equals([string]$_.UniqueId, [string]$t.UniqueId, [StringComparison]::Ordinal) -and
                [string]::Equals([string]$_.ObjectId, [string]$t.ObjectId, [StringComparison]::Ordinal)
            }) 'volume-not-unique'
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
                        @(Get-Partition -DiskNumber $disk.Number -ErrorAction SilentlyContinue).Count -ne 0) { throw 'disk-not-raw-empty' }
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
                    foreach ($existing in @(Get-Partition -DiskNumber $disk.Number -ErrorAction Stop)) {
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
                    $associatedVolumes = @(Get-Volume -Partition $partition -ErrorAction SilentlyContinue)
                    if ($associatedVolumes.Count -gt 1 -or
                        ($associatedVolumes.Count -eq 1 -and [string]$associatedVolumes[0].FileSystem -notin @('NTFS','RAW',''))) {
                        throw 'resize-file-system-not-enabled'
                    }
                    $size = [uint64]$c.SizeBytes
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
                    if ($old.Length -eq 0 -and -not [string]::IsNullOrWhiteSpace([string]$partition.DriveLetter)) {
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
                    if (@(Get-VirtualDisk -StoragePool $pool -ErrorAction Stop).Count -ne 0) { throw 'pool-already-has-virtual-disk' }
                    $tier = Assert-One @(Get-StorageTier -StoragePool $pool -ErrorAction Stop | Where-Object {
                        [string]::Equals([string]$_.UniqueId, [string]$t.RelatedUniqueId, [StringComparison]::Ordinal) -and
                        [string]::Equals([string]$_.ObjectId, [string]$t.RelatedObjectId, [StringComparison]::Ordinal)
                    }) 'tier-not-unique'
                    if ([string]$tier.MediaType -ne 'HDD') { throw 'tier-not-hdd' }
                    $invoked = $true
                    $outputObject = New-VirtualDisk -InputObject $pool -FriendlyName ([string]$c.Name) -StorageTiers @($tier) -StorageTierSizes @([uint64]$c.SizeBytes) -ProvisioningType Fixed -ErrorAction Stop
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
                    if ([int]$c.InterleaveBytes -ne 65536 -or [int]$c.DataColumns -ne 1 -or
                        @(Get-StorageTier -StoragePool $pool -ErrorAction Stop).Count -ne 0) { throw 'tier-layout-not-enabled' }
                    $invoked = $true
                    $outputObject = New-StorageTier -InputObject $pool -FriendlyName ([string]$c.Name) -MediaType HDD -ResiliencySettingName 'Simple' -NumberOfColumns 1 -Interleave 65536 -ErrorAction Stop
                    break
                }
                'DeleteTier' {
                    $tier = Exact-Tier $t
                    if (@(Get-VirtualDisk -StorageTier $tier -ErrorAction Stop).Count -ne 0) { throw 'tier-still-in-use' }
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
                ProviderJobId = $null; ProviderError = $null
            }
        } catch {
            $result = [ordered]@{
                ProviderReturned = $invoked;
                Code = $(if ($invoked) { 'provider.error-outcome-unknown' } else { 'adapter.preflight-rejected' });
                UniqueId = $null; ObjectId = $null; PartitionGuid = $null;
                DiskNumber = $null; PartitionNumber = $null; ProviderJobId = $null;
                ProviderError = [string]$_.Exception.Message
            }
        }
        [Console]::Out.WriteLine(($result | ConvertTo-Json -Compress -Depth 8))
        """;
}
