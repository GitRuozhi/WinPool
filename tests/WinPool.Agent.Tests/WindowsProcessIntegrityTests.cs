namespace WinPool.Agent.Tests;

public sealed class WindowsProcessIntegrityTests
{
    [Theory]
    [InlineData(null, 12288, false)]
    [InlineData(12288, null, false)]
    [InlineData(8192, 12288, false)]
    [InlineData(4096, 8192, false)]
    [InlineData(8192, 8192, true)]
    [InlineData(12288, 8192, true)]
    [InlineData(12288, 12288, true)]
    public void UnknownAndLowerIntegrityCannotControlAgent(int? client, int? server, bool allowed) =>
        Assert.Equal(allowed, WindowsProcessIntegrity.CanControl(client, server));

    [Fact]
    public void CurrentProcessTokenCanBeReadAndMissingProcessFailsClosed()
    {
        Assert.True(WindowsProcessIntegrity.TryRead(Environment.ProcessId) >= 0);
        Assert.Null(WindowsProcessIntegrity.TryRead(-1));
    }
}
