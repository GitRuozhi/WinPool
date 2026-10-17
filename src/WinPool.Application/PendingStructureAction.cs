using System.Globalization;
using WinPool.Application;

using WinPool.Domain;

namespace WinPool.Application;

public sealed record PendingStructureParameter(string Key, string Value);
public sealed record PendingStructureAction(string Title, IReadOnlyList<PendingStructureParameter> Parameters,
    string? Reason = null, bool DataLoss = false, bool IsChild = false)
{
    public static PendingStructureParameter Parameter(string key, object? value) =>
        new(key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");

    public static PendingStructureAction FromSimulation(SimulationPlanItem item, string title)
    {
        var request = item.Request;
        var parameters = new List<PendingStructureParameter>();
        void Add(string key, object? value) { if (value is not null) parameters.Add(Parameter(key, value)); }
        Add("Target", request.TargetProviderKey);
        Add("Name", request.Name);
        Add("VirtualDiskName", request.VirtualDiskName);
        Add("Members", request.MemberDiskIds is { Count: > 0 } members ? string.Join(", ", members) : null);
        Add("Usage", request.DiskUsage);
        Add("SizeBytes", request.SizeBytes);
        Add("OffsetBytes", request.OffsetBytes);
        Add("PartitionStyle", request.PartitionStyle);
        Add("CreateMsr", request.CreateMsr);
        Add("FileSystem", request.FileSystem);
        Add("AllocationUnitSize", request.AllocationUnitSize);
        Add("VolumeName", request.VolumeName);
        Add("DriveLetter", request.DriveLetter);
        Add("QuickFormat", request.QuickFormat);
        Add("Resiliency", request.Resiliency);
        Add("Interleave", request.InterleaveBytes);
        Add("Provisioning", request.ProvisioningType);
        Add("CreatePartition", request.CreatePartition);
        Add("CreateVirtualDisk", request.CreateVirtualDisk);
        Add("Offline", request.Offline);
        Add("PartitionKind", request.PartitionKind);
        Add("Destination", request.DestinationGroupId);
        Add("AccessPaths", request.AccessPaths is { Count: > 0 } paths ? string.Join(", ", paths) : null);
        AddTier("SSD", request.PerformanceResiliency, request.PerformanceColumns, request.PerformanceInterleaveBytes,
            request.PerformanceDataCopies, request.PerformanceToleratedFailures, request.PerformanceSizeBytes, request.PerformanceUseMaximum);
        AddTier("HDD", request.CapacityResiliency, request.CapacityColumns, request.CapacityInterleaveBytes,
            request.CapacityDataCopies, request.CapacityToleratedFailures, request.CapacitySizeBytes, request.CapacityUseMaximum);
        AddTier("SCM", request.ScmResiliency, request.ScmColumns, request.ScmInterleaveBytes,
            request.ScmDataCopies, request.ScmToleratedFailures, request.ScmSizeBytes, request.ScmUseMaximum);
        void AddTier(string media, string? resiliency, int? columns, long? interleave, int? copies, int? failures, long? size, bool max)
        {
            if (resiliency is null && size is null && columns is null && !max) return;
            Add("Media", media); Add("Resiliency", resiliency); Add("Columns", columns); Add("Interleave", interleave);
            Add("Copies", copies); Add("Failures", failures); Add("SizeBytes", size); if (max) Add("Maximum", true);
        }
        foreach (var usage in request.MemberDiskUsages ?? []) Add("MemberUsage", $"{usage.DiskId}: {usage.Usage}");
        var reason = item.Decision?.Verdict == StorageRuleVerdict.Allow ? null : item.Decision?.Message;
        return new(title, parameters, reason, item.CausesDataLoss, item.ParentId is not null);
    }
}
