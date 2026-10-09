namespace WinPool.Domain;

/// <summary>Shared integer-GiB candidate policy for simulated and native capacity search.</summary>
public static class MaximumCapacityAlgorithm
{
    public const long GiB = 1_073_741_824;
    public const long ReserveBytes = 4_000_000;
    public const string Version = "WINPOOL-MAX-GIB-1";

    /// <summary>
    /// Returns the largest whole-GiB value strictly below the upper bound after
    /// the fixed decimal-byte reserve. A nonpositive result means there is no candidate.
    /// </summary>
    public static long InitialCandidateBytes(long upperBoundBytes)
    {
        if (upperBoundBytes <= ReserveBytes)
        {
            return 0;
        }

        var candidateGiB = (upperBoundBytes - ReserveBytes - 1) / GiB;
        return candidateGiB <= 0 ? 0 : checked(candidateGiB * GiB);
    }

    /// <summary>Returns the exact half-size seed; odd-GiB candidates retain their half-GiB.</summary>
    public static long SeedBytes(long candidateBytes)
    {
        if (candidateBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateBytes));
        }

        return candidateBytes / 2;
    }
}

/// <summary>
/// A monotonic, one-GiB-step search. The first observation selects the direction:
/// success searches upward to the first failure; failure searches downward to the
/// first success. A fractional seed is known to exist but cannot be returned as MAX.
/// </summary>
public sealed class MaximumCapacitySearchState
{
    private bool _searchingUp;
    private bool _directionChosen;
    private bool _hasMaximum;

    private MaximumCapacitySearchState(
        long candidateBytes,
        long lastSuccessfulBytes,
        bool isComplete)
    {
        CandidateBytes = candidateBytes;
        LastSuccessfulBytes = lastSuccessfulBytes;
        IsComplete = isComplete;
    }

    public long CandidateBytes { get; private set; }
    public long LastSuccessfulBytes { get; private set; }
    public bool IsComplete { get; private set; }
    public bool HasMaximum => IsComplete && _hasMaximum;

    public static MaximumCapacitySearchState Start(
        long initialCandidateBytes,
        long lastSuccessfulBytes = 0)
    {
        if (initialCandidateBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCandidateBytes));
        }

        if (lastSuccessfulBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastSuccessfulBytes));
        }

        if (initialCandidateBytes == 0)
        {
            return new(0, lastSuccessfulBytes, isComplete: true);
        }

        if (initialCandidateBytes % MaximumCapacityAlgorithm.GiB != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialCandidateBytes),
                "The initial search candidate must be a whole number of GiB.");
        }

        if (initialCandidateBytes <= lastSuccessfulBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lastSuccessfulBytes),
                "The next candidate must be greater than the last successful size.");
        }

        return new(initialCandidateBytes, lastSuccessfulBytes, isComplete: false);
    }

    public void Observe(bool success)
    {
        if (IsComplete)
        {
            throw new InvalidOperationException("The maximum-capacity search is complete.");
        }

        if (!_directionChosen)
        {
            _searchingUp = success;
            _directionChosen = true;
        }

        if (_searchingUp)
        {
            if (!success)
            {
                Complete(hasMaximum: IsPositiveWholeGiB(LastSuccessfulBytes));
                return;
            }

            LastSuccessfulBytes = CandidateBytes;
            if (CandidateBytes > long.MaxValue - MaximumCapacityAlgorithm.GiB)
            {
                Complete(hasMaximum: false);
                return;
            }

            CandidateBytes += MaximumCapacityAlgorithm.GiB;
            return;
        }

        if (success)
        {
            LastSuccessfulBytes = CandidateBytes;
            Complete(hasMaximum: IsPositiveWholeGiB(LastSuccessfulBytes));
            return;
        }

        var nextCandidate = CandidateBytes - MaximumCapacityAlgorithm.GiB;
        if (nextCandidate <= LastSuccessfulBytes)
        {
            Complete(hasMaximum: IsPositiveWholeGiB(LastSuccessfulBytes));
            return;
        }

        CandidateBytes = nextCandidate;
    }

    private static bool IsPositiveWholeGiB(long bytes) =>
        bytes >= MaximumCapacityAlgorithm.GiB
        && bytes % MaximumCapacityAlgorithm.GiB == 0;

    private void Complete(bool hasMaximum)
    {
        CandidateBytes = 0;
        _hasMaximum = hasMaximum;
        IsComplete = true;
    }
}
