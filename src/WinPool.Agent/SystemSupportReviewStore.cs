using System.Collections.Concurrent;
using WinPool.Application;

namespace WinPool.Agent;

public sealed record PendingSystemSupportReview(
    Guid ReviewId,
    ElevatedBrokerExecutionRequest ExecutionRequest,
    DateTimeOffset ExpiresAtUtc);

public sealed class SystemSupportReviewStore
{
    private readonly ConcurrentDictionary<Guid, PendingSystemSupportReview> reviews = new();

    public PendingSystemSupportReview Create(
        ElevatedBrokerExecutionRequest executionRequest,
        DateTimeOffset nowUtc,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(executionRequest);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var review = new PendingSystemSupportReview(
            Guid.NewGuid(),
            executionRequest,
            nowUtc.Add(lifetime));
        if (!reviews.TryAdd(review.ReviewId, review))
        {
            throw new InvalidOperationException("A unique review ID could not be allocated.");
        }

        return review;
    }

    public bool TryTake(
        Guid reviewId,
        DateTimeOffset nowUtc,
        out PendingSystemSupportReview? review,
        out string code)
    {
        review = null;
        if (reviewId == Guid.Empty || !reviews.TryRemove(reviewId, out review))
        {
            code = "system-support.review-missing";
            return false;
        }

        if (review.ExpiresAtUtc <= nowUtc)
        {
            code = "system-support.review-expired";
            return false;
        }

        code = "system-support.review-consumed";
        return true;
    }
}
