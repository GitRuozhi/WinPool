namespace WinPool.Application;

public sealed record WelcomeMascotAsset(string Key, string Title);

public static class WelcomeMascotCatalog
{
    public static IReadOnlyList<WelcomeMascotAsset> Assets { get; } =
    [
        FromFileName("00-随机切换.png"),
        FromFileName("01-气笑了.png"),
        FromFileName("02-心作痛.png"),
        FromFileName("03-苦酒入喉.png"),
        FromFileName("04-早点毁灭吧.png"),
        FromFileName("05-这世界还能不能好了.png"),
        FromFileName("06-哈哈哈哈哈哈哈嗝.png"),
        FromFileName("07-绷不住了.png")
    ];

    public static WelcomeMascotAsset FromFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var separator = stem.IndexOf('-');
        if (separator <= 0 || separator == stem.Length - 1)
        {
            throw new ArgumentException(
                "Welcome mascot file names must be '{key}-{title}.png'.",
                nameof(fileName));
        }

        return new WelcomeMascotAsset(stem[..separator], stem[(separator + 1)..]);
    }

    public static WelcomeMascotAsset ByKey(string key) =>
        Assets.FirstOrDefault(asset => asset.Key == key) ?? Assets[0];

    public static string NextKey(string currentKey)
    {
        var index = 0;
        for (var i = 0; i < Assets.Count; i++)
        {
            if (Assets[i].Key == currentKey)
            {
                index = i;
                break;
            }
        }

        return Assets[(index + 1) % Assets.Count].Key;
    }

    public static string RandomKey(string currentKey)
    {
        var candidates = Assets.Where(asset => asset.Key != currentKey).ToList();
        return candidates.Count == 0
            ? currentKey
            : candidates[Random.Shared.Next(candidates.Count)].Key;
    }
}

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
