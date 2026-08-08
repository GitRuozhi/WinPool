using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class SystemSupportReviewStoreTests
{
    [Fact]
    public void ReviewIsBoundToExactRequestAndCanBeConsumedOnlyOnce()
    {
        var store = new SystemSupportReviewStore();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var request = Request();
        var review = store.Create(request, now, TimeSpan.FromMinutes(2));

        Assert.True(
            store.TryTake(
                review.ReviewId,
                now.AddSeconds(1),
                out var consumed,
                out var code));
        Assert.Equal("system-support.review-consumed", code);
        Assert.Same(request, consumed!.ExecutionRequest);
        Assert.False(
            store.TryTake(
                review.ReviewId,
                now.AddSeconds(2),
                out _,
                out code));
        Assert.Equal("system-support.review-missing", code);
    }

    [Fact]
    public void ExpiredReviewIsRejectedAndRemoved()
    {
        var store = new SystemSupportReviewStore();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var review = store.Create(Request(), now, TimeSpan.FromSeconds(1));

        Assert.False(
            store.TryTake(
                review.ReviewId,
                now.AddSeconds(1),
                out _,
                out var code));
        Assert.Equal("system-support.review-expired", code);
        Assert.False(
            store.TryTake(
                review.ReviewId,
                now.AddSeconds(2),
                out _,
                out code));
        Assert.Equal("system-support.review-missing", code);
    }

    private static ElevatedBrokerExecutionRequest Request() =>
        new(
            Guid.Empty,
            Guid.Empty,
            0,
            string.Empty,
            new string('a', 64),
            DateTimeOffset.FromUnixTimeSeconds(1_800_000_120),
            ElevatedBrokerOperationKind.SetActivePowerPlan,
            PowerPlanId: Guid.NewGuid());
}
