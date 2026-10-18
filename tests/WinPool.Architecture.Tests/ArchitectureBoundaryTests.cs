using System.Xml.Linq;
using WinPool.Application;

namespace WinPool.Architecture.Tests;

public sealed class ArchitectureBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["WinPool.Domain"] = [],
            ["WinPool.Execution"] = ["WinPool.Domain"],
            ["WinPool.Application"] = ["WinPool.Domain", "WinPool.Execution"],
            ["WinPool.Ipc"] = [],
            ["WinPool.Agent.Client"] = ["WinPool.Application", "WinPool.Ipc"],
            ["WinPool.Monitoring"] = ["WinPool.Application", "WinPool.Domain"],
            ["WinPool.Infrastructure.Windows"] = ["WinPool.Application", "WinPool.Domain", "WinPool.Execution"],
            ["WinPool.Infrastructure.Sqlite"] = ["WinPool.Application", "WinPool.Domain", "WinPool.Monitoring"],
            ["WinPool.Agent"] = ["WinPool.Application", "WinPool.Infrastructure.Sqlite", "WinPool.Infrastructure.Windows", "WinPool.Ipc", "WinPool.Monitoring"],
            ["WinPool.App"] = ["WinPool.Agent.Client", "WinPool.Application", "WinPool.Infrastructure.Windows", "WinPool.Infrastructure.Sqlite", "WinPool.Monitoring"]
        };

    [Fact]
    public void NewDomainAndApplicationProjectsFollowApprovedDependencyDirection()
    {
        var root = FindRepositoryRoot();
        var discoveredProjects = Directory.EnumerateFiles(Path.Combine(root, "src"), "WinPool.*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(AllowedReferences.Keys.OrderBy(name => name, StringComparer.Ordinal), discoveredProjects);
        foreach (var (projectName, allowed) in AllowedReferences)
        {
            var projectFile = Path.Combine(root, "src", projectName, $"{projectName}.csproj");
            Assert.True(File.Exists(projectFile), projectFile);

            var references = XDocument.Load(projectFile)
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => Path.GetFileNameWithoutExtension(value!))
                .ToArray();

            Assert.All(
                references,
                reference => Assert.Contains(reference, allowed, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void SoftwareAssetsAreRepositoryContentAndOriginArtworkStaysLocal()
    {
        var root = FindRepositoryRoot();
        var gitIgnore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        var assets = Path.Combine(root, "assets");

        Assert.True(Directory.Exists(assets));
        Assert.NotEmpty(Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories));
        Assert.Contains("/OriginArtWork/", gitIgnore, StringComparison.Ordinal);
        Assert.DoesNotContain("/assets/", gitIgnore, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WinPool.Domain")]
    [InlineData("WinPool.Execution")]
    [InlineData("WinPool.Application")]
    public void PureLayersDoNotReferenceUiDatabasePowershellOrProcessApis(string projectName)
    {
        var root = FindRepositoryRoot();
        var directory = Path.Combine(root, "src", projectName);
        var forbidden = new[]
        {
            "Microsoft.UI.Xaml",
            "Microsoft.Data.Sqlite",
            "System.Management.Automation",
            "PowerShell.Create",
            "System.Diagnostics.Process",
            "Process.Start(",
            "ProcessStartInfo",
            "cmd.exe",
            "diskpart"
        };

        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.All(
            forbidden,
            token => Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinPool.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the WinPool repository root.");
    }
}
