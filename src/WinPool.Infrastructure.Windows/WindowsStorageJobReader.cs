using System.Text.Json;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsStorageJobObservation(string UniqueId, string ObjectId, ushort JobState);
public sealed record WindowsStorageJobAbsenceEvidence(DateTimeOffset ObservedAtUtc,
    IReadOnlyList<WindowsStorageJobObservation> Jobs);

public interface IWindowsStorageJobReader
{
    Task<WindowsStorageJobAbsenceEvidence> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fixed, read-only enumeration for strict creation-error and unchanged-delete
/// observations. Require every global job to be terminal. This does not itself
/// prove a provider return, an unissued call, or no effect. Historical failed
/// jobs are recorded without attributing them to an operation by their name.
/// </summary>
public sealed class WindowsStorageJobReader : IWindowsStorageJobReader
{
    private readonly WindowsPowerShellHost host;
    public WindowsStorageJobReader(WindowsPowerShellHost? host = null) => this.host = host ?? WindowsPowerShellHost.Shared;
    public async Task<WindowsStorageJobAbsenceEvidence> ReadAsync(CancellationToken cancellationToken)
    {
        var json = await host.QueryAsync(WindowsPowerShellQuery.StorageJobs, new { }, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<WindowsStorageJobAbsenceEvidence>(json, WindowsPowerShellHost.JsonOptions)
            ?? throw new InvalidDataException("The storage-job query returned no observation.");
        if (result.ObservedAtUtc == default || result.Jobs is null)
            throw new InvalidDataException("The storage-job query returned an incomplete observation.");
        if (result.Jobs.Any(job => job is null || string.IsNullOrWhiteSpace(job.UniqueId) || string.IsNullOrWhiteSpace(job.ObjectId)))
            throw new InvalidDataException("A storage job identity is unavailable.");
        return result;
    }
}
