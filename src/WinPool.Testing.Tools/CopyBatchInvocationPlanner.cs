using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Testing.Tools;

public sealed record CopyBatchInvocationItem(
    CopyBatchManifestEntry Entry,
    CopyBatchEntryCheckpoint Checkpoint,
    ToolProcessRequest Request);

public sealed record CopyBatchInvocationGroup(
    CopyBatchSegment Batch,
    IReadOnlyList<CopyBatchInvocationItem> Items);

/// <summary>
/// Converts the remaining entries of an immutable copy manifest into typed,
/// literal RoboCopy requests. It performs no I/O and never changes checkpoint
/// state; the Agent must commit Copying before dispatch and verify afterwards.
/// </summary>
public sealed class CopyBatchInvocationPlanner
{
    public IReadOnlyList<CopyBatchInvocationGroup> Build(
        CopyBatchManifest manifest,
        IReadOnlyList<CopyBatchEntryCheckpoint> checkpoints,
        TestStep step,
        AuthorizedTestWorkspace workspace,
        ToolState tool,
        RoboCopyAdapter adapter,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(adapter);
        if (!CopyBatchManifestHash.IsValid(manifest)
            || !StringComparer.Ordinal.Equals(manifest.StepId, step.Id)
            || step.ToolId != tool.ToolId
            || tool.ToolId.Value is not ToolProcessExitPolicy.RoboCopyToolId)
        {
            throw new InvalidDataException(
                "The copy invocation inputs do not match the immutable manifest and tool.");
        }

        var checkpointByOrdinal = checkpoints.ToDictionary(
            item => item.Ordinal);
        if (checkpointByOrdinal.Count != manifest.Entries.Count
            || checkpoints.Any(item => item.RunId != manifest.RunId
                || !StringComparer.Ordinal.Equals(item.StepId, manifest.StepId))
            || manifest.Entries.Any(
                item => !checkpointByOrdinal.ContainsKey(item.Ordinal)))
        {
            throw new InvalidDataException(
                "The copy checkpoints do not exactly cover the manifest.");
        }

        var entriesByBatch = manifest.Entries
            .GroupBy(item => item.BatchNumber)
            .ToDictionary(item => item.Key, item => item.OrderBy(entry => entry.Ordinal));
        var groups = new List<CopyBatchInvocationGroup>();
        foreach (var batch in manifest.Batches.OrderBy(item => item.BatchNumber))
        {
            if (!entriesByBatch.TryGetValue(batch.BatchNumber, out var entries))
            {
                throw new InvalidDataException(
                    "The copy manifest contains an empty batch.");
            }

            var items = new List<CopyBatchInvocationItem>();
            foreach (var entry in entries)
            {
                var checkpoint = checkpointByOrdinal[entry.Ordinal];
                if (checkpoint.State is CopyBatchEntryState.Completed)
                {
                    continue;
                }

                if (checkpoint.State is not (CopyBatchEntryState.Pending
                    or CopyBatchEntryState.Failed))
                {
                    throw new InvalidOperationException(
                        $"Copy entry {entry.Ordinal} is not dispatchable from state {checkpoint.State}.");
                }

                var invocation = adapter.BuildDirectoryEntryInvocation(
                    step,
                    workspace,
                    entry.RelativePath,
                    correlationId);
                if (!invocation.IsSuccess || invocation.Value is null)
                {
                    throw new InvalidDataException(
                        $"Copy entry {entry.Ordinal} could not be converted to a sealed RoboCopy invocation.");
                }

                items.Add(
                    new(
                        entry,
                        checkpoint,
                        new(
                            manifest.RunId,
                            manifest.StepId,
                            invocation.Value,
                            tool,
                            TimeSpan.FromSeconds(3))));
            }

            if (items.Count > 0)
            {
                groups.Add(new(batch, items));
            }
        }

        return groups;
    }
}
