using WinPool.Domain;

namespace WinPool.Domain.Tests;

public sealed class MaximumCapacityAlgorithmTests
{
    [Fact]
    public void InitialCandidateUsesTheStrictDecimalReserveAndWholeGibRule()
    {
        var reserve = MaximumCapacityAlgorithm.ReserveBytes;
        var gib = MaximumCapacityAlgorithm.GiB;

        Assert.Equal(0, MaximumCapacityAlgorithm.InitialCandidateBytes(reserve + gib));
        Assert.Equal(gib, MaximumCapacityAlgorithm.InitialCandidateBytes(reserve + gib + 1));
        Assert.Equal(2 * gib, MaximumCapacityAlgorithm.InitialCandidateBytes(reserve + 3 * gib));
        Assert.Equal(3 * gib, MaximumCapacityAlgorithm.InitialCandidateBytes(reserve + 3 * gib + 1));
        Assert.Equal(0, MaximumCapacityAlgorithm.InitialCandidateBytes(-1));
    }

    [Fact]
    public void SeedRetainsTheExactHalfOfAnOddGibCandidate()
    {
        var candidate = 5 * MaximumCapacityAlgorithm.GiB;

        Assert.Equal(candidate / 2, MaximumCapacityAlgorithm.SeedBytes(candidate));
        Assert.Equal(2 * MaximumCapacityAlgorithm.GiB + MaximumCapacityAlgorithm.GiB / 2,
            MaximumCapacityAlgorithm.SeedBytes(candidate));
        Assert.Equal(0, MaximumCapacityAlgorithm.SeedBytes(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaximumCapacityAlgorithm.SeedBytes(-1));
    }

    [Fact]
    public void SearchAfterFirstSuccessStepsUpUntilTheFirstFailure()
    {
        var gib = MaximumCapacityAlgorithm.GiB;
        var state = MaximumCapacitySearchState.Start(3 * gib);

        Assert.Equal(3 * gib, state.CandidateBytes);
        state.Observe(success: true);
        Assert.False(state.IsComplete);
        Assert.Equal(3 * gib, state.LastSuccessfulBytes);
        Assert.Equal(4 * gib, state.CandidateBytes);
        Assert.True(state.CandidateBytes > state.LastSuccessfulBytes);

        state.Observe(success: true);
        Assert.Equal(4 * gib, state.LastSuccessfulBytes);
        Assert.Equal(5 * gib, state.CandidateBytes);
        state.Observe(success: false);

        Assert.True(state.IsComplete);
        Assert.True(state.HasMaximum);
        Assert.Equal(4 * gib, state.LastSuccessfulBytes);
        Assert.Equal(0, state.CandidateBytes);
        Assert.Throws<InvalidOperationException>(() => state.Observe(success: true));
    }

    [Fact]
    public void SearchAfterFirstFailureStepsDownUntilTheFirstSuccess()
    {
        var gib = MaximumCapacityAlgorithm.GiB;
        var state = MaximumCapacitySearchState.Start(3 * gib);

        state.Observe(success: false);
        Assert.False(state.IsComplete);
        Assert.Equal(2 * gib, state.CandidateBytes);
        Assert.True(state.CandidateBytes > state.LastSuccessfulBytes);

        state.Observe(success: false);
        Assert.Equal(gib, state.CandidateBytes);
        state.Observe(success: true);

        Assert.True(state.IsComplete);
        Assert.True(state.HasMaximum);
        Assert.Equal(gib, state.LastSuccessfulBytes);
        Assert.Equal(0, state.CandidateBytes);
    }

    [Fact]
    public void FailedDescentWithoutAnyPositiveCandidateHasNoMaximum()
    {
        var state = MaximumCapacitySearchState.Start(MaximumCapacityAlgorithm.GiB);

        state.Observe(success: false);

        Assert.True(state.IsComplete);
        Assert.False(state.HasMaximum);
        Assert.Equal(0, state.LastSuccessfulBytes);
        Assert.Equal(0, state.CandidateBytes);
    }

    [Fact]
    public void ZeroInitialCandidateCompletesWithoutAResizeOrMaximum()
    {
        var state = MaximumCapacitySearchState.Start(0);

        Assert.True(state.IsComplete);
        Assert.False(state.HasMaximum);
        Assert.Equal(0, state.CandidateBytes);
        Assert.Equal(0, state.LastSuccessfulBytes);
    }

    [Fact]
    public void FractionalSuccessfulSeedIsNeverReportedAsTheFinalMaximum()
    {
        var gib = MaximumCapacityAlgorithm.GiB;
        var seed = 5 * gib / 2;
        var state = MaximumCapacitySearchState.Start(5 * gib, seed);

        state.Observe(success: false);
        Assert.Equal(4 * gib, state.CandidateBytes);
        state.Observe(success: false);
        Assert.Equal(3 * gib, state.CandidateBytes);
        state.Observe(success: false);

        Assert.True(state.IsComplete);
        Assert.False(state.HasMaximum);
        Assert.Equal(seed, state.LastSuccessfulBytes);
        Assert.Equal(0, state.CandidateBytes);
    }

    [Fact]
    public void WholeGibSuccessfulSeedCanBeTheMaximumAfterHigherCandidatesFail()
    {
        var gib = MaximumCapacityAlgorithm.GiB;
        var state = MaximumCapacitySearchState.Start(5 * gib, 2 * gib);

        state.Observe(success: false);
        state.Observe(success: false);
        state.Observe(success: false);

        Assert.True(state.IsComplete);
        Assert.True(state.HasMaximum);
        Assert.Equal(2 * gib, state.LastSuccessfulBytes);
    }

    [Fact]
    public void SearchRejectsAResizeCandidateNotAboveTheLastSuccess()
    {
        var gib = MaximumCapacityAlgorithm.GiB;
        Assert.Throws<ArgumentOutOfRangeException>(() => MaximumCapacitySearchState.Start(gib, gib));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaximumCapacitySearchState.Start(-gib));
    }
}
