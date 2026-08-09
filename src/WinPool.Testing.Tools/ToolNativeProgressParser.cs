using System.Text;
using System.Text.RegularExpressions;
using WinPool.Application;

namespace WinPool.Testing.Tools;

public sealed record ToolNativeProgress(
    TestRunId RunId,
    string StepId,
    ToolId ToolId,
    double Fraction,
    DateTimeOffset OccurredAtUtc,
    string Code);

/// <summary>
/// Extracts only a bounded percentage from native tool output. The original text
/// is never copied to the App event stream because it may contain target paths.
/// </summary>
public sealed partial class ToolNativeProgressParser
{
    private const int MaximumBufferedCharacters = 4_096;
    private static readonly TimeSpan MinimumPublishInterval =
        TimeSpan.FromMilliseconds(250);
    private readonly Dictionary<(TestRunId RunId, string StepId), State> _states = [];

    [GeneratedRegex(
        @"(?<![\d.])(?<percent>\d{1,3}(?:\.\d{1,3})?)\s*%",
        RegexOptions.CultureInvariant)]
    private static partial Regex PercentagePattern();

    public ToolNativeProgress? Consume(
        WorkerEvent workerEvent,
        ToolId toolId,
        ToolOutputEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(workerEvent);
        if (workerEvent.Kind is not (
                WorkerEventKind.StandardOutput or WorkerEventKind.StandardError) ||
            workerEvent.RawBytes.IsEmpty)
        {
            return null;
        }

        var key = (workerEvent.RunId, workerEvent.StepId);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new State();
            _states.Add(key, state);
        }

        state.Text.Append(Decode(workerEvent.RawBytes.Span, encoding));
        if (state.Text.Length > MaximumBufferedCharacters)
        {
            state.Text.Remove(0, state.Text.Length - MaximumBufferedCharacters);
        }

        var matches = PercentagePattern().Matches(state.Text.ToString());
        if (matches.Count == 0 ||
            !double.TryParse(
                matches[^1].Groups["percent"].Value,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var percent) ||
            percent is < 0 or > 100)
        {
            return null;
        }

        var fraction = percent / 100d;
        if (state.LastFraction.HasValue &&
            Math.Abs(state.LastFraction.Value - fraction) < 0.000_001)
        {
            return null;
        }

        if (state.LastPublishedAtUtc.HasValue &&
            workerEvent.OccurredAtUtc - state.LastPublishedAtUtc.Value <
                MinimumPublishInterval &&
            fraction < 1)
        {
            return null;
        }

        state.LastFraction = fraction;
        state.LastPublishedAtUtc = workerEvent.OccurredAtUtc;
        return new(
            workerEvent.RunId,
            workerEvent.StepId,
            toolId,
            fraction,
            workerEvent.OccurredAtUtc,
            $"tool.progress.{SanitizeCode(toolId.Value)}.native");
    }

    public void Complete(TestRunId runId, string stepId) =>
        _states.Remove((runId, stepId));

    private static string Decode(
        ReadOnlySpan<byte> bytes,
        ToolOutputEncoding encoding) =>
        encoding == ToolOutputEncoding.Utf16LittleEndian
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);

    private static string SanitizeCode(string value) =>
        string.Concat(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-'
                ? char.ToLowerInvariant(character)
                : '_'));

    private sealed class State
    {
        public StringBuilder Text { get; } = new();
        public double? LastFraction { get; set; }
        public DateTimeOffset? LastPublishedAtUtc { get; set; }
    }
}
