using System.Collections.Immutable;
using System.Buffers.Binary;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsRealStorageSafetyInspectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");
    private const string PhysicalId = "physical:synthetic";
    private const string DiskId = "osdisk:synthetic";
    private const string DataId = "partition:data";
    private const string EfiId = "partition:efi";
    private const string ExactVolumePath = @"\\?\Volume{1e18831c-8b4c-4c24-b59c-afd8fce35579}\";

    private static VolumeInfo RawVolume(string fileSystem = "") => new("volume:raw", true,
        DataId, fileSystem, "", 0, 0, 0, "Healthy", "Unknown", [ExactVolumePath]);

    [Fact]
    public async Task ExactOfflineDiskCanOnlyRestoreOnlineWithoutQueryingDismountedVolumes()
    {
        var fixture = new Fixture();
        fixture.SetOfflineState(true, true);
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var disk = RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.OsDisk, DiskId));
        var partition = RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.Partition, DataId));
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Empty(volumes),
            (identity, _) => new(identity, 0));
        // The normal fixture deliberately contains a protected sibling EFI.
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(topology, closure,
            new SetDiskOnlineCommand(disk, true), CancellationToken.None));
        fixture.RemoveSibling();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await inspector.ValidateAsync(topology, closure, new SetDiskOnlineCommand(disk, true), CancellationToken.None);
        foreach (var command in new RealStorageCommand[]
        {
            new SetDiskOnlineCommand(disk, false), fixture.Delete(DataId),
            new FormatVolumeCommand(partition, RealFileSystem.Ntfs, 65536, false, ""),
            new SetDiskOnlineCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.OsDisk, "disk:wrong")), true)
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(topology, closure, command, CancellationToken.None));

        fixture.SetTargetReadOnlyState(true);
        topology = await fixture.Capture();
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(topology,
            fixture.Closure(topology), new SetDiskOnlineCommand(disk, true), CancellationToken.None));
    }

    [Fact]
    public async Task OnlineEntryDoesNotExemptAnOfflinePartitionWhenItsDiskIsAlreadyOnlineOrStateUnknown()
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.SetOfflineState(false, true);
        var topology = await fixture.Capture();
        var command = new SetDiskOnlineCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.OsDisk, DiskId)), true);
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => throw new Exception("Must reject before encryption probe"));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(topology,
            fixture.Closure(topology), command, CancellationToken.None));
        fixture.SetOfflineState(true, true);
        fixture.RemoveRawTargetField("IsOffline");
        topology = await fixture.Capture();
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(topology,
            fixture.Closure(topology), command, CancellationToken.None));
    }

    [Fact]
    public async Task ProviderNullOfflineBasicDataRequiresNativeProofForOnlineAndReadOnlyObservedOfflineState()
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.SetOfflineState(true, true);
        foreach (var field in new[] { "IsHidden", "IsOffline", "IsReadOnly", "IsShadowCopy", "AccessPaths", "DriveLetter" })
            fixture.SetRawTargetField(field, null);
        var topology = await fixture.Capture();
        Assert.Contains(topology.Snapshot.FieldIssues, issue => issue.ObjectId == DataId && issue.FieldName == "IsHidden");
        var closure = fixture.Closure(topology);
        Assert.Equal([DataId], closure.OfflinePartitionIdsNeedingNativeProof);
        var disk = RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.OsDisk, DiskId));
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Empty(volumes), (identity, _) =>
        {
            Assert.True(identity.ExpectedDiskOffline);
            Assert.Equal(WindowsNativeMsrAttributesReader.BasicDataRole, identity.PartitionType);
            calls++;
            return new(identity, 0);
        });
        var online = await inspector.ValidateWithEvidenceAsync(topology, closure, new SetDiskOnlineCommand(disk, true), CancellationToken.None);
        var proof = Assert.Single(online!.OfflinePartitionAttributes!);
        Assert.True(proof.DiskIsOffline);
        Assert.Equal(DataId, proof.PartitionStableId);
        Assert.Equal(topology.InventoryVersion, proof.InventoryVersion);
        var observed = await inspector.ValidateObservedDiskStateWithEvidenceAsync(topology, closure,
            new SetDiskOnlineCommand(disk, false), CancellationToken.None);
        Assert.Single(observed!.OfflinePartitionAttributes!);
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateWithEvidenceAsync(topology, closure,
            new SetDiskOnlineCommand(disk, false), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateObservedDiskStateWithEvidenceAsync(topology, closure,
            new SetDiskOnlineCommand(disk, true), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateWithEvidenceAsync(topology, closure,
            fixture.Delete(DataId), CancellationToken.None));
    }

    [Theory]
    [InlineData(WindowsNativeMsrAttributes.PlatformRequired)]
    [InlineData(WindowsNativeMsrAttributes.ReadOnly)]
    [InlineData(WindowsNativeMsrAttributes.ShadowCopy)]
    public async Task OfflineNativeProtectedAttrsStillRejectOnlineRestoration(ulong attributes)
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.SetOfflineState(true, true);
        var topology = await fixture.Capture();
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) => new(identity, attributes));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(topology, fixture.Closure(topology),
            new SetDiskOnlineCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.OsDisk, DiskId)), true), CancellationToken.None));
    }

    [Fact]
    public async Task OfflineNativeUnavailableOrChangedExactIdentityStillRejectsOnlineRestoration()
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.SetOfflineState(true, true);
        var topology = await fixture.Capture();
        var command = new SetDiskOnlineCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.OsDisk, DiskId)), true);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null, (_, _) => { },
            (_, _) => throw new InvalidDataException("Native unavailable")).ValidateAsync(topology,
            fixture.Closure(topology), command, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null, (_, _) => { },
            (identity, _) => new(identity with { ExpectedDiskOffline = false }, 0)).ValidateAsync(topology,
            fixture.Closure(topology), command, CancellationToken.None));
    }

    [Fact]
    public async Task OfflineRestorationProvesEveryPartitionIncludingMsrAndBasicData()
    {
        var fixture = new Fixture();
        fixture.MakeSiblingMsr();
        fixture.SetOfflineState(true, true);
        var topology = await fixture.Capture();
        var identities = new List<WindowsNativeMsrIdentity>();
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Empty(volumes), (identity, _) =>
        {
            identities.Add(identity);
            return WindowsNativeMsrAttributesReader.ParseLayout(NativeLayout(identity), identity);
        });
        var command = new SetDiskOnlineCommand(RealTargetReference.ForExisting(fixture.Id(StorageObjectKind.OsDisk, DiskId)), true);
        var result = await inspector.ValidateWithEvidenceAsync(topology, fixture.Closure(topology), command, CancellationToken.None);
        Assert.Equal(2, identities.Count);
        Assert.All(identities, identity => Assert.True(identity.ExpectedDiskOffline));
        Assert.Equal([DataId, EfiId], result!.OfflinePartitionAttributes!.Select(item => item.PartitionStableId).Order().ToArray());
        Assert.All(result.OfflinePartitionAttributes!, proof => Assert.True(proof.DiskIsOffline));
        Assert.Contains(identities, item => item.PartitionType == WindowsNativeMsrAttributesReader.MsrRole);
        Assert.Contains(identities, item => item.PartitionType == WindowsNativeMsrAttributesReader.BasicDataRole);

        var unavailableSibling = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
            identity.PartitionType == WindowsNativeMsrAttributesReader.MsrRole
                ? throw new InvalidDataException("Sibling native evidence unavailable") : new(identity, 0));
        await Assert.ThrowsAsync<InvalidDataException>(() => unavailableSibling.ValidateAsync(topology,
            fixture.Closure(topology), command, CancellationToken.None));
    }

    [Fact]
    public async Task NativeBasicDataRoleIsLimitedToExactOfflineMetadataContext()
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        var topology = await fixture.Capture();
        var partition = topology.Snapshot.Partitions.Single();
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.Identify(topology, partition, expectedDiskOffline: true));
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.Identify(topology, partition));
        fixture.SetOfflineState(true, true);
        topology = await fixture.Capture();
        var identity = WindowsNativeMsrAttributesReader.Identify(topology, topology.Snapshot.Partitions.Single(), expectedDiskOffline: true);
        Assert.Equal(new WindowsNativeMsrAttributes(identity, 0), WindowsNativeMsrAttributesReader.ParseLayout(NativeLayout(identity), identity));
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.ParseLayout(NativeLayout(identity),
            identity with { ExpectedDiskOffline = false }));
    }

    [Theory]
    [InlineData("MissingHidden")]
    [InlineData("WrongDiskPath")]
    [InlineData("MissingDiskGuid")]
    [InlineData("MissingDiskPath")]
    [InlineData("MissingDiskOffline")]
    [InlineData("MissingPartitionParent")]
    [InlineData("OnlineParent")]
    public async Task DeferredOfflineHiddenFieldRequiresExactCompleteOfflineParent(string fault)
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.SetOfflineState(true, true);
        fixture.SetRawTargetField("IsHidden", null);
        switch (fault)
        {
            case "MissingHidden": fixture.RemoveRawTargetField("IsHidden"); break;
            case "WrongDiskPath": fixture.SetRawTargetField("DiskId", @"\\.\PHYSICALDRIVE8"); break;
            case "MissingDiskGuid": fixture.RemoveObjectField(DiskId, "Guid"); break;
            case "MissingDiskPath": fixture.RemoveObjectField(DiskId, "Path"); break;
            case "MissingDiskOffline": fixture.RemoveObjectField(DiskId, "IsOffline"); break;
            case "MissingPartitionParent": fixture.RemoveRelationshipKind("disk-partition"); break;
            case "OnlineParent": fixture.SetOfflineState(false, true); break;
        }
        var topology = await fixture.Capture();
        var error = Assert.Throws<InvalidDataException>(() => fixture.Closure(topology));
        Assert.Contains(fault switch
        {
            "MissingPartitionParent" => "exactly one identified physical member",
            _ => "related storage safety field could not be read"
        }, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RAW")]
    public void UnformattedAbsentFromBitLockerNeedsIndependentExactVdsRawProof(string fileSystem)
    {
        var calls = 0;
        var evidence = WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(fileSystem), [], (path, _) =>
        {
            Assert.Equal(ExactVolumePath, path);
            calls++;
            return new(path, 1, 512);
        }, false, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.NotNull(evidence);
        Assert.Equal(DataId, evidence.PartitionStableId);
        Assert.Equal(ExactVolumePath, evidence.VolumeGuidPath);
        Assert.True(evidence.BitLockerEnumerationComplete);
        Assert.Equal("NotApplicableUnformatted", evidence.BitLockerApplicability);
        Assert.Equal(1, evidence.VdsFileSystemType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(999)]
    public void UnformattedDoesNotExemptUnknownEncryptedOrDifferentVdsClassification(int type)
    {
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            RawVolume(), [], (path, _) => new(path, type, 512), false, CancellationToken.None));
    }

    [Fact]
    public void UnformattedRejectsUnavailableProbeChangedGuidAndAmbiguousGuidPaths()
    {
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            RawVolume(), [], (_, _) => throw new InvalidDataException("VDS unavailable"), false, CancellationToken.None));
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            RawVolume(), [], (_, _) => new(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\", 1, 512),
            false, CancellationToken.None));
        foreach (var paths in new IReadOnlyList<string>[] { [], [@"Q:\"],
            [ExactVolumePath, @"\\?\Volume{11111111-2222-3333-4444-555555555555}\"] })
            Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
                RawVolume() with { AccessPaths = paths }, [], (_, _) => throw new Exception("Must not probe"),
                false, CancellationToken.None));
    }

    [Theory]
    [InlineData(0U, 0U, 0U, 0U, true)]
    [InlineData(0U, 1U, 0U, 0U, false)]
    [InlineData(0U, 0U, 0U, 1U, false)]
    [InlineData(0U, null, 0U, 0U, false)]
    [InlineData(null, 0U, 0U, 0U, false)]
    [InlineData(0U, 0U, null, 0U, false)]
    [InlineData(0U, 0U, 0U, null, false)]
    [InlineData(5U, 0U, 0U, 0U, false)]
    public void BitLockerMatchAlwaysControlsRawSafetyIncludingUnknownOrLocked(uint? conversionReturn,
        uint? conversion, uint? lockReturn, uint? locked, bool allowed)
    {
        var candidates = new[] { new WindowsRealStorageSafetyInspector.BitLockerVolume(ExactVolumePath,
            null, () => new(conversionReturn, conversion, lockReturn, locked)) };
        Action action = () => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), candidates,
            (_, _) => throw new Exception("A BitLocker match must never use the VDS exemption"), false, CancellationToken.None);
        if (allowed) action();
        else Assert.Throws<InvalidDataException>(action);
    }

    [Fact]
    public void MissingNtfsBitLockerStateAndDuplicateMatchesRemainRejected()
    {
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            RawVolume("NTFS") with { AllocationUnitSize = 65536 }, [], (_, _) => throw new Exception("Must not probe"),
            false, CancellationToken.None));
        var candidate = new WindowsRealStorageSafetyInspector.BitLockerVolume(ExactVolumePath, null, () => new(0, 0, 0, 0));
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            RawVolume(), [candidate, candidate], (_, _) => throw new Exception("Must not probe"), false, CancellationToken.None));
    }

    [Fact]
    public void ExactGuidCannotBorrowDecryptedStateFromAnotherVolumeWithSameDriveLetter()
    {
        var volume = RawVolume("NTFS") with { AllocationUnitSize = 65536, AccessPaths = [ExactVolumePath, @"Q:\"] };
        var wrongVolume = new WindowsRealStorageSafetyInspector.BitLockerVolume(
            @"\\?\Volume{11111111-2222-3333-4444-555555555555}\", "Q:", () => new(0, 0, 0, 0));
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            volume, [wrongVolume], (_, _) => throw new Exception("NTFS no-match must not probe"), false, CancellationToken.None));
    }

    [Fact]
    public void EfiApplicabilityRequiresNativeRoleProofAndRecordsNoVdsClaim()
    {
        var efi = RawVolume("FAT32") with { AllocationUnitSize = 4096 };
        var evidence = WindowsRealStorageSafetyInspector.RequireBitLockerSafety(efi, [], (_, _) => throw new Exception("EFI is excluded from VDS enumeration"),
            true, CancellationToken.None);
        Assert.Equal("NotApplicableNativeEfiSystemPartition", evidence!.BitLockerApplicability);
        Assert.Null(evidence.VdsFileSystemType);
        Assert.Null(evidence.VdsAllocationUnitSize);
        Assert.True(evidence.BitLockerEnumerationComplete);
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            efi, [], (_, _) => throw new Exception("Non-EFI FAT32 must not be exempted"), false, CancellationToken.None));
        foreach (var unsupported in new[]
        {
            efi with { AllocationUnitSize = 8192 }, efi with { AllocationUnitSize = null },
            efi with { FileSystem = "NTFS" }, efi with { AccessPaths = [@"Q:\"] },
            efi with { AccessPaths = [ExactVolumePath, @"Q:\"] }
        })
            Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
                unsupported, [], (_, _) => throw new InvalidDataException("No applicable volume proof"), true, CancellationToken.None));
    }

    [Fact]
    public void RawEfiApplicabilityDoesNotUseUnavailableVdsAndStillRejectsBitLockerMatches()
    {
        var proof = WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), [],
            (_, _) => throw new Exception("EFI must not need VDS"), true, CancellationToken.None);
        Assert.Equal("NotApplicableNativeEfiSystemPartition", proof!.BitLockerApplicability);
        var locked = new WindowsRealStorageSafetyInspector.BitLockerVolume(ExactVolumePath, null, () => new(0, 0, 0, 1));
        var encrypted = locked with { ReadState = () => new(0, 1, 0, 0) };
        var unknown = locked with { ReadState = () => new(null, null, null, null) };
        foreach (var matches in new IReadOnlyList<WindowsRealStorageSafetyInspector.BitLockerVolume>[]
            { [locked], [encrypted], [unknown], [locked, locked] })
            Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), matches,
                (_, _) => throw new Exception("BitLocker match must control EFI safety"), true, CancellationToken.None));
    }

    private static WindowsRealStorageSafetyInspector.ExactBitLockerState ExactDecryptedBitLocker() =>
        new(ExactVolumePath, ExactVolumePath, 0, 0, new(0, 0, 0, 0));

    [Theory]
    [InlineData("")]
    [InlineData("NTFS")]
    public void ExactBitLockerObjectProvesDecryptionWithoutAnEnumeratedCandidateOrVds(string fileSystem)
    {
        var volume = RawVolume(fileSystem) with { AllocationUnitSize = fileSystem.Length == 0 ? 0 : 4096 };
        var proof = WindowsRealStorageSafetyInspector.RequireBitLockerSafety(volume, [],
            (_, _) => throw new Exception("A successful exact BitLocker object must not need VDS"), false, CancellationToken.None,
            (path, _) => { Assert.Equal(ExactVolumePath, path); return ExactDecryptedBitLocker(); });
        Assert.Equal("Applicable", proof!.BitLockerApplicability);
        Assert.Equal("Win32EncryptableVolumeExactGet", proof.VerificationMethod);
        Assert.Equal(DataId, proof.PartitionStableId);
        Assert.Equal(ExactVolumePath, proof.VolumeGuidPath);
        Assert.True(proof.BitLockerEnumerationComplete);
        Assert.Null(proof.VdsFileSystemType);
        Assert.Null(proof.VdsAllocationUnitSize);
        var decrypted = Assert.IsType<WindowsBitLockerDecryptionEvidence>(proof.FullyDecryptedBitLocker);
        Assert.Equal("FullyDecrypted", decrypted.EncryptionState);
        Assert.Equal(ExactVolumePath, decrypted.DeviceId);
        Assert.Equal(ExactVolumePath, decrypted.ConfirmedDeviceId);
        Assert.Equal(0U, decrypted.EncryptionMethod);
        Assert.Equal(0U, decrypted.ConversionStatus);
        Assert.Equal(0U, decrypted.LockStatus);
    }

    [Theory]
    [InlineData("BeforeIdentity")]
    [InlineData("AfterIdentity")]
    [InlineData("MethodReturn")]
    [InlineData("ConversionReturn")]
    [InlineData("LockReturn")]
    [InlineData("EncryptedMethod")]
    [InlineData("EncryptedConversion")]
    [InlineData("Locked")]
    [InlineData("UnknownMethod")]
    [InlineData("UnknownConversion")]
    [InlineData("UnknownLock")]
    public void DirectBitLockerFailureCannotBeOverriddenByVdsOrEfiApplicability(string fault)
    {
        var direct = ExactDecryptedBitLocker();
        direct = fault switch
        {
            "BeforeIdentity" => direct with { DeviceId = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\" },
            "AfterIdentity" => direct with { ConfirmedDeviceId = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\" },
            "MethodReturn" => direct with { EncryptionMethodReturnValue = 5 },
            "ConversionReturn" => direct with { State = direct.State with { ConversionReturnValue = 5 } },
            "LockReturn" => direct with { State = direct.State with { LockReturnValue = 5 } },
            "EncryptedMethod" => direct with { EncryptionMethod = 7 },
            "EncryptedConversion" => direct with { State = direct.State with { ConversionStatus = 1 } },
            "Locked" => direct with { State = direct.State with { LockStatus = 1 } },
            "UnknownMethod" => direct with { EncryptionMethod = null },
            "UnknownConversion" => direct with { State = direct.State with { ConversionStatus = null } },
            "UnknownLock" => direct with { State = direct.State with { LockStatus = null } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        foreach (var efi in new[] { false, true })
            Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), [],
                (_, _) => throw new Exception("Invalid direct state must reject before independent fallback"), efi, CancellationToken.None,
                (_, _) => direct));
    }

    [Fact]
    public void GenericDirectProbeErrorAndMatchedEncryptionCannotUseFallback()
    {
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), [],
            (path, _) => new(path, 1, 512), true, CancellationToken.None,
            (_, _) => throw new InvalidDataException("Generic WMI error is not object absence")));
        var encrypted = new WindowsRealStorageSafetyInspector.BitLockerVolume(ExactVolumePath, null, () => new(0, 1, 0, 0));
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), [encrypted],
            (_, _) => throw new Exception("Matched encryption must reject before VDS"), true, CancellationToken.None,
            (_, _) => throw new Exception("Matched encryption must reject before direct lookup")));
    }

    [Fact]
    public void ExactObjectNotFoundNeedsIndependentExistingProofAndDoesNotProveNtfsDecrypted()
    {
        var raw = WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), [],
            (path, _) => new(path, 1, 512), false, CancellationToken.None, (_, _) => null);
        Assert.Equal("VdsExactVolumeFileSystem", raw!.VerificationMethod);
        Assert.Null(raw.FullyDecryptedBitLocker);
        var efi = WindowsRealStorageSafetyInspector.RequireBitLockerSafety(RawVolume(), [],
            (_, _) => throw new Exception("Native EFI does not need VDS"), true, CancellationToken.None, (_, _) => null);
        Assert.Equal("NativeGptEfiSystemPartition", efi!.VerificationMethod);
        Assert.Null(efi.FullyDecryptedBitLocker);
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireBitLockerSafety(
            RawVolume("NTFS") with { AllocationUnitSize = 4096 }, [], (path, _) => new(path, 4, 4096),
            false, CancellationToken.None, (_, _) => null));
    }

    [Fact]
    public async Task NewlyCreatedRawEfiFormatAndRawEfiDeletionRequireExactNativeEvidence()
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.NativeNullUnformattedEfi();
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var target = fixture.Id(StorageObjectKind.Partition, DataId);
        var command = new FormatVolumeCommand(RealTargetReference.FromStep(StorageObjectKind.Partition, "create-partition"),
            RealFileSystem.Fat32, 4096, false, "WP_S1_EFI");
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Single(volumes), (identity, _) => new(identity, 0));
        var result = await inspector.ValidateCreatedPartitionFormatWithEvidenceAsync(topology, closure, command, target, CancellationToken.None);
        var nativeProof = Assert.IsType<WindowsNativeMsrSafetyEvidence>(result?.NativePartitionAttributes);
        Assert.Equal(DataId, nativeProof.PartitionStableId);
        Assert.Equal(WindowsNativeMsrAttributesReader.EfiRole, nativeProof.PartitionType);
        Assert.Equal(topology.InventoryVersion, nativeProof.InventoryVersion);
        Assert.Equal([ExactVolumePath], WindowsNativeMsrAttributesReader.Identify(topology, topology.Snapshot.Partitions.Single()).AccessPaths);
        Assert.NotNull((await inspector.ValidateWithEvidenceAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None))!.NativePartitionAttributes);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateCreatedPartitionFormatWithEvidenceAsync(topology,
            closure, command with { Partition = RealTargetReference.ForExisting(target) }, target, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateCreatedPartitionFormatWithEvidenceAsync(topology,
            closure, command with { Full = true }, target, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateCreatedPartitionFormatWithEvidenceAsync(topology,
            closure, command with { ClusterBytes = 65536 }, target, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateCreatedPartitionFormatWithEvidenceAsync(topology,
            closure, command, fixture.Id(StorageObjectKind.Partition, "partition:wrong"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null, (_, _) => { },
            (identity, _) => new(identity, WindowsNativeMsrAttributes.PlatformRequired)).ValidateCreatedPartitionFormatWithEvidenceAsync(
                topology, closure, command, target, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null,
            (_, _) => throw new InvalidDataException("BitLocker enumeration unavailable"), (identity, _) => new(identity, 0))
            .ValidateCreatedPartitionFormatWithEvidenceAsync(topology, closure, command, target, CancellationToken.None));
    }

    [Theory]
    [InlineData("MissingVolume")]
    [InlineData("MissingGuidPath")]
    [InlineData("ChangedPaths")]
    [InlineData("MissingVolumePath")]
    [InlineData("BootPartition")]
    [InlineData("RecoveryRole")]
    public async Task NativeEfiApplicabilityRejectsUnavailableAssociationAndOtherRoles(string fault)
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.NativeNullUnformattedEfi();
        switch (fault)
        {
            case "MissingVolume": fixture.RemoveTargetVolume(); break;
            case "MissingGuidPath": fixture.SetRawTargetField("AccessPaths", Array.Empty<string>()); break;
            case "ChangedPaths": fixture.SetRawTargetField("AccessPaths", new[] { @"\\?\Volume{11111111-2222-3333-4444-555555555555}\" }); break;
            case "MissingVolumePath": fixture.RemoveObjectField("volume:raw", "Path"); break;
            case "BootPartition": fixture.MarkTargetBoot(); break;
            case "RecoveryRole": fixture.SetTargetRole("WindowsRecovery", WindowsNativeMsrAttributesReader.RecoveryRole.ToString()); break;
        }
        var topology = await fixture.Capture();
        if (fault == "ChangedPaths")
        {
            Assert.Equal(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\", Assert.Single(topology.Snapshot.Volumes).AccessPaths.Single());
            Assert.Equal(ExactVolumePath, topology.RequireObject(fixture.Id(StorageObjectKind.Volume, "volume:raw")).Field("Path")!.DisplayValue());
        }
        var reads = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            reads++;
            return new(identity, 0);
        });
        var command = new FormatVolumeCommand(RealTargetReference.FromStep(StorageObjectKind.Partition, "create-partition"),
            RealFileSystem.Fat32, 4096, false, "WP_S1_EFI");
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateCreatedPartitionFormatWithEvidenceAsync(topology,
            fixture.Closure(topology), command, fixture.Id(StorageObjectKind.Partition, DataId), CancellationToken.None));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task RawRecoveryDeletionRetainsNativeGuardsAndRequiresPositiveEncryptionProof()
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.NativeNullUnformattedRecovery();
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        WindowsVolumeSafetyEvidence? encryptionProof = null;
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) =>
        {
            encryptionProof = WindowsRealStorageSafetyInspector.RequireBitLockerSafety(Assert.Single(volumes), [],
                (_, _) => throw new InvalidDataException("Recovery absent from VDS"), false, CancellationToken.None,
                (_, _) => ExactDecryptedBitLocker());
        }, (identity, _) =>
        {
            Assert.Equal(WindowsNativeMsrAttributesReader.RecoveryRole, identity.PartitionType);
            return new(identity, 0);
        });
        var result = await inspector.ValidateWithEvidenceAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None);
        Assert.Equal(WindowsNativeMsrAttributesReader.RecoveryRole, result!.NativePartitionAttributes!.PartitionType);
        Assert.Equal("Win32EncryptableVolumeExactGet", encryptionProof!.VerificationMethod);
        Assert.NotNull(encryptionProof.FullyDecryptedBitLocker);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null, (volumes, _) =>
        {
            WindowsRealStorageSafetyInspector.RequireBitLockerSafety(Assert.Single(volumes), [],
                (_, _) => throw new InvalidDataException("Recovery absent from VDS"), false, CancellationToken.None,
                (_, _) => null);
        }, (identity, _) => new(identity, 0)).ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null,
            (_, _) => throw new Exception("Protected native attributes must reject before encryption probe"),
            (identity, _) => new(identity, WindowsNativeMsrAttributes.PlatformRequired))
            .ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Fact]
    public async Task DirectBitLockerGuidCannotBorrowAnotherVolumeThroughInheritedPartitionPaths()
    {
        var fixture = new Fixture();
        fixture.RemoveSibling();
        fixture.NativeNullUnformattedRecovery();
        var topology = await fixture.Capture();
        Assert.Equal(ExactVolumePath, WindowsRealStorageSafetyInspector.RequireExactVolumeGuidPath(topology,
            Assert.Single(topology.Snapshot.Volumes)));
        fixture.SetRawTargetField("AccessPaths", new[] { @"\\?\Volume{11111111-2222-3333-4444-555555555555}\" });
        topology = await fixture.Capture();
        Assert.Throws<InvalidDataException>(() => WindowsRealStorageSafetyInspector.RequireExactVolumeGuidPath(topology,
            Assert.Single(topology.Snapshot.Volumes)));
    }

    [Theory]
    [InlineData("EfiSystem", "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", "FAT32")]
    [InlineData("WindowsRecovery", "de94bba4-06d1-4d40-a16a-bfd50179d6ac", "NTFS")]
    public async Task ProviderNullEfiRecoveryAttributesRequireExactNativeProofAndRetainVolumeGuards(
        string role, string guid, string fileSystem)
    {
        var fixture = new Fixture();
        fixture.NativeNullFormattedRole(role, guid, fileSystem);
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) => Assert.Single(volumes), (identity, _) =>
        {
            Assert.Equal(Guid.Parse(guid), identity.PartitionType);
            Assert.Equal([ExactVolumePath], identity.AccessPaths);
            calls++;
            return new(identity, 0);
        });
        await inspector.ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null,
            (_, _) => { }, (identity, _) => new(identity, WindowsNativeMsrAttributes.ReadOnly))
            .ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(null,
            (_, _) => throw new InvalidDataException("BitLocker locked"), (identity, _) => new(identity, 0))
            .ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData('\0', true)]
    [InlineData("", true)]
    [InlineData('Q', false)]
    [InlineData(' ', false)]
    [InlineData("Q", false)]
    [InlineData(" ", false)]
    [InlineData("\0", false)]
    [InlineData(0, false)]
    public void NativeMsrLiveDriveLetterAcceptsOnlyExactUnassignedValues(object? value, bool expected)
    {
        Assert.Equal(expected, WindowsNativeMsrAttributesReader.IsUnassignedDriveLetter(value));
    }

    [Fact]
    public void NativeMsrLiveDriveLetterRejectsArrays()
    {
        Assert.False(WindowsNativeMsrAttributesReader.IsUnassignedDriveLetter(Array.Empty<string>()));
        Assert.False(WindowsNativeMsrAttributesReader.IsUnassignedDriveLetter(new[] { '\0' }));
    }

    [Fact]
    public async Task A05ChecksSelectedBasicDataWithoutTreatingSiblingEfiAsDeletionTarget()
    {
        var fixture = new Fixture();
        var observed = new List<string>();
        var inspector = new WindowsRealStorageSafetyInspector(null, (volumes, _) =>
            observed.AddRange(volumes.Select(item => item.StableId)));
        var topology = await fixture.Capture();
        Assert.True(topology.Snapshot.FieldIssues.Count == 0,
            string.Join("; ", topology.Snapshot.FieldIssues.Select(issue =>
                $"{issue.ObjectId}:{issue.FieldName}:{issue.Reason}")));
        var closure = fixture.Closure(topology);

        await inspector.ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None);

        Assert.Equal(["volume:data"], observed);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, fixture.Delete(EfiId), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, new ClearDiskCommand(RealTargetReference.ForExisting(
                fixture.Id(StorageObjectKind.OsDisk, DiskId)), false), CancellationToken.None));

        fixture.MarkTargetShadowCopy();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Theory]
    [InlineData("EfiSystem", "c12a7328-f81f-11d2-ba4b-00a0c93ec93b")]
    [InlineData("MicrosoftReserved", "e3c9e316-0b5c-4db8-817d-f92df00215ae")]
    [InlineData("WindowsRecovery", "de94bba4-06d1-4d40-a16a-bfd50179d6ac")]
    public async Task H04CanDeleteExactNewNonSystemPartitionOfSupportedRole(
        string roleName, string roleGuid)
    {
        var fixture = new Fixture();
        if (roleName == "MicrosoftReserved") fixture.SetTargetRole(roleName, roleGuid);
        else fixture.NativeNullFormattedRole(roleName, roleGuid, roleName == "EfiSystem" ? "FAT32" : "NTFS");
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) => new(identity, 0));

        await inspector.ValidateAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None);

        fixture.MarkTargetBoot();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Fact]
    public async Task A05RejectsUnlistedGptRoleBeforeSafetyInspection()
    {
        var fixture = new Fixture();
        fixture.SetTargetRole("Unknown", "11111111-2222-3333-4444-555555555555");
        var topology = await fixture.Capture();
        Assert.Throws<InvalidDataException>(() => fixture.Closure(topology));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task H04RejectsReadOnlyOrUnknownSelectedPartitionState(bool? isReadOnly)
    {
        var fixture = new Fixture();
        fixture.SetTargetRole("MicrosoftReserved", "e3c9e316-0b5c-4db8-817d-f92df00215ae");
        fixture.SetTargetReadOnlyState(isReadOnly);
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);

        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(
            null, (_, _) => { }, (_, _) => throw new InvalidDataException("Native proof unavailable.")).ValidateAsync(
                topology, closure, fixture.Delete(DataId), CancellationToken.None));
    }

    [Fact]
    public async Task H04ProviderNullMsrRequiresExactSafeNativeAttributes()
    {
        var fixture = new Fixture();
        fixture.NativeNullMsr();
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            Assert.Equal(@"\\.\PHYSICALDRIVE7", identity.DiskPath);
            Assert.Equal("SERIAL-1", identity.SerialNumber);
            Assert.Equal(Guid.Parse("8c4b7c34-04ba-46b1-80d7-9d967441a02d"), identity.PartitionGuid);
            return new(identity, 0);
        });
        var proof = (await inspector.ValidateWithEvidenceAsync(topology, closure, fixture.Delete(DataId), CancellationToken.None))?.NativePartitionAttributes;
        Assert.NotNull(proof);
        Assert.Equal(topology.InventoryVersion, proof.InventoryVersion);
        Assert.Equal(DataId, proof.PartitionStableId);
        Assert.Equal(0UL, proof.Attributes);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task H07SingleMemberVirtualDiskMsrPreservesProviderBackingEvidence()
    {
        var fixture = new Fixture();
        fixture.NativeNullVirtualMsr();
        var topology = await fixture.Capture();
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            Assert.Equal("vd:synthetic", identity.VirtualDiskUniqueId);
            Assert.Equal("vd:synthetic", identity.VirtualDiskObjectId);
            Assert.Equal("pool:concrete", identity.PoolUniqueId);
            Assert.Equal("pool:concrete", identity.PoolObjectId);
            Assert.Equal("SERIAL-1", identity.SerialNumber);
            Assert.Equal(@"\\?\storage#virtual#synthetic", identity.DiskPath);
            // The virtual OS disk deliberately has no physical serial. Its exact
            // provider associations and native GPT GUID bind the selected handle.
            Assert.Equal("", topology.Facts.Objects.Single(item => item.Id == DiskId)
                .Field("SerialNumber")!.Value!.Value.GetString());
            return WindowsNativeMsrAttributesReader.ParseLayout(NativeLayout(identity), identity);
        });
        var proof = (await inspector.ValidateWithEvidenceAsync(topology, fixture.Closure(topology),
            fixture.Delete(DataId), CancellationToken.None))?.NativePartitionAttributes;
        Assert.NotNull(proof);
        Assert.Equal("vd:synthetic", proof.VirtualDiskUniqueId);
        Assert.Equal("vd:synthetic", proof.VirtualDiskObjectId);
        Assert.Equal("pool:concrete", proof.PoolUniqueId);
        Assert.Equal("pool:concrete", proof.PoolObjectId);
        Assert.Equal("ProviderPathGptGuidAndVirtualDiskPoolSingleMemberAssociations", proof.NativeBindingMethod);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("same-device")]
    [InlineData("pool-virtual-disk")]
    [InlineData("pool-member")]
    public async Task H07VirtualMsrMissingBackingAssociationCannotReadNative(string missingKind)
    {
        var fixture = new Fixture();
        fixture.NativeNullVirtualMsr();
        fixture.RemoveRelationshipKind(missingKind);
        var topology = await fixture.Capture();
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            return new(identity, 0);
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await inspector.ValidateWithEvidenceAsync(topology, fixture.Closure(topology),
                fixture.Delete(DataId), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task H07VirtualMsrMultiplePhysicalMembersRemainRejected()
    {
        var fixture = new Fixture();
        fixture.NativeNullVirtualMsr();
        fixture.AddSecondPoolMember();
        var topology = await fixture.Capture();
        Assert.Throws<InvalidDataException>(() => fixture.Closure(topology));
    }

    [Theory]
    [InlineData("vd:synthetic", "UniqueId")]
    [InlineData("vd:synthetic", "ObjectId")]
    [InlineData("pool:concrete", "UniqueId")]
    [InlineData("pool:concrete", "ObjectId")]
    public async Task H07VirtualMsrMissingBackingIdentityCannotReadNative(string id, string field)
    {
        var fixture = new Fixture();
        fixture.NativeNullVirtualMsr();
        fixture.RemoveObjectField(id, field);
        var topology = await fixture.Capture();
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            return new(identity, 0);
        });
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await inspector.ValidateWithEvidenceAsync(topology, fixture.Closure(topology),
                fixture.Delete(DataId), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task H07VirtualMsrChangedBackingIdentityOrProtectedAttributesRemainRejected(bool changedBacking)
    {
        var fixture = new Fixture();
        fixture.NativeNullVirtualMsr();
        var topology = await fixture.Capture();
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
            new(changedBacking ? identity with { PoolObjectId = "pool:other" } : identity,
                changedBacking ? 0 : WindowsNativeMsrAttributes.ShadowCopy));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateWithEvidenceAsync(
            topology, fixture.Closure(topology), fixture.Delete(DataId), CancellationToken.None));
    }

    [Theory]
    [InlineData(WindowsNativeMsrAttributes.ReadOnly)]
    [InlineData(WindowsNativeMsrAttributes.ShadowCopy)]
    [InlineData(WindowsNativeMsrAttributes.PlatformRequired)]
    public async Task H04NativeProtectedMsrStillRejectsDeletion(ulong attributes)
    {
        var fixture = new Fixture();
        fixture.NativeNullMsr();
        var topology = await fixture.Capture();
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { },
            (identity, _) => new(identity, attributes));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, fixture.Closure(topology), fixture.Delete(DataId), CancellationToken.None));
    }

    [Theory]
    [InlineData("DriveLetter", "Q")]
    [InlineData("IsReadOnly", true)]
    [InlineData("IsShadowCopy", true)]
    [InlineData("IsOffline", true)]
    public async Task H04UnsafeRawMsrFieldsRejectBeforeNativeRead(string name, object value)
    {
        var fixture = new Fixture();
        fixture.NativeNullMsr();
        fixture.SetRawTargetField(name, value);
        var topology = await fixture.Capture();
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            return new(identity, 0);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, fixture.Closure(topology), fixture.Delete(DataId), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task H04NativeFailureOrChangedIdentityCannotSupplyMsrSafety(bool changedIdentity)
    {
        var fixture = new Fixture();
        fixture.NativeNullMsr();
        var topology = await fixture.Capture();
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
            changedIdentity ? new(identity with { DiskGuid = Guid.NewGuid() }, 0)
                : throw new InvalidDataException("Synthetic native read failure."));
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, fixture.Closure(topology), fixture.Delete(DataId), CancellationToken.None));
    }

    [Fact]
    public async Task H04BasicDataNullCannotUseNativeMsrEvidence()
    {
        var fixture = new Fixture();
        fixture.SetTargetReadOnlyState(null);
        var topology = await fixture.Capture();
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            return new(identity, 0);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, fixture.Closure(topology), fixture.Delete(DataId), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task H04MsrNonemptyPathsCannotUseNullAttributeSupplement()
    {
        var fixture = new Fixture();
        fixture.NativeNullMsr();
        fixture.SetRawTargetField("AccessPaths", new[] { @"Q:\" });
        var topology = await fixture.Capture();
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            return new(identity, 0);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => inspector.ValidateAsync(
            topology, fixture.Closure(topology), fixture.Delete(DataId), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("IsReadOnly")]
    [InlineData("IsShadowCopy")]
    [InlineData("AccessPaths")]
    [InlineData("DriveLetter")]
    public async Task H04MsrMissingSafetyFieldStillRejectsBeforeNativeRead(string name)
    {
        var fixture = new Fixture();
        fixture.NativeNullMsr();
        fixture.RemoveRawTargetField(name);
        var topology = await fixture.Capture();
        var calls = 0;
        var inspector = new WindowsRealStorageSafetyInspector(null, (_, _) => { }, (identity, _) =>
        {
            calls++;
            return new(identity, 0);
        });
        // Either topology or the direct safety check must reject missing fields.
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await inspector.ValidateAsync(topology, fixture.Closure(topology), fixture.Delete(DataId), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task H04NativeLayoutRequiresExactCompleteUniqueGptEntry()
    {
        var fixture = new Fixture();
        fixture.NativeNullMsr();
        var topology = await fixture.Capture();
        var identity = WindowsNativeMsrAttributesReader.Identify(topology,
            topology.Snapshot.Partitions.Single(item => item.StableId == DataId));
        var bytes = NativeLayout(identity);
        Assert.Equal(new WindowsNativeMsrAttributes(identity, 0),
            WindowsNativeMsrAttributesReader.ParseLayout(bytes, identity));
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.ParseLayout(bytes[..^1], identity));
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.ParseLayout(bytes,
            identity with { DiskGuid = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.ParseLayout(bytes,
            identity with { PartitionGuid = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.ParseLayout(bytes,
            identity with { Offset = identity.Offset + 512 }));
        var duplicate = new byte[48 + 288];
        bytes.CopyTo(duplicate, 0);
        bytes.AsSpan(48, 144).CopyTo(duplicate.AsSpan(192));
        BinaryPrimitives.WriteUInt32LittleEndian(duplicate.AsSpan(4), 2);
        Assert.Throws<InvalidDataException>(() => WindowsNativeMsrAttributesReader.ParseLayout(duplicate, identity));
    }

    private static byte[] NativeLayout(WindowsNativeMsrIdentity identity)
    {
        var bytes = new byte[48 + 144];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        identity.DiskGuid.TryWriteBytes(bytes.AsSpan(8, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 1L << 20);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), 1L << 30);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(56), identity.Offset);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(64), identity.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(72), (uint)identity.PartitionNumber);
        identity.PartitionType.TryWriteBytes(bytes.AsSpan(80, 16));
        identity.PartitionGuid.TryWriteBytes(bytes.AsSpan(96, 16));
        return bytes;
    }

    [Fact]
    public async Task A05StillRejectsTargetBitLockerUnknownRuntimeUseAndProtectedPhysicalMember()
    {
        var fixture = new Fixture();
        var topology = await fixture.Capture();
        var closure = fixture.Closure(topology);
        var deletion = fixture.Delete(DataId);

        var unknownEncryption = new WindowsRealStorageSafetyInspector(null, (volumes, _) =>
        {
            Assert.Single(volumes);
            throw new InvalidDataException("Synthetic BitLocker state unknown.");
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => unknownEncryption.ValidateAsync(
            topology, closure, deletion, CancellationToken.None));

        var activePath = new WindowsRealStorageSafetyInspector([@"E:\WinPool"], (_, _) => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => activePath.ValidateAsync(
            topology, closure, deletion, CancellationToken.None));

        fixture.RemoveTargetVolume();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => activePath.ValidateAsync(
            topology, closure, deletion, CancellationToken.None));

        fixture.ProtectPhysicalMember();
        topology = await fixture.Capture();
        closure = fixture.Closure(topology);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsRealStorageSafetyInspector(
            null, (_, _) => { }).ValidateAsync(topology, closure, deletion, CancellationToken.None));
    }

    private sealed class Fixture
    {
        private readonly SystemId system = SystemId.New();
        private StorageSnapshot snapshot = InitialSnapshot();
        private bool targetShadowCopy;
        private bool siblingShadowCopy = true;
        private bool replaceTargetReadOnly;
        private bool? targetReadOnlyState;
        private readonly Dictionary<string, object?> rawTargetFields = new();
        private readonly HashSet<string> removedTargetFields = new();
        private readonly HashSet<string> removedRelationshipKinds = new();
        private readonly HashSet<(string Id, string Field)> removedObjectFields = new();
        private string DiskProviderPath => snapshot.OsDisks[0].VirtualDiskStableId is null
            ? @"\\.\PHYSICALDRIVE7" : @"\\?\storage#virtual#synthetic";
        private readonly WindowsRealStorageTopologyReader reader;

        public Fixture() => reader = new WindowsRealStorageTopologyReader(
            new SyntheticFactSource(() => Document()), new SyntheticMachineIdentity(),
            new FixedTimeProvider());

        public StorageObjectId Id(StorageObjectKind kind, string key) => new(system, kind, key);
        public DeletePartitionCommand Delete(string id) => new(
            RealTargetReference.ForExisting(Id(StorageObjectKind.Partition, id)));
        public Task<WindowsRealStorageTopology> Capture() => reader.CaptureAsync(CancellationToken.None);
        public RealTargetClosure Closure(WindowsRealStorageTopology topology) =>
            topology.RequireSinglePhysicalClosure([Id(StorageObjectKind.Partition, DataId)]);
        public void ProtectPhysicalMember() => snapshot = snapshot with
        {
            PhysicalDisks = [snapshot.PhysicalDisks[0] with { HealthStatus = "Unhealthy" }]
        };
        public void RemoveTargetVolume() => snapshot = snapshot with
        {
            Volumes = snapshot.Volumes.Where(item => item.PartitionStableId != DataId).ToArray()
        };
        public void MarkTargetShadowCopy() => targetShadowCopy = true;
        public void RemoveSibling() => snapshot = snapshot with
        {
            Partitions = snapshot.Partitions.Where(item => item.StableId != EfiId).ToArray(),
            Volumes = snapshot.Volumes.Where(item => item.PartitionStableId != EfiId).ToArray()
        };
        public void MakeSiblingMsr()
        {
            siblingShadowCopy = false;
            snapshot = snapshot with
            {
                Partitions = snapshot.Partitions.Select(item => item.StableId == EfiId ? item with
                {
                    Type = "MicrosoftReserved", PartitionTypeId = WindowsNativeMsrAttributesReader.MsrRole.ToString(),
                    GptType = WindowsNativeMsrAttributesReader.MsrRole.ToString(), IsHidden = true,
                    DriveLetter = "", Path = "", FileSystem = "", FileSystemLabel = "", AllocationUnitSize = 0
                } : item).ToArray(),
                Volumes = snapshot.Volumes.Where(item => item.PartitionStableId != EfiId).ToArray()
            };
        }
        public void SetOfflineState(bool diskOffline, bool partitionOffline)
        {
            snapshot = snapshot with { OsDisks = [snapshot.OsDisks[0] with { IsOffline = diskOffline }],
                Partitions = diskOffline ? snapshot.Partitions.Select(item => item with
                    { DriveLetter = "", Path = "", FileSystem = "" }).ToArray() : snapshot.Partitions,
                Volumes = diskOffline ? [] : snapshot.Volumes };
            SetRawTargetField("IsOffline", partitionOffline);
        }
        public void SetRawTargetField(string name, object? value) => rawTargetFields[name] = value;
        public void RemoveRawTargetField(string name) => removedTargetFields.Add(name);
        public void RemoveRelationshipKind(string kind) => removedRelationshipKinds.Add(kind);
        public void RemoveObjectField(string id, string field) => removedObjectFields.Add((id, field));
        public void NativeNullVirtualMsr()
        {
            NativeNullMsr();
            snapshot = snapshot with
            {
                PhysicalDisks = [snapshot.PhysicalDisks[0] with { PoolStableId = "pool:concrete" }],
                StoragePools = [snapshot.StoragePools[0], new StoragePoolInfo("pool:concrete", true,
                    "Concrete", false, "Healthy", "OK", 1L << 30, 256L << 20,
                    "subsystem:local", [PhysicalId])],
                VirtualDisks = [new VirtualDiskInfo("vd:synthetic", true, "Synthetic VD", "Healthy", "OK",
                    "Simple", "Fixed", 1, 65536, 256L << 20, 256L << 20,
                    "pool:concrete", [], [7])],
                OsDisks = [snapshot.OsDisks[0] with { PhysicalDiskStableId = null,
                    VirtualDiskStableId = "vd:synthetic", Size = 256L << 20 }]
            };
        }
        public void AddSecondPoolMember() => snapshot = snapshot with
        {
            PhysicalDisks = [.. snapshot.PhysicalDisks, snapshot.PhysicalDisks[0] with
            { StableId = "physical:second", SerialNumber = "SERIAL-2", DeviceId = 8 }],
            StoragePools = snapshot.StoragePools.Select(item => item.StableId == "pool:concrete"
                ? item with { MemberPhysicalDiskIds = [PhysicalId, "physical:second"] } : item).ToArray()
        };
        public void NativeNullMsr()
        {
            SetTargetRole("MicrosoftReserved", "e3c9e316-0b5c-4db8-817d-f92df00215ae");
            foreach (var name in new[] { "IsReadOnly", "IsShadowCopy", "AccessPaths", "DriveLetter" })
                SetRawTargetField(name, null);
        }
        public void NativeNullFormattedRole(string role, string guid, string fileSystem)
        {
            SetTargetRole(role, guid);
            SetRawTargetField("IsReadOnly", null);
            SetRawTargetField("IsShadowCopy", null);
            SetRawTargetField("AccessPaths", new[] { ExactVolumePath });
            snapshot = snapshot with
            {
                Partitions = snapshot.Partitions.Select(item => item.StableId == DataId
                    ? item with { FileSystem = fileSystem, AllocationUnitSize = 4096 } : item).ToArray(),
                Volumes = [.. snapshot.Volumes, new VolumeInfo("volume:data", true, DataId,
                    fileSystem, "", 128L << 20, 64L << 20, 4096, "Healthy", "OK", [ExactVolumePath])]
            };
        }
        public void NativeNullUnformattedEfi() =>
            NativeNullUnformattedRole("EfiSystem", WindowsNativeMsrAttributesReader.EfiRole);
        public void NativeNullUnformattedRecovery() =>
            NativeNullUnformattedRole("WindowsRecovery", WindowsNativeMsrAttributesReader.RecoveryRole);
        private void NativeNullUnformattedRole(string role, Guid type)
        {
            SetTargetRole(role, type.ToString());
            foreach (var name in new[] { "IsReadOnly", "IsShadowCopy", "DriveLetter" })
                SetRawTargetField(name, null);
            SetRawTargetField("AccessPaths", new[] { ExactVolumePath });
            snapshot = snapshot with
            {
                Partitions = snapshot.Partitions.Select(item => item.StableId == DataId
                    ? item with { AllocationUnitSize = 0 } : item).ToArray(),
                Volumes = [.. snapshot.Volumes, RawVolume()]
            };
        }
        public void SetTargetReadOnlyState(bool? value)
        {
            replaceTargetReadOnly = true;
            targetReadOnlyState = value;
        }
        public void SetTargetRole(string type, string guid)
        {
            snapshot = snapshot with
            {
                Partitions = snapshot.Partitions.Select(item => item.StableId == DataId
                    ? item with
                    {
                        Type = type, PartitionTypeId = guid, GptType = guid,
                        IsHidden = true, DriveLetter = "", Path = "", FileSystem = "",
                        FileSystemLabel = ""
                    }
                    : item).ToArray(),
                Volumes = snapshot.Volumes.Where(item => item.PartitionStableId != DataId).ToArray()
            };
        }
        public void MarkTargetBoot() => snapshot = snapshot with
        {
            Partitions = snapshot.Partitions.Select(item => item.StableId == DataId
                ? item with { IsBoot = true } : item).ToArray()
        };

        private StorageSystemDocument Document()
        {
            var facts = WinPoolSimulationFacts.Create(snapshot, system);
            var sources = facts.Sources.Select(source => source with
            {
                Origin = source.ClassName.StartsWith("MSFT_", StringComparison.Ordinal)
                    ? FactOrigin.StorageCim : FactOrigin.Win32
            }).ToImmutableArray();
            // An offline disk has no visible volumes. The query still completed;
            // absence of an object must not stand in for an unavailable source.
            foreach (var className in new[] { "MSFT_StorageTier", "MSFT_VirtualDisk", "MSFT_Volume" })
            {
                if (sources.Any(source => source.ClassName == className)) continue;
                sources = sources.Add(new WinPoolSource("synthetic-empty:" + className,
                    FactOrigin.StorageCim, "root/microsoft/windows/storage", className,
                    Now, CollectionPurpose.Storage));
            }
            var objects = facts.Objects.Select(item =>
            {
                if (item.ObjectType == FactObjectType.Disk)
                    return item with { Fields = item.Fields
                        .Add(WinPoolSourceField.Returned("Path", DiskProviderPath,
                            FactValueType.String, item.SourceRef))
                        .Add(WinPoolSourceField.Returned("Guid", "14289e19-c40f-4f16-a62d-e4e15afc6a52",
                            FactValueType.String, item.SourceRef))
                        .Add(WinPoolSourceField.Returned("IsClustered", false,
                            FactValueType.Boolean, item.SourceRef)) };
                if (item.Id == DataId && (targetShadowCopy || replaceTargetReadOnly))
                {
                    var fields = item.Fields;
                    if (targetShadowCopy)
                    {
                        var selectedShadow = item.Field("IsShadowCopy")!;
                        fields = fields.Replace(selectedShadow,
                            WinPoolSourceField.Returned("IsShadowCopy", true,
                                FactValueType.Boolean, item.SourceRef));
                    }
                    if (replaceTargetReadOnly)
                    {
                        var selectedReadOnly = item.Field("IsReadOnly")!;
                        fields = fields.Replace(selectedReadOnly,
                            WinPoolSourceField.Returned("IsReadOnly", targetReadOnlyState,
                                FactValueType.Boolean, item.SourceRef));
                    }
                    return item with { Fields = fields };
                }
                if (item.Id != EfiId || !siblingShadowCopy) return item;
                var shadow = item.Field("IsShadowCopy")!;
                return item with { Fields = item.Fields.Replace(shadow,
                    WinPoolSourceField.Returned("IsShadowCopy", true,
                        FactValueType.Boolean, item.SourceRef)) };
            }).Select(item =>
            {
                if (item.ObjectType != FactObjectType.Partition) return item;
                var fields = item.Fields;
                var diskId = item.Field("DiskId")!;
                fields = fields.Replace(diskId, WinPoolSourceField.Returned("DiskId",
                    DiskProviderPath, FactValueType.String, item.SourceRef));
                if (item.Id != DataId) return item with { Fields = fields };
                foreach (var (name, value) in rawTargetFields)
                {
                    var old = fields.Single(field => field.Name == name);
                    fields = fields.Replace(old, WinPoolSourceField.Returned(name, value, old.ValueType, item.SourceRef));
                }
                fields = fields.Where(field => !removedTargetFields.Contains(field.Name)).ToImmutableArray();
                return item with { Fields = fields };
            }).Select(item => item with
            {
                Fields = item.Fields.Where(field => !removedObjectFields.Contains((item.Id, field.Name))).ToImmutableArray()
            }).ToImmutableArray();
            facts = facts with
            {
                IsSimulation = false, InventoryVersion = "synthetic-inventory",
                InventoryCapturedAt = Now, Sources = sources, Objects = objects,
                Relationships = facts.Relationships.Where(item => !removedRelationshipKinds.Contains(item.Kind)).ToImmutableArray()
            };
            return new StorageSystemDocument(StorageSystemDocument.CurrentSchemaVersion,
                "local:synthetic", StorageSystemKind.Local, "Synthetic Storage", facts, [], Now)
            { SystemId = system };
        }

        private static StorageSnapshot InitialSnapshot()
        {
            var data = new PartitionInfo(DataId, true, 7, 2, "Primary",
                17L << 20, 128L << 20, false, false, "E", "Data", "NTFS",
                65536, 64L << 20, "Healthy", "OK", @"E:\", DiskId,
                PartitionTypeId: "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7",
                Guid: "8c4b7c34-04ba-46b1-80d7-9d967441a02d");
            var efi = new PartitionInfo(EfiId, true, 7, 1, "EfiSystem",
                1L << 20, 16L << 20, false, false, "S", "EFI", "FAT32",
                4096, 8L << 20, "Healthy", "OK", @"S:\", DiskId,
                PartitionTypeId: "c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
                Guid: "2f8ae502-1e4e-4d94-b190-6284ccb62bea");
            return StorageSnapshot.Empty(Environment.MachineName) with
            {
                SnapshotVersion = "synthetic-inventory", ScannedAt = Now,
                Computer = new ComputerInfo("system:synthetic", Environment.MachineName,
                    "Synthetic Windows", "10.0", "26100", Now.AddHours(-1)),
                StorageSubsystems = [new StorageSubsystemInfo("subsystem:local", "Storage Spaces", "Healthy", "OK")],
                PhysicalDisks = [new PhysicalDiskInfo(PhysicalId, true, "Synthetic Candidate", "Model",
                    "SERIAL-1", "SATA", "HDD", 1L << 30, 512, 4096, "Healthy", "OK",
                    false, "Partitioned", 7, false, false, false, false, "pool:primordial")],
                StoragePools = [new StoragePoolInfo("pool:primordial", true, "Primordial", true,
                    "Healthy", "OK", 1L << 30, 0, "subsystem:local", [PhysicalId])],
                OsDisks = [new OsDiskInfo(DiskId, "Synthetic Candidate", 7, "GPT", 1L << 30,
                    false, false, false, PhysicalId, null)],
                Partitions = [efi, data],
                Volumes = [new VolumeInfo("volume:efi", true, EfiId, "FAT32", "EFI",
                        16L << 20, 8L << 20, 4096, "Healthy", "OK", [@"S:\"]),
                    new VolumeInfo("volume:data", true, DataId, "NTFS", "Data",
                        128L << 20, 64L << 20, 65536, "Healthy", "OK", [@"E:\"])]
            };
        }
    }

    private sealed class SyntheticFactSource(Func<StorageSystemDocument> capture) : IWindowsRealStorageFactSource
    {
        public Task<StorageSystemDocument> CaptureFreshAsync(CancellationToken token) => Task.FromResult(capture());
    }
    private sealed class SyntheticMachineIdentity : IRealMachineIdentityProvider
    {
        public Task<string> ReadBindingAsync(CancellationToken token) => Task.FromResult("synthetic-machine-binding");
    }
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
