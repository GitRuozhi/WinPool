using System.Collections.ObjectModel;

namespace WinPool.Core;

public sealed record CommandLogEntry(
    DateTimeOffset At,
    string Source,
    string Command,
    string Output,
    bool Simulated);

public interface ICommandLogService
{
    ReadOnlyObservableCollection<CommandLogEntry> Entries { get; }

    void Log(string source, string command, string output, bool simulated);

    void Clear();
}

public sealed class GlobalCommandLogService : ICommandLogService
{
    private const int MaxEntries = 500;
    private readonly ObservableCollection<CommandLogEntry> _entries = [];

    public ReadOnlyObservableCollection<CommandLogEntry> Entries { get; }

    public GlobalCommandLogService()
    {
        Entries = new ReadOnlyObservableCollection<CommandLogEntry>(_entries);
    }

    public void Log(string source, string command, string output, bool simulated)
    {
        _entries.Add(new CommandLogEntry(DateTimeOffset.Now, source, command, output, simulated));
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }

    public void Clear() => _entries.Clear();
}
