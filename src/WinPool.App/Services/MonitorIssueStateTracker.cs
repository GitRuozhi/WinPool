namespace WinPool.App.Services;

/// <summary>
/// A page-independent monitoring fact. Permanent gaps never emit an automatic
/// recovery; a transport gap never proves recovery.
/// </summary>
public sealed record MonitorIssueState(
    string Key,
    string Text,
    bool IsPermanentGap = false);

public enum MonitorIssueTransitionKind
{
    Appeared,
    Recovered
}

public sealed record MonitorIssueTransition(
    MonitorIssueState Issue,
    MonitorIssueTransitionKind Kind);

public sealed record MonitorIssueStateSnapshot(
    IReadOnlyList<MonitorIssueState> ActiveIssues,
    IReadOnlyList<MonitorIssueTransition> Transitions);

/// <summary>
/// Bounded, UI-free session tracker for monitor issue appearance and recovery.
/// Missing facts only prove recovery when the caller has an authoritative
/// snapshot; permanent recording gaps are never recovered automatically.
/// </summary>
public sealed class MonitorIssueStateTracker : IDisposable
{
    private const int MaxIssues = 64;
    private const int MaxKeyLength = 256;
    private const int MaxTextLength = 2048;
    private readonly object _sync = new();
    private readonly Dictionary<string, MonitorIssueState> _issues =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _issueOrder = new(StringComparer.Ordinal);
    private long _nextOrder;

    public MonitorIssueStateSnapshot Update(
        IEnumerable<MonitorIssueState> observedIssues,
        bool stateIsAuthoritative)
    {
        ArgumentNullException.ThrowIfNull(observedIssues);

        var observed = new Dictionary<string, MonitorIssueState>(StringComparer.Ordinal);
        foreach (var candidate in observedIssues)
        {
            var key = Normalize(candidate.Key, MaxKeyLength);
            var text = Normalize(candidate.Text, MaxTextLength);
            if (key.Length == 0 || text.Length == 0)
            {
                continue;
            }

            if (observed.Count >= MaxIssues && !observed.ContainsKey(key))
            {
                continue;
            }

            observed[key] = new MonitorIssueState(key, text, candidate.IsPermanentGap);
        }

        lock (_sync)
        {
            var transitions = new List<MonitorIssueTransition>();
            foreach (var (key, issue) in observed)
            {
                if (!_issues.TryGetValue(key, out var previous))
                {
                    if (_issues.Count >= MaxIssues)
                    {
                        // Do not let old irreversible gaps consume the bounded
                        // tracker forever. Their notifications remain active;
                        // this only retires transition bookkeeping so a later
                        // session's actual issue can still be reported.
                        var retired = _issues
                            .OrderBy(item => item.Value.IsPermanentGap ? 0 : 1)
                            .ThenBy(item => _issueOrder.GetValueOrDefault(item.Key))
                            .FirstOrDefault();
                        if (retired.Key is null)
                        {
                            continue;
                        }

                        _issues.Remove(retired.Key);
                        _issueOrder.Remove(retired.Key);
                    }

                    _issues[key] = issue;
                    _issueOrder[key] = ++_nextOrder;
                    transitions.Add(new MonitorIssueTransition(issue, MonitorIssueTransitionKind.Appeared));
                    continue;
                }

                _issues[key] = issue with
                {
                    IsPermanentGap = issue.IsPermanentGap || previous.IsPermanentGap
                };
            }

            if (stateIsAuthoritative)
            {
                foreach (var (key, issue) in _issues.ToArray())
                {
                    if (!observed.ContainsKey(key) && !issue.IsPermanentGap)
                    {
                        _issues.Remove(key);
                        _issueOrder.Remove(key);
                        transitions.Add(new MonitorIssueTransition(issue, MonitorIssueTransitionKind.Recovered));
                    }
                }
            }

            return new MonitorIssueStateSnapshot(_issues.Values.ToArray(), transitions);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _issues.Clear();
            _issueOrder.Clear();
        }
    }

    private static string Normalize(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }
}
