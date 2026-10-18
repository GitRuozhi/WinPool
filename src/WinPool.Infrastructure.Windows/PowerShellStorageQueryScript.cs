namespace WinPool.Infrastructure.Windows;

internal static class PowerShellStorageQueryScript
{
    internal const string Source = """
param([string]$kind,$p)
$ErrorActionPreference='Stop'
$ns='root/Microsoft/Windows/Storage'
function Exact($class,$unique,$object) {
    $matches=@(Get-CimInstance -Namespace $ns -ClassName $class -ErrorAction Stop | Where-Object {
        [string]::Equals([string]$_.UniqueId,[string]$unique,[StringComparison]::Ordinal) -and
        [string]::Equals([string]$_.ObjectId,[string]$object,[StringComparison]::Ordinal)
    })
    if ($matches.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$unique) -or [string]::IsNullOrWhiteSpace([string]$object)) { throw 'exact-provider-identity-required' }
    return $matches[0]
}
function Method($instance,$name,$arguments) {
    $result=Invoke-CimMethod -InputObject $instance -MethodName $name -Arguments $arguments -ErrorAction Stop
    $output=@{}
    foreach($property in $result.CimInstanceProperties) { $output[$property.Name]=$property.Value }
    return @{ Status=$(if($result.ReturnValue -eq 0){'returned'}else{'provider_error'}); ReturnValue=$result.ReturnValue; Output=$output; Error=$null }
}
switch($kind) {
    'MachineIdentity' {
        $result=@{ HardwareUuids=@(Get-CimInstance -Namespace root/CIMV2 -ClassName Win32_ComputerSystemProduct -ErrorAction Stop | ForEach-Object { [string]$_.UUID }) }
    }
    'StorageJobs' {
        $jobs=@(Get-CimInstance -Namespace $ns -ClassName MSFT_StorageJob -ErrorAction Stop | ForEach-Object {
            if ([string]::IsNullOrWhiteSpace([string]$_.UniqueId) -or [string]::IsNullOrWhiteSpace([string]$_.ObjectId)) { throw 'storage-job-identity-unavailable' }
            # The Storage module adapts JobState to a display name such as Completed.
            # MSFT_StorageJob's underlying CIM property is UInt16; retain unknown numeric states.
            $stateProperty=$_.CimInstanceProperties['JobState']
            if ($null -eq $stateProperty -or $stateProperty.Value -isnot [uint16]) { throw 'storage-job-raw-state-unavailable' }
            @{ UniqueId=[string]$_.UniqueId; ObjectId=[string]$_.ObjectId; JobState=$stateProperty.Value }
        })
        $result=@{ ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); Jobs=$jobs }
    }
    'PartitionSize' {
        $t=$p.Target
        $parts=@(Get-CimInstance -Namespace $ns -ClassName MSFT_Partition -Filter ('DiskNumber = '+[uint32]$t.DiskNumber+' AND PartitionNumber = '+[uint32]$t.PartitionNumber) -ErrorAction Stop |
            Where-Object { [guid]$_.Guid -eq [guid]$t.PartitionGuid -and [uint64]$_.Offset -eq [uint64]$t.OffsetBytes -and [uint64]$_.Size -eq [uint64]$t.SizeBytes })
        if($parts.Count -ne 1) { throw 'partition-identity-or-geometry-changed' }
        $range=Invoke-CimMethod -InputObject $parts[0] -MethodName GetSupportedSize -ErrorAction Stop
        if($range.ReturnValue -ne 0) { throw 'partition-size-provider-error' }
        $result=@{ MinimumBytes=$range.SizeMin; MaximumBytes=$range.SizeMax }
    }
    {$_ -in 'VirtualDiskSize','VirtualDiskProbe'} {
        $t=$p.Target; $pool=Exact 'MSFT_StoragePool' $t.UniqueId $t.ObjectId
        if($pool.IsPrimordial -ne $false -or $pool.IsReadOnly -ne $false -or $pool.IsClustered -ne $false) { throw 'concrete-writable-unclustered-pool-required' }
        $layout='Simple'; if($null -ne $t.Layout) { $layout=[string]$t.Layout.ResiliencySettingName }
        $range=Invoke-CimMethod -InputObject $pool -MethodName GetSupportedSize -Arguments @{ResiliencySettingName=$layout} -ErrorAction Stop
        if($range.ReturnValue -ne 0) { throw 'virtual-disk-size-provider-error' }
        if($kind -eq 'VirtualDiskProbe') { $result=@{PoolUniqueId=[string]$pool.UniqueId; PoolObjectId=[string]$pool.ObjectId; ResiliencySettingName=$layout; ReturnValue=[uint32]$range.ReturnValue; ObservedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')} }
        else { $result=@{MinimumBytes=$range.VirtualDiskSizeMin;MaximumBytes=$range.VirtualDiskSizeMax;DivisorBytes=$range.VirtualDiskSizeDivisor;EnumeratedSizes=@($range.SupportedSizes | Where-Object {$null -ne $_})} }
    }
    'TierSize' {
        $tier=Exact 'MSFT_StorageTier' $p.UniqueId $p.ObjectId
        $range=Invoke-CimMethod -InputObject $tier -MethodName GetSupportedSize -Arguments @{ResiliencySettingName=[string]$p.ResiliencySettingName} -ErrorAction Stop
        $result=@{ReturnValue=$range.ReturnValue;SupportedSizes=@($range.SupportedSizes | Where-Object {$null -ne $_});TierSizeMin=$range.TierSizeMin;TierSizeMax=$range.TierSizeMax;TierSizeDivisor=$range.TierSizeDivisor}
    }
    'TierCapability' {
        $subsystem=Exact 'MSFT_StorageSubSystem' $p.UniqueId $p.ObjectId
        $fields=@()
        foreach($name in @('SupportsStoragePoolCreation','SupportsStorageTierCreation','SupportsStorageTieredVirtualDiskCreation','SupportsStorageTierDeletion','SupportsStorageTierFriendlyNameModification','PhysicalDisksPerStoragePoolMin')) {
            $property=$subsystem.CimInstanceProperties[$name]
            $fields+=@{ Name=$name; Present=($null -ne $property); Value=$(if($null -ne $property){$property.Value}else{$null}); CimType=$(if($null -ne $property){[string]$property.CimType}else{$null}) }
        }
        $result=@{ Fields=$fields }
    }
    'VolumeFormat' {
        $volume=Exact 'MSFT_Volume' $p.UniqueId $p.ObjectId
        $parents=@(Get-CimAssociatedInstance -InputObject $volume -ResultClassName MSFT_Partition -ErrorAction Stop)
        if($parents.Count -ne 1 -or [guid]$parents[0].Guid -ne [guid]$p.PartitionGuid -or
            [int64]$parents[0].DiskNumber -ne [int64]$p.DiskNumber -or [int64]$parents[0].Offset -ne [int64]$p.Offset -or [int64]$parents[0].Size -ne [int64]$p.Size) { throw 'volume-parent-identity-or-geometry-changed' }
        $result=@{ FileSystems=(Method $volume 'GetSupportedFileSystems' @{}); ClusterSizes=(Method $volume 'GetSupportedClusterSizes' @{FileSystem=[string]$p.FileSystem}) }
    }
    'PhysicalDevice' {
        $disks=@(Get-CimInstance -Namespace root/CIMV2 -ClassName Win32_DiskDrive -Filter ('Index = '+[uint32]$p.DiskNumber) -ErrorAction Stop)
        if($disks.Count -gt 1) { throw 'physical-device-identity-ambiguous' }
        $result=@{ DeviceId=$(if($disks.Count -eq 1){[string]$disks[0].PNPDeviceID}else{$null}) }
    }
    'BitLocker' {
        $scope=[System.Management.ManagementScope]::new('root\CIMV2\Security\MicrosoftVolumeEncryption')
        $scope.Options.Authentication=[System.Management.AuthenticationLevel]::PacketPrivacy
        $scope.Options.Impersonation=[System.Management.ImpersonationLevel]::Impersonate
        $path='Win32_EncryptableVolume.DeviceID="'+([string]$p.DeviceId).Replace('\','\\')+'"'
        $instance=[System.Management.ManagementObject]::new($scope,[System.Management.ManagementPath]::new($path),$null)
        try {
            try { $instance.Get() }
            catch [System.Management.ManagementException] { if($_.Exception.ErrorCode -eq [System.Management.ManagementStatus]::NotFound) { return 'null' }; throw }
            $deviceId=[string]$instance['DeviceID']
            if(-not [string]::Equals($deviceId,[string]$p.DeviceId,[StringComparison]::OrdinalIgnoreCase)) { throw 'exact-bitlocker-identity-changed' }
            $conversionInput=$instance.GetMethodParameters('GetConversionStatus'); $conversionInput['PrecisionFactor']=[uint32]0
            try { $conversion=$instance.InvokeMethod('GetConversionStatus',$conversionInput,$null) } finally { $conversionInput.Dispose() }
            $lock=$instance.InvokeMethod('GetLockStatus',$null,$null)
            $encryption=$instance.InvokeMethod('GetEncryptionMethod',$null,$null)
            try {
                $instance.Get()
                $result=@{DeviceId=$deviceId;ConfirmedDeviceId=[string]$instance['DeviceID'];EncryptionMethodReturnValue=$encryption['ReturnValue'];EncryptionMethod=$encryption['EncryptionMethod'];
                    State=@{ConversionReturnValue=$conversion['ReturnValue'];ConversionStatus=$conversion['ConversionStatus'];LockReturnValue=$lock['ReturnValue'];LockStatus=$lock['LockStatus']}}
            } finally { if($null -ne $conversion){$conversion.Dispose()};if($null -ne $lock){$lock.Dispose()};if($null -ne $encryption){$encryption.Dispose()} }
        } finally { $instance.Dispose() }
    }
    'BitLockerCandidates' {
        $result=@(Get-CimInstance -Namespace 'root/CIMV2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume -ErrorAction Stop |
            ForEach-Object { @{DeviceId=[string]$_.DeviceID;DriveLetter=[string]$_.DriveLetter} })
    }
    'NativeMsrAttributes' {
        function Same($a,$b) { return -not [string]::IsNullOrWhiteSpace([string]$a) -and [string]::Equals(([string]$a).Trim(),([string]$b).Trim(),[StringComparison]::OrdinalIgnoreCase) }
        function One($values) { $items=@($values); if($items.Count -ne 1){throw 'native-msr-provider-identity-not-unique'}; return $items[0] }
        function Verify-NativeIdentity {
            $disk=One @(Get-CimInstance -Namespace $ns -ClassName MSFT_Disk -ErrorAction Stop | Where-Object { Same $_.Path $p.DiskPath })
            if(-not (Same $disk.UniqueId $p.DiskUniqueId) -or -not (Same $disk.ObjectId $p.DiskObjectId) -or
                $disk.IsReadOnly -ne $false -or $disk.IsOffline -ne [bool]$p.ExpectedDiskOffline -or
                $disk.IsBoot -ne $false -or $disk.IsSystem -ne $false -or $disk.IsClustered -ne $false -or [guid]$disk.Guid -ne [guid]$p.DiskGuid) { throw 'native-msr-disk-identity-changed' }
            if([string]::IsNullOrWhiteSpace([string]$p.VirtualDiskUniqueId)) {
                if(-not (Same $disk.SerialNumber $p.SerialNumber)) { throw 'native-msr-disk-serial-changed' }
            } else {
                $vd=One @(Get-CimAssociatedInstance -InputObject $disk -Association MSFT_VirtualDiskToDisk -ResultClassName MSFT_VirtualDisk -ErrorAction Stop)
                if(-not (Same $vd.UniqueId $p.VirtualDiskUniqueId) -or -not (Same $vd.ObjectId $p.VirtualDiskObjectId)) { throw 'native-msr-virtual-disk-changed' }
                $pool=One @(Get-CimAssociatedInstance -InputObject $vd -Association MSFT_StoragePoolToVirtualDisk -ResultClassName MSFT_StoragePool -ErrorAction Stop)
                if($pool.IsPrimordial -ne $false -or -not (Same $pool.UniqueId $p.PoolUniqueId) -or -not (Same $pool.ObjectId $p.PoolObjectId)) { throw 'native-msr-pool-changed' }
                $members=@(Get-CimAssociatedInstance -InputObject $pool -Association MSFT_StoragePoolToPhysicalDisk -ResultClassName MSFT_PhysicalDisk -ErrorAction Stop)
                if($members.Count -ne @($p.PhysicalMembers).Count) { throw 'native-msr-pool-member-count-changed' }
                foreach($expected in @($p.PhysicalMembers)) {
                    $member=One @($members | Where-Object { Same $_.UniqueId $expected.UniqueId })
                    if(-not (Same $member.ObjectId $expected.ObjectId) -or -not (Same $member.SerialNumber $expected.SerialNumber)) { throw 'native-msr-pool-member-identity-changed' }
                }
            }
            $physical=@(Get-CimInstance -Namespace $ns -ClassName MSFT_PhysicalDisk -ErrorAction Stop)
            foreach($expected in @($p.PhysicalMembers)) {
                $member=One @($physical | Where-Object { Same $_.UniqueId $expected.UniqueId })
                if(-not (Same $member.ObjectId $expected.ObjectId) -or -not (Same $member.SerialNumber $expected.SerialNumber)) { throw 'native-msr-physical-identity-changed' }
            }
            $part=One @(Get-CimInstance -Namespace $ns -ClassName MSFT_Partition -ErrorAction Stop | Where-Object { (Same $_.DiskId $p.DiskPath) -and [guid]$_.Guid -eq [guid]$p.PartitionGuid })
            if([guid]$part.GptType -ne [guid]$p.PartitionType -or [int64]$part.Offset -ne [int64]$p.Offset -or [int64]$part.Size -ne [int64]$p.Length -or
                [int]$part.PartitionNumber -ne [int]$p.PartitionNumber -or $part.IsBoot -ne $false -or $part.IsSystem -ne $false -or
                (-not [bool]$p.ExpectedDiskOffline -and $part.IsOffline -ne $false) -or ($null -ne $part.IsReadOnly -and $part.IsReadOnly -ne $false) -or
                ($null -ne $part.IsShadowCopy -and $part.IsShadowCopy -ne $false) -or
                ($null -ne $part.DriveLetter -and -not ($part.DriveLetter -is [char] -and $part.DriveLetter -eq [char]0) -and -not ($part.DriveLetter -is [string] -and $part.DriveLetter.Length -eq 0))) { throw 'native-msr-partition-identity-changed' }
            if([bool]$p.ExpectedDiskOffline -or [guid]$p.PartitionType -eq [guid]'e3c9e316-0b5c-4db8-817d-f92df00215ae') {
                if(@($part.AccessPaths | Where-Object {$null -ne $_}).Count -ne 0) { throw 'native-msr-partition-access-paths-changed' }
            } else {
                $paths=@($part.AccessPaths);$expectedPaths=@($p.AccessPaths)
                if($paths.Count -ne $expectedPaths.Count) { throw 'native-msr-partition-access-paths-changed' }
                for($index=0;$index -lt $paths.Count;$index++){if(-not [string]::Equals($paths[$index],$expectedPaths[$index],[StringComparison]::OrdinalIgnoreCase)){throw 'native-msr-partition-access-paths-changed'}}
            }
        }
        if($null -eq ('WinPoolReadOnlyDisk' -as [type])) {
            $assembly=[AppDomain]::CurrentDomain.DefineDynamicAssembly([Reflection.AssemblyName]::new('WinPool.Native.ReadOnly'),[Reflection.Emit.AssemblyBuilderAccess]::Run)
            $module=$assembly.DefineDynamicModule('Native')
            $builder=$module.DefineType('WinPoolReadOnlyDisk',[Reflection.TypeAttributes]'Public,Abstract,Sealed')
            $flags=[Reflection.MethodAttributes]'Public,Static,PinvokeImpl'
            $calling=[Reflection.CallingConventions]::Standard
            $nativeCalling=[Runtime.InteropServices.CallingConvention]::Winapi
            $charset=[Runtime.InteropServices.CharSet]::Unicode
            $method=$builder.DefinePInvokeMethod('CreateFile','kernel32.dll','CreateFileW',$flags,$calling,[IntPtr],[type[]]@([string],[uint32],[uint32],[IntPtr],[uint32],[uint32],[IntPtr]),$nativeCalling,$charset)
            $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
            $method=$builder.DefinePInvokeMethod('DeviceIoControl','kernel32.dll','DeviceIoControl',$flags,$calling,[bool],[type[]]@([IntPtr],[uint32],[byte[]],[int],[byte[]],[int],[uint32].MakeByRefType(),[IntPtr]),$nativeCalling,$charset)
            $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
            $method=$builder.DefinePInvokeMethod('CloseHandle','kernel32.dll','CloseHandle',$flags,$calling,[bool],[type[]]@([IntPtr]),$nativeCalling,$charset)
            $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
            $method=$builder.DefinePInvokeMethod('GetLastError','kernel32.dll','GetLastError',$flags,$calling,[uint32],[type[]]@(),$nativeCalling,$charset)
            $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
            $null=$builder.CreateType()
        }
        function Read-NativeDisk($handle,[uint32]$code,[byte[]]$inputBytes) {
            for($length=65536;$length -le 1048576;$length*=2) {
                $outputBytes=[byte[]]::new($length);[uint32]$returned=0
                $inputLength=0;if($null -ne $inputBytes){$inputLength=$inputBytes.Length}
                $success=[WinPoolReadOnlyDisk]::DeviceIoControl($handle,$code,$inputBytes,$inputLength,$outputBytes,$length,[ref]$returned,[IntPtr]::Zero)
                $nativeError=[WinPoolReadOnlyDisk]::GetLastError()
                if($success) { if($returned -gt $length){throw 'native-read-length-invalid'};return ,[byte[]]$outputBytes[0..([int]$returned-1)] }
                if($nativeError -notin @(122,234)){throw ('native-read-error-'+$nativeError)}
            }
            throw 'native-read-exceeds-bound'
        }
        Verify-NativeIdentity
        $handle=[WinPoolReadOnlyDisk]::CreateFile([string]$p.DiskPath,0,3,[IntPtr]::Zero,3,0,[IntPtr]::Zero)
        try {
            if($handle -eq [IntPtr](-1)) { throw 'native-msr-disk-open-failed' }
            $serial1=$null;$serial2=$null
            if([string]::IsNullOrWhiteSpace([string]$p.VirtualDiskUniqueId)) { $serial1=Read-NativeDisk $handle 0x002D1400 ([byte[]]::new(12)) }
            $first=Read-NativeDisk $handle 0x00070050 $null
            if([string]::IsNullOrWhiteSpace([string]$p.VirtualDiskUniqueId)) { $serial2=Read-NativeDisk $handle 0x002D1400 ([byte[]]::new(12)) }
            $last=Read-NativeDisk $handle 0x00070050 $null
            Verify-NativeIdentity
            $result=@{First=[Convert]::ToBase64String($first);Last=[Convert]::ToBase64String($last);SerialFirst=$(if($null -ne $serial1){[Convert]::ToBase64String($serial1)}else{$null});SerialLast=$(if($null -ne $serial2){[Convert]::ToBase64String($serial2)}else{$null})}
        } finally { $null=[WinPoolReadOnlyDisk]::CloseHandle($handle) }
    }
    default { throw 'closed-storage-query-required' }
}
$result | ConvertTo-Json -Depth 16 -Compress
""";
}
