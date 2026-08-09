using WinPool.Domain;

namespace WinPool.Application;

public enum TestPresetScenario
{
    IoBenchmark,
    CopyVerification,
    MixedFileCopyVerification
}

public enum TestPresetVerificationMode
{
    Metadata,
    SampledContent,
    FullHash
}

/// <summary>
/// User-owned reusable test configuration. The target path and one-shot system
/// support selections are intentionally excluded so loading a preset cannot
/// silently authorize a write target or an elevated action.
/// </summary>
public sealed record UserTestPreset(
    Guid PresetId,
    string Name,
    TestPresetScenario Scenario,
    ToolId? ToolId,
    TestPresetVerificationMode VerificationMode,
    int MixedFileCount,
    IoAccessPattern AccessPattern,
    int WritePercentage,
    long FileSizeBytes,
    int BlockSizeBytes,
    int ThreadCount,
    int QueueDepth,
    int DurationSeconds,
    int WarmupSeconds,
    int CooldownSeconds,
    int RepeatCount,
    bool CollectLatency,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public static class UserTestPresetValidator
{
    public static bool IsValid(UserTestPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset.PresetId != Guid.Empty
            && !string.IsNullOrWhiteSpace(preset.Name)
            && preset.Name.Trim().Length <= 80
            && Enum.IsDefined(preset.Scenario)
            && Enum.IsDefined(preset.VerificationMode)
            && Enum.IsDefined(preset.AccessPattern)
            && preset.MixedFileCount is >= 3 and <= 200_000
            && preset.WritePercentage is >= 0 and <= 100
            && preset.FileSizeBytes is >= 10L * 1024 * 1024
                and <= 1024L * 1024 * 1024 * 1024
            && preset.BlockSizeBytes is >= 1024 and <= 16 * 1024 * 1024
            && preset.ThreadCount is >= 1 and <= 256
            && preset.QueueDepth is >= 1 and <= 1024
            && preset.DurationSeconds is >= 1 and <= 86_400
            && preset.WarmupSeconds is >= 0 and <= 3_600
            && preset.CooldownSeconds is >= 0 and <= 3_600
            && preset.RepeatCount is >= 1 and <= 100
            && preset.CreatedAtUtc != default
            && preset.UpdatedAtUtc >= preset.CreatedAtUtc
            && IsToolBindingValid(preset);
    }

    private static bool IsToolBindingValid(UserTestPreset preset) =>
        preset.Scenario switch
        {
            TestPresetScenario.IoBenchmark => preset.ToolId is { } tool
                && tool.Value is "microsoft.diskspd" or "fio",
            TestPresetScenario.CopyVerification => preset.ToolId is null,
            TestPresetScenario.MixedFileCopyVerification => preset.ToolId is null,
            _ => false
        };
}

public interface IUserTestPresetRepository
{
    Task<IReadOnlyList<UserTestPreset>> ListAsync(
        CancellationToken cancellationToken);

    Task<UserTestPreset> SaveAsync(
        UserTestPreset preset,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        Guid presetId,
        CancellationToken cancellationToken);
}
