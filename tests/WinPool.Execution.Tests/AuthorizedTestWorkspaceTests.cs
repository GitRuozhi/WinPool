using WinPool.Execution;

namespace WinPool.Execution.Tests;

public sealed class AuthorizedTestWorkspaceTests
{
    [Fact]
    public void ResolvesOnlyExactlyRegisteredDescendantFiles()
    {
        using var directory = TemporaryDirectory.Create();
        var workspace = new AuthorizedTestWorkspace(
            directory.Path,
            [Path.Combine("run-1", "data.bin")]);

        var resolved = workspace.ResolveRegisteredPath(Path.Combine("run-1", "data.bin"));

        Assert.StartsWith(directory.Path, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<UnauthorizedAccessException>(
            () => workspace.ResolveRegisteredPath(Path.Combine("run-1", "other.bin")));
        Assert.Throws<UnauthorizedAccessException>(
            () => workspace.ResolveRegisteredPath(Path.Combine("..", "escape.bin")));
        Assert.Throws<UnauthorizedAccessException>(
            () => workspace.ResolveRegisteredPath(Path.Combine(directory.Path, "rooted.bin")));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL.bin")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void RejectsWindowsDeviceAndAmbiguousNames(string relativePath)
    {
        using var directory = TemporaryDirectory.Create();
        Assert.Throws<UnauthorizedAccessException>(
            () => new AuthorizedTestWorkspace(directory.Path, [relativePath]));
    }

    [Fact]
    public void RejectsReparsePointInExistingTargetChainWhenPlatformPermitsCreation()
    {
        using var directory = TemporaryDirectory.Create();
        var outside = System.IO.Directory.CreateDirectory(
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinPool.Execution.Tests.Outside",
                Guid.NewGuid().ToString("N")));
        var link = System.IO.Path.Combine(directory.Path, "linked");

        try
        {
            try
            {
                System.IO.Directory.CreateSymbolicLink(link, outside.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                // Windows reports missing symbolic-link privilege as IOException
                // on machines without Developer Mode or SeCreateSymbolicLinkPrivilege.
                return;
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }

            Assert.Throws<UnauthorizedAccessException>(
                () => new AuthorizedTestWorkspace(
                    directory.Path,
                    [Path.Combine("linked", "data.bin")]));
        }
        finally
        {
            if (System.IO.Directory.Exists(link))
            {
                System.IO.Directory.Delete(link);
            }

            if (outside.Exists)
            {
                outside.Delete(recursive: true);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WinPool.Execution.Tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Path))
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
        }
    }
}
