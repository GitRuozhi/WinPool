using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using WinPool.Infrastructure.Windows;

namespace WinPool.App.Services;

/// <summary>
/// One opt-in UI-thread measurement, not a monitoring/logging service. Disabled
/// calls do not read a clock, sample the GC, serialize, or perform I/O.
/// </summary>
internal static class MonitorUiMeasurement
{
    internal static readonly bool IsEnabled =
        Environment.GetEnvironmentVariable("WINPOOL_MEASURE_MONITOR_UI") == "1";

    private const int WarmupSeconds = 15;
    private const int MeasurementSeconds = 60;
    private static Measurement? _measurement;
    private static bool _completed;

    internal readonly record struct PollMeasurement(long Started, long AllocatedBytes, bool Active);

    internal static PollMeasurement BeginPoll()
    {
        if (!IsEnabled || _completed) return default;
        var measurement = _measurement ??= new Measurement();
        if (measurement.ThreadId != Environment.CurrentManagedThreadId) return default;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(measurement.Started, now).TotalSeconds;
        if (elapsed >= WarmupSeconds + MeasurementSeconds)
        {
            Complete(measurement, now);
            return default;
        }
        if (elapsed < WarmupSeconds) return default;
        measurement.InPoll = true;
        measurement.PollVisible = null;
        measurement.PollUnchanged = false;
        var visible = measurement.Visible;
        measurement.BeforePoll = (visible.WindowToArrayCopies, visible.WindowCopiedPoints,
            visible.GraphRenderCount, visible.UiElementsCreated, visible.UiElementsRemoved);
        return new(now, GC.GetAllocatedBytesForCurrentThread(), true);
    }

    internal static void PollVisibility(bool visible, double sampleRateHz)
    {
        if (!IsEnabled || _completed || _measurement is not { InPoll: true } measurement) return;
        measurement.PollVisible = visible;
        measurement.MinimumSampleRateHz = Math.Min(measurement.MinimumSampleRateHz, sampleRateHz);
        measurement.MaximumSampleRateHz = Math.Max(measurement.MaximumSampleRateHz, sampleRateHz);
    }

    internal static void EndPoll(PollMeasurement poll)
    {
        if (!poll.Active || _measurement is not { } measurement) return;
        // Capture before bookkeeping and before the single final serialization.
        var allocated = GC.GetAllocatedBytesForCurrentThread() - poll.AllocatedBytes;
        var ended = Stopwatch.GetTimestamp();
        var elapsedMs = Stopwatch.GetElapsedTime(poll.Started, ended).TotalMilliseconds;
        var bucket = measurement.Bucket(measurement.PollVisible);
        bucket.PollCount++;
        bucket.PollElapsedMs += elapsedMs;
        bucket.PollMaxMs = Math.Max(bucket.PollMaxMs, elapsedMs);
        if (elapsedMs > 16) bucket.PollsOver16Ms++;
        bucket.PollAllocatedBytes += allocated;
        bucket.PollMaxAllocatedBytes = Math.Max(bucket.PollMaxAllocatedBytes, allocated);
        bucket.FirstPollAtUtc ??= measurement.UtcAt(poll.Started);
        bucket.LastPollAtUtc = measurement.UtcAt(poll.Started);
        if (measurement.PollUnchanged)
        {
            bucket.UnchangedWindowToArrayCopies += bucket.WindowToArrayCopies - measurement.BeforePoll.Copies;
            bucket.UnchangedWindowCopiedPoints += bucket.WindowCopiedPoints - measurement.BeforePoll.Points;
            bucket.UnchangedGraphRenderCount += bucket.GraphRenderCount - measurement.BeforePoll.Renders;
            bucket.UnchangedUiElementsCreated += bucket.UiElementsCreated - measurement.BeforePoll.Created;
            bucket.UnchangedUiElementsRemoved += bucket.UiElementsRemoved - measurement.BeforePoll.Removed;
        }
        measurement.InPoll = false;
        if (Stopwatch.GetElapsedTime(measurement.Started, ended).TotalSeconds >= WarmupSeconds + MeasurementSeconds)
            Complete(measurement, ended);
    }

    internal static void WindowCopied(int points)
    {
        if (CurrentBucket() is not { } bucket) return;
        bucket.WindowToArrayCopies++;
        bucket.WindowCopiedPoints += points;
    }

    internal static void PollSameRevision()
    {
        if (CurrentBucket() is not { } bucket) return;
        bucket.PollSameRevisionSkips++;
        _measurement!.PollUnchanged = true;
    }

    internal static void GraphSetSeriesSameRevision(bool visible)
    {
        if (CurrentBucket(visible) is { } bucket) bucket.GraphSetSeriesSameRevisionSkips++;
    }

    internal static void GraphHidden()
    {
        if (CurrentBucket(false) is { } bucket) bucket.GraphHiddenSkips++;
    }

    internal static void GraphRenderedKeyUnchanged()
    {
        if (CurrentBucket(true) is { } bucket) bucket.GraphRenderedKeySkips++;
    }

    internal static void GraphRender()
    {
        if (CurrentBucket(true) is { } bucket) bucket.GraphRenderCount++;
    }

    internal static void GridLineCreated()
    {
        if (CurrentBucket(true) is not { } bucket) return;
        bucket.GridLinesCreated++;
        bucket.UiElementsCreated++;
        bucket.PlotElementsAdded++;
    }

    internal static void SegmentCreated()
    {
        if (CurrentBucket(true) is not { } bucket) return;
        bucket.SegmentsCreated++;
        // Four plot shapes and three flags, each flag a Grid/Polygon/TextBlock.
        bucket.UiElementsCreated += 13;
        bucket.PlotElementsAdded += 7;
    }

    internal static void SegmentRemoved()
    {
        if (CurrentBucket(true) is not { } bucket) return;
        bucket.SegmentsRemoved++;
        bucket.UiElementsRemoved += 13;
        bucket.PlotElementsRemoved += 7;
    }

    private static Counters? CurrentBucket(bool? visible = null)
    {
        if (!IsEnabled || _completed || _measurement is not { } measurement
            || measurement.ThreadId != Environment.CurrentManagedThreadId) return null;
        // A call begun in the interval is measured in full, including a call
        // spanning the end. Size/theme renders outside Poll are counted too.
        if (!measurement.InPoll)
        {
            var elapsed = Stopwatch.GetElapsedTime(measurement.Started).TotalSeconds;
            if (elapsed < WarmupSeconds || elapsed >= WarmupSeconds + MeasurementSeconds) return null;
        }
        return measurement.Bucket(visible ?? measurement.PollVisible);
    }

    private static void Complete(Measurement measurement, long now)
    {
        _completed = true;
        try
        {
            var assembly = typeof(MonitorUiMeasurement).Assembly;
            StorageOperationTiming.Sink(JsonSerializer.Serialize(new
            {
                phase = "monitor.ui.measurement",
                scope = "monitor",
                processId = Environment.ProcessId,
                uiThreadId = measurement.ThreadId,
                buildId = assembly.ManifestModule.ModuleVersionId,
                buildVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                assemblyVersion = assembly.GetName().Version?.ToString(),
                warmupSeconds = WarmupSeconds,
                measurementSeconds = MeasurementSeconds,
                warmupStartedAtUtc = measurement.StartedAtUtc,
                measurementStartedAtUtc = measurement.StartedAtUtc.AddSeconds(WarmupSeconds),
                measurementEndedAtUtc = measurement.StartedAtUtc.AddSeconds(WarmupSeconds + MeasurementSeconds),
                reportedAtUtc = measurement.UtcAt(now),
                sampleRateHzMinimum = double.IsFinite(measurement.MinimumSampleRateHz) ? measurement.MinimumSampleRateHz : (double?)null,
                sampleRateHzMaximum = double.IsFinite(measurement.MaximumSampleRateHz) ? measurement.MaximumSampleRateHz : (double?)null,
                allocationScope = "GC.GetAllocatedBytesForCurrentThread around actual PollCore; managed allocations only",
                callWindow = "PollCore calls beginning in the 60-second window, measured to completion",
                elementCounting = "Created/detached UI elements; each segment has 7 Canvas children and 13 elements including flag descendants. Removal is not GC reclamation.",
                visible = measurement.Visible,
                hidden = measurement.Hidden,
                unattributed = measurement.Unattributed
            }));
        }
        catch (Exception)
        {
            // Optional evidence must not change a timer callback or its result.
        }
    }

    private sealed class Measurement
    {
        internal long Started { get; } = Stopwatch.GetTimestamp();
        internal DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        internal int ThreadId { get; } = Environment.CurrentManagedThreadId;
        internal bool InPoll { get; set; }
        internal bool? PollVisible { get; set; }
        internal bool PollUnchanged { get; set; }
        internal (long Copies, long Points, long Renders, long Created, long Removed) BeforePoll { get; set; }
        internal double MinimumSampleRateHz { get; set; } = double.PositiveInfinity;
        internal double MaximumSampleRateHz { get; set; } = double.NegativeInfinity;
        internal Counters Visible { get; } = new();
        internal Counters Hidden { get; } = new();
        internal Counters Unattributed { get; } = new();
        internal Counters Bucket(bool? visible) => visible is true ? Visible : visible is false ? Hidden : Unattributed;
        internal DateTimeOffset UtcAt(long timestamp) => StartedAtUtc + Stopwatch.GetElapsedTime(Started, timestamp);
    }

    private sealed class Counters
    {
        public long PollCount { get; set; }
        public double PollElapsedMs { get; set; }
        public double PollMeanMs => PollCount == 0 ? 0 : PollElapsedMs / PollCount;
        public double PollMaxMs { get; set; }
        public long PollsOver16Ms { get; set; }
        public long PollAllocatedBytes { get; set; }
        public long PollMaxAllocatedBytes { get; set; }
        public DateTimeOffset? FirstPollAtUtc { get; set; }
        public DateTimeOffset? LastPollAtUtc { get; set; }
        public long WindowToArrayCopies { get; set; }
        public long WindowCopiedPoints { get; set; }
        public long PollSameRevisionSkips { get; set; }
        public long UnchangedWindowToArrayCopies { get; set; }
        public long UnchangedWindowCopiedPoints { get; set; }
        public long UnchangedGraphRenderCount { get; set; }
        public long UnchangedUiElementsCreated { get; set; }
        public long UnchangedUiElementsRemoved { get; set; }
        public long GraphSetSeriesSameRevisionSkips { get; set; }
        public long GraphHiddenSkips { get; set; }
        public long GraphRenderedKeySkips { get; set; }
        public long GraphRenderCount { get; set; }
        public long GridLinesCreated { get; set; }
        public long SegmentsCreated { get; set; }
        public long SegmentsRemoved { get; set; }
        public long UiElementsCreated { get; set; }
        public long UiElementsRemoved { get; set; }
        public long PlotElementsAdded { get; set; }
        public long PlotElementsRemoved { get; set; }
    }
}
