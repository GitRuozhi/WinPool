using WinPool.Infrastructure.Windows;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsPowerPlanCatalogTests
{
    [Fact]
    public async Task ListsLocalizedPowerPlansUsingFixedPowerCfgCommand()
    {
        var runner = new Runner(
            """
            电源方案 GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (平衡) *
            电源方案 GUID: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (高性能)
            """);
        var catalog = new WindowsPowerPlanCatalog(runner, @"C:\Windows");

        var plans = await catalog.ListAsync(CancellationToken.None);

        Assert.Equal(2, plans.Count);
        Assert.True(plans[0].IsActive);
        Assert.Equal("高性能", plans[1].DisplayName);
        Assert.Equal(
            [
                @"C:\Windows\System32\powercfg.exe",
                "/list"
            ],
            runner.Invocation);
    }

    private sealed class Runner(string output) : IWindowsCommandRunner
    {
        public List<string> Invocation { get; } = [];

        public Task<WindowsCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocation.Add(executablePath);
            Invocation.AddRange(arguments);
            return Task.FromResult(new WindowsCommandResult(0, output, string.Empty));
        }
    }
}
