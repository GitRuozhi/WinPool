using System.Text.Json;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class StorageDataLocationsTests
{
    [Fact]
    public void PackagedAgentUsesProductRootPortableDataFromSharedPointer()
    {
        using var directory = TemporaryDirectory.Create();
        var productRoot = Path.Combine(directory.Path, "product");
        var standardRoot = Path.Combine(directory.Path, "standard");
        Directory.CreateDirectory(productRoot);
        Directory.CreateDirectory(standardRoot);
        var pointerPath = Path.Combine(standardRoot, "storage-location.json");
        File.WriteAllText(
            pointerPath,
            "{\"mode\":\"portable\"}");

        var actual = StorageDataLocations.ResolveCurrentRoot(
            productRoot,
            standardRoot,
            pointerPath);

        Assert.Equal(Path.Combine(productRoot, "Data"), actual);
        Assert.DoesNotContain(
            Path.Combine(productRoot, "Agent", "Data"),
            actual,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitStandardPointerOverridesPortableLegacyMarkers()
    {
        using var directory = TemporaryDirectory.Create();
        var productRoot = Path.Combine(directory.Path, "product");
        var portableRoot = Path.Combine(productRoot, "Data");
        var standardRoot = Path.Combine(directory.Path, "standard");
        Directory.CreateDirectory(portableRoot);
        Directory.CreateDirectory(standardRoot);
        File.WriteAllText(Path.Combine(portableRoot, "settings.json"), "{}");
        var pointerPath = Path.Combine(standardRoot, "storage-location.json");
        File.WriteAllText(
            pointerPath,
            JsonSerializer.Serialize(new { Mode = StorageLocationMode.Standard }));

        var actual = StorageDataLocations.ResolveCurrentRoot(
            productRoot,
            standardRoot,
            pointerPath);

        Assert.Equal(standardRoot, actual);
    }

    [Fact]
    public void OutsidePointerKeepsPortableSourceWhenStandardRootWasRenamed()
    {
        using var directory = TemporaryDirectory.Create();
        var productRoot = Path.Combine(directory.Path, "product");
        var portableRoot = Path.Combine(productRoot, "Data");
        var standardRoot = Path.Combine(directory.Path, "standard");
        var rollback = standardRoot + ".winpool-rollback-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(portableRoot);
        Directory.CreateDirectory(rollback);
        File.WriteAllText(Path.Combine(portableRoot, "app-settings.json"), "latest");
        File.WriteAllText(Path.Combine(rollback, "storage-location.json"), "{\"mode\":\"portable\"}");
        var pointerPath = standardRoot + ".storage-location.json";
        File.WriteAllText(pointerPath, "{\"mode\":\"portable\"}");

        Assert.Equal(portableRoot, StorageDataLocations.ResolveCurrentRoot(
            productRoot, standardRoot, pointerPath));
        Assert.True(File.Exists(Path.Combine(rollback, "storage-location.json")));
    }

    [Fact]
    public void OutsidePointerTakesPrecedenceOverLegacyPointerAfterCommit()
    {
        using var directory = TemporaryDirectory.Create();
        var productRoot = Path.Combine(directory.Path, "product");
        var standardRoot = Path.Combine(directory.Path, "standard");
        Directory.CreateDirectory(standardRoot);
        File.WriteAllText(Path.Combine(standardRoot, "storage-location.json"), "{\"mode\":\"portable\"}");
        var pointerPath = standardRoot + ".storage-location.json";
        File.WriteAllText(pointerPath, "{\"mode\":\"standard\"}");

        Assert.Equal(standardRoot, StorageDataLocations.ResolveCurrentRoot(
            productRoot, standardRoot, pointerPath));
    }

    [Fact]
    public void OutsidePointerKeepsStandardSourceWhenPortableTargetWasRenamed()
    {
        using var directory = TemporaryDirectory.Create();
        var productRoot = Path.Combine(directory.Path, "product");
        var portableRoot = Path.Combine(productRoot, "Data");
        var rollback = Path.Combine(productRoot,
            ".Data.winpool-rollback-" + Guid.NewGuid().ToString("N"));
        var standardRoot = Path.Combine(directory.Path, "standard");
        Directory.CreateDirectory(standardRoot);
        Directory.CreateDirectory(rollback);
        File.WriteAllText(Path.Combine(standardRoot, "winpool.db"), "latest standard");
        File.WriteAllText(Path.Combine(rollback, "old.txt"), "retain old target");
        var pointerPath = standardRoot + ".storage-location.json";
        File.WriteAllText(pointerPath, "{\"mode\":\"standard\"}");

        Assert.Equal(standardRoot, StorageDataLocations.ResolveCurrentRoot(
            productRoot, standardRoot, pointerPath));
        Assert.False(Directory.Exists(portableRoot));
        Assert.True(File.Exists(Path.Combine(rollback, "old.txt")));
    }

    [Fact]
    public void DamagedOutsidePointerDoesNotSilentlySelectAnotherRoot()
    {
        using var directory = TemporaryDirectory.Create();
        var productRoot = Path.Combine(directory.Path, "product");
        var standardRoot = Path.Combine(directory.Path, "standard");
        Directory.CreateDirectory(standardRoot);
        File.WriteAllText(Path.Combine(standardRoot, "storage-location.json"), "{\"mode\":\"portable\"}");
        var pointerPath = standardRoot + ".storage-location.json";
        File.WriteAllText(pointerPath, "broken json");

        Assert.Throws<JsonException>(() => StorageDataLocations.ResolveCurrentRoot(
            productRoot, standardRoot, pointerPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinPool.StorageDataLocations.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
