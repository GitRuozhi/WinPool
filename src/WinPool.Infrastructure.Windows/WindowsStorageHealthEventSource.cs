using System.Diagnostics.Eventing.Reader;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>
/// Subscribes read-only to fixed Windows storage channels. No caller-supplied
/// XPath, provider name, channel or event command is accepted.
/// </summary>
public sealed class WindowsStorageHealthEventSource : IStorageHealthEventSource
{
    private const string LevelFilter =
        "*[System[(Level=1 or Level=2 or Level=3)]]";

    private static readonly string[] OperationalChannels =
    [
        "Microsoft-Windows-StorageSpaces-Driver/Operational",
        "Microsoft-Windows-StorageSpaces-SpaceManager/Operational",
        "Microsoft-Windows-ReFS/Operational"
    ];

    public async IAsyncEnumerable<StorageHealthEvent> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<StorageHealthEvent>(
            new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
        var watchers = new List<EventLogWatcher>();
        try
        {
            foreach (var logName in OperationalChannels)
            {
                TryStartWatcher(
                    new EventLogQuery(logName, PathType.LogName, LevelFilter),
                    channel.Writer,
                    watchers);
            }

            TryStartWatcher(
                new EventLogQuery(
                    "System",
                    PathType.LogName,
                    """
                    *[System[
                        (Level=1 or Level=2 or Level=3) and
                        (Provider[@Name='disk'] or
                         Provider[@Name='Ntfs'] or
                         Provider[@Name='ReFS'] or
                         Provider[@Name='stornvme'] or
                         Provider[@Name='storahci'] or
                         Provider[@Name='Microsoft-Windows-StorageSpaces-Driver'])
                    ]]
                    """),
                channel.Writer,
                watchers);
            if (watchers.Count == 0)
            {
                yield break;
            }

            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            foreach (var watcher in watchers)
            {
                watcher.Enabled = false;
                watcher.Dispose();
            }

            channel.Writer.TryComplete();
        }
    }

    private static void TryStartWatcher(
        EventLogQuery query,
        ChannelWriter<StorageHealthEvent> writer,
        ICollection<EventLogWatcher> watchers)
    {
        EventLogWatcher? watcher = null;
        try
        {
            watcher = new EventLogWatcher(query);
            watcher.EventRecordWritten += (_, args) =>
            {
                using var record = args.EventRecord;
                if (record is null)
                {
                    return;
                }

                writer.TryWrite(ToEvent(record));
            };
            watcher.Enabled = true;
            watchers.Add(watcher);
        }
        catch (Exception exception) when (
            exception is EventLogException or UnauthorizedAccessException)
        {
            watcher?.Dispose();
        }
    }

    private static StorageHealthEvent ToEvent(EventRecord record)
    {
        string message;
        try
        {
            message = record.FormatDescription() ?? string.Empty;
        }
        catch (EventLogException)
        {
            message = string.Empty;
        }

        if (message.Length > 8192)
        {
            message = message[..8192];
        }

        return new StorageHealthEvent(
            record.LogName ?? string.Empty,
            record.ProviderName ?? string.Empty,
            record.RecordId,
            record.Id,
            record.Level switch
            {
                1 => StorageHealthEventSeverity.Critical,
                2 => StorageHealthEventSeverity.Error,
                3 => StorageHealthEventSeverity.Warning,
                _ => StorageHealthEventSeverity.Information
            },
            record.TimeCreated is { } created
                ? new DateTimeOffset(created)
                : DateTimeOffset.UtcNow,
            message);
    }
}
