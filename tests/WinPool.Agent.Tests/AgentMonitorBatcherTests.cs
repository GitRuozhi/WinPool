using System.Threading.Channels;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Agent.Tests;

public sealed class AgentMonitorBatcherTests
{
    [Fact]
    public async Task PartialBatchFlushesWithoutWaitingForAnotherSampleOrPersistence()
    {
        var samples = Channel.CreateUnbounded<MonitorSample>();
        var delivered = Channel.CreateUnbounded<AgentMonitorBatchEvent>();
        var sessionId = SessionId.New();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var forwarding = AgentMonitorBatcher.ForwardAsync(new(Guid.NewGuid()), sessionId,
            samples.Reader.ReadAllAsync(timeout.Token), batch => delivered.Writer.TryWrite(batch), timeout.Token);
        samples.Writer.TryWrite(Sample(sessionId, 1));

        var first = await delivered.Reader.ReadAsync(timeout.Token);
        Assert.Single(first.Samples);
        Assert.Equal(1, first.FirstSequence);
        Assert.Equal(1, first.LastSequence);
        Assert.False(first.HasGap);
        Assert.False(forwarding.IsCompleted);
        samples.Writer.TryComplete();
        await forwarding;
    }

    [Fact]
    public async Task CountBoundAndUpstreamGapsSurviveBatching()
    {
        var sessionId = SessionId.New();
        var samples = Channel.CreateUnbounded<MonitorSample>();
        for (var index = 1; index <= 300; index++)
            samples.Writer.TryWrite(Sample(sessionId, index < 100 ? index : index + 2));
        samples.Writer.TryComplete();
        var batches = new List<AgentMonitorBatchEvent>();
        await AgentMonitorBatcher.ForwardAsync(new(Guid.NewGuid()), sessionId,
            samples.Reader.ReadAllAsync(), batches.Add, CancellationToken.None);

        Assert.All(batches, batch => Assert.InRange(batch.Samples.Count, 1, AgentMonitorBatcher.MaximumSamples));
        Assert.Equal(300, batches.Sum(batch => batch.Samples.Count));
        Assert.Equal(302, batches[^1].LastSequence);
        Assert.Single(batches, batch => batch.HasGap);
    }

    private static MonitorSample Sample(SessionId session, long sequence) =>
        new(session, new(SystemId.New(), StorageObjectKind.PhysicalDisk, "synthetic"),
            DateTimeOffset.UtcNow, [new(MonitorMetricKind.ActiveTimePercent, 1)]) { Sequence = sequence };
}
