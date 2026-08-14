using WinPool.Application;
using WinPool.Domain;
using WinPool.Testing;

namespace WinPool.Testing.Tests;

public sealed class TestDefinitionFactoryTests
{
    [Fact]
    public void BuildsIoDefinitionWithOneReusableTaskAndOrderedSteps()
    {
        var definition = Create(TestDefinitionScenario.IoBenchmark).Build(CreateWorkload(), 2);

        var task = Assert.Single(definition.Tasks);
        Assert.Equal(TestActionKind.RunIo, task.Action);
        Assert.Equal(["io-001", "io-002"], definition.Schedule.Select(item => item.Id));
        Assert.Equal(["io-001"], definition.Schedule[1].DependsOn);
    }

    [Fact]
    public void BuildsCopyDefinitionWithBoundedCopyBatchParameters()
    {
        var definition = Create(TestDefinitionScenario.CopyVerification).Build(CreateWorkload(), 1);

        Assert.Equal(
            ["generate-source", "copy-001", "verify-001"],
            definition.Schedule.Select(item => item.Id));
        var copy = Assert.Single(definition.Tasks, item => item.Action is TestActionKind.Copy);
        Assert.Equal("131072", copy.Parameters["copyBatchThresholdMiB"].SerializedValue);
        Assert.Equal("10000", copy.Parameters["copyBatchMaximumFiles"].SerializedValue);
    }

    [Fact]
    public void BuildsMixedDirectoryDefinitionWithManifestBound()
    {
        var definition = Create(TestDefinitionScenario.MixedFileCopyVerification).Build(CreateWorkload(), 1);

        Assert.Equal("MixedFileCopyVerification", definition.Parameters["scenario"].SerializedValue);
        Assert.Equal("12", definition.Parameters["targetCount"].SerializedValue);
        Assert.Equal("13", definition.Parameters["maximumFileCount"].SerializedValue);
        Assert.Contains(definition.Tasks, item => item.Name == "generate-mixed-source");
    }

    private static TestDefinitionFactory Create(TestDefinitionScenario scenario) =>
        new(
            new ToolId("microsoft.diskspd"),
            "DiskSpd",
            "Sequential write",
            scenario,
            mixedFileCount: 12,
            RegisteredTestFileVerificationMode.FullHash);

    private static TestWorkload CreateWorkload() =>
        new(
            1024L * 1024 * 1024,
            64 * 1024,
            4,
            8,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            IoAccessPattern.Sequential,
            100,
            SoftwareCacheMode.Enabled,
            WriteThroughMode.Disabled,
            CollectLatency: true);
}
