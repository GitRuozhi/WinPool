using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class TestStepOrderingTests
{
    [Theory]
    [InlineData("windows.robocopy", 0, true)]
    [InlineData("windows.robocopy", 7, true)]
    [InlineData("windows.robocopy", 8, false)]
    [InlineData("microsoft.diskspd", 0, true)]
    [InlineData("microsoft.diskspd", 1, false)]
    public void ToolExitAcceptancePreservesRoboCopyBitmaskSemantics(
        string toolId,
        int exitCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopAgentRuntime.IsAcceptedToolExit(
                new ToolId(toolId),
                exitCode));
    }

    [Fact]
    public void StableTopologicalOrderMovesDependenciesBeforeDependents()
    {
        var first = Step("first", []);
        var second = Step("second", ["first"]);
        var independent = Step("independent", []);

        var ordered = DesktopAgentRuntime.OrderStepsForExecution(
            [second, independent, first]);

        Assert.NotNull(ordered);
        Assert.Equal(
            ["independent", "first", "second"],
            ordered.Select(item => item.Id));
    }

    [Fact]
    public void InvalidOrCyclicGraphIsRejected()
    {
        Assert.Null(
            DesktopAgentRuntime.OrderStepsForExecution(
                [Step("a", ["b"]), Step("b", ["a"])]));
        Assert.Null(
            DesktopAgentRuntime.OrderStepsForExecution(
                [Step("a", ["missing"])]));
    }

    private static TestStep Step(
        string id,
        IReadOnlyList<string> dependsOn) =>
        new(
            id,
            TestActionKind.RunIo,
            new ToolId("controlled"),
            null,
            new Dictionary<string, TestParameter>(),
            dependsOn,
            true);
}
