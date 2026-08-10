using System.Reflection;

namespace WinPool.App.Services;

public static class ProductInformation
{
    public const string Name = "WinPool";

    public static string Version =>
        typeof(ProductInformation).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("WinPool display version metadata is unavailable.");

    public static string UserAgent =>
        $"{Name}/{typeof(ProductInformation).Assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
        ?? throw new InvalidOperationException("WinPool file version metadata is unavailable.")}";

    public static readonly Uri WebsiteUri = new("https://github.com/GitRuozhi/WinPool");

    public static readonly Uri UpdateUri = new("https://github.com/GitRuozhi/WinPool/releases");

    public static readonly Uri FeedbackUri = new("https://github.com/GitRuozhi/WinPool/issues");
}
