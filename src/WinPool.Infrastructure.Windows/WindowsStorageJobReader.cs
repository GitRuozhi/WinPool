using System.Management;

namespace WinPool.Infrastructure.Windows;

public sealed record WindowsStorageJobObservation(string UniqueId, string ObjectId, ushort JobState);
public sealed record WindowsStorageJobAbsenceEvidence(DateTimeOffset ObservedAtUtc,
    IReadOnlyList<WindowsStorageJobObservation> Jobs);

public interface IWindowsStorageJobReader
{
    Task<WindowsStorageJobAbsenceEvidence> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Fixed, read-only enumeration used only to reconcile a synchronously returned
/// creation error. Require every global job to be terminal. Historical failed
/// jobs are recorded without attributing them to this operation by their name.
/// </summary>
public sealed class WindowsStorageJobReader : IWindowsStorageJobReader
{
    public Task<WindowsStorageJobAbsenceEvidence> ReadAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"\\.\root\Microsoft\Windows\Storage"),
            new ObjectQuery("SELECT * FROM MSFT_StorageJob"),
            new System.Management.EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(10) });
        using var results = searcher.Get();
        var jobs = new List<WindowsStorageJobObservation>();
        foreach (ManagementObject job in results)
        {
            using (job)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (job["UniqueId"] is not string { Length: > 0 } uniqueId
                    || job["ObjectId"] is not string { Length: > 0 } objectId
                    || job["JobState"] is not ushort state)
                    throw new InvalidDataException("A storage job identity or state is unavailable.");
                jobs.Add(new(uniqueId, objectId, state));
            }
        }
        return new WindowsStorageJobAbsenceEvidence(DateTimeOffset.UtcNow, jobs);
    }, cancellationToken);
}
