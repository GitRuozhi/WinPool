namespace WinPool.Application;

/// <summary>
/// Selects the welcome mascot independently from image decoding or window state.
/// The primary mascot deliberately owns 70% of openings; the remaining seven
/// assets share the other 30% uniformly.
/// </summary>
public interface IWelcomeMascotSelector
{
    string SelectAssetKey();
}

public interface IWelcomeRandomSource
{
    int Next(int exclusiveMaximum);
}

public sealed class WelcomeMascotSelector : IWelcomeMascotSelector
{
    private readonly IWelcomeRandomSource random;

    public WelcomeMascotSelector(IWelcomeRandomSource? random = null)
    {
        this.random = random ?? new SharedWelcomeRandomSource();
    }

    public string SelectAssetKey() => random.Next(10) < 7
        ? "00"
        : $"0{random.Next(7) + 1}";

    private sealed class SharedWelcomeRandomSource : IWelcomeRandomSource
    {
        public int Next(int exclusiveMaximum) => Random.Shared.Next(exclusiveMaximum);
    }
}
