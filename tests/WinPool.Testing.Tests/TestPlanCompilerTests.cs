using WinPool.Application;
using WinPool.Domain;
using WinPool.Execution;
using WinPool.Testing;

namespace WinPool.Testing.Tests;

public sealed class TestPlanCompilerTests
{
    [Fact]
    public void CompilesTypedFilePlanWithRegisteredRunFileAndStableHash()
    {
        var definition = CreateDefinition();
        var target = CreateTarget(isWriteAllowed: true, availableBytes: 2L * 1024 * 1024 * 1024);
        var result = new TestPlanCompiler().Compile(definition, target, CorrelationId.New());

        Assert.True(result.IsSuccess);
        var plan = Assert.IsType<TestPlan>(result.Value);
        Assert.Equal(1L * 1024 * 1024 * 1024, plan.EstimatedWriteBytes);
        Assert.Single(plan.Workspace.RegisteredFiles);
        Assert.StartsWith(
            Path.Combine("WinPoolRuns", plan.RunId.Value.ToString("N")),
            plan.Workspace.RegisteredFiles[0].RelativePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            plan.Workspace.RegisteredFiles[0].RelativePath,
            plan.Steps[0].Parameters["targetRelativePath"].SerializedValue);
        Assert.Equal(64, plan.PlanHash.Length);
        Assert.DoesNotContain("disk-provider", plan.PlanHash, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsWritablePlanWhenTargetIsReadOnly()
    {
        var result = new TestPlanCompiler().Compile(
            CreateDefinition(),
            CreateTarget(isWriteAllowed: false, availableBytes: long.MaxValue),
            CorrelationId.New());

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationStatus.Rejected, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "test.plan.write_not_allowed");
    }

    [Fact]
    public async Task CompilesTypedSystemSupportPolicyIntoR3HashBoundPlan()
    {
        var target = CreateTarget(true, 2L * 1024 * 1024 * 1024);
        SystemSupportAction[] supportActions =
        [
            new TestProcessSchedulingPolicyAction(
                TestProcessPriority.AboveNormal,
                [0, 1]),
            new UseTemporaryPowerPlanAction(Guid.NewGuid())
        ];

        var result = new TestPlanCompiler().Compile(
            CreateDefinition(),
            target,
            supportActions,
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        var plan = result.Value!;
        Assert.Equal(RiskLevel.R3ControlledSystemSupport, plan.Risk);
        Assert.Equal(supportActions, plan.SupportActions);
        Assert.True(TestPlanCompiler.HasValidHash(plan));
        var authorized = await new TestRunAuthorizationCoordinator(
                (_, _) => Task.FromResult(true))
            .AuthorizeAsync(plan, CancellationToken.None);
        Assert.True(authorized.IsSuccess);
        Assert.All(
            authorized.Value!.SupportActions,
            item => Assert.Equal(plan.PlanHash, item.PlanHash));
    }

    [Fact]
    public void RejectsDuplicateOrForeignTargetSystemSupportActions()
    {
        var target = CreateTarget(true, long.MaxValue);
        var duplicate = new TestPlanCompiler().Compile(
            CreateDefinition(),
            target,
            [
                new UseTemporaryPowerPlanAction(Guid.NewGuid()),
                new UseTemporaryPowerPlanAction(Guid.NewGuid())
            ],
            CorrelationId.New());
        var foreignSystem = SystemId.New();
        var foreignVolume = new StorageObjectId(
            foreignSystem,
            StorageObjectKind.Partition,
            "foreign-volume");
        var foreign = new TestPlanCompiler().Compile(
            CreateDefinition(),
            target,
            [new FlushVolumeAction(foreignVolume)],
            CorrelationId.New());

        Assert.Contains(
            duplicate.Messages,
            item => item.Code == "test.plan.support_actions_invalid");
        Assert.Contains(
            foreign.Messages,
            item => item.Code == "test.plan.support_action_invalid");
    }

    [Fact]
    public void FlushSnapshotIsHashBoundAndLimitedToCopyPlans()
    {
        var target = CreateTarget(true, 4L * 1024 * 1024 * 1024);
        var snapshot = new VolumeTargetSnapshot(
            target.VolumeId,
            @"\\?\Volume{11111111-1111-1111-1111-111111111111}",
            Path.GetPathRoot(target.TestRootDirectory)!);
        var source = CreateDefinition().Tasks[0];
        var copy = new TestTaskDefinition(
            TestTaskId.New(),
            "Copy",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceTaskId"] = TextTaskId("sourceTaskId", source.Id)
            });
        var copyDefinition = new TestDefinition(
            TestDefinitionId.New(),
            "Generate and copy",
            "1",
            new Dictionary<string, TestParameter>(),
            [source, copy],
            [
                new("generate", source.Id, [], true),
                new("copy", copy.Id, ["generate"], true)
            ],
            AlgorithmConfidence.Derived);

        var compiled = new TestPlanCompiler().Compile(
            copyDefinition,
            target,
            [new FlushVolumeAction(target.VolumeId, snapshot)],
            CorrelationId.New());
        var rejected = new TestPlanCompiler().Compile(
            CreateDefinition(),
            target,
            [new FlushVolumeAction(target.VolumeId, snapshot)],
            CorrelationId.New());

        Assert.True(compiled.IsSuccess);
        Assert.True(TestPlanCompiler.HasValidHash(compiled.Value!));
        Assert.False(TestPlanCompiler.HasValidHash(
            compiled.Value! with
            {
                SupportActions =
                [
                    new FlushVolumeAction(
                        target.VolumeId,
                        snapshot with { StableIdentity = @"\\?\Volume{22222222-2222-2222-2222-222222222222}" })
                ]
            }));
        Assert.Contains(
            rejected.Messages,
            item => item.Code == "test.plan.support_action_invalid");
    }

    [Fact]
    public void RejectsInsufficientSpaceAndCyclicSchedule()
    {
        var compiler = new TestPlanCompiler();
        var insufficient = compiler.Compile(
            CreateDefinition(),
            CreateTarget(isWriteAllowed: true, availableBytes: 1024),
            CorrelationId.New());
        Assert.Contains(insufficient.Messages, message => message.Code == "test.plan.insufficient_space");

        var definition = CreateDefinition();
        var original = definition.Schedule[0];
        var cyclic = definition with
        {
            Schedule = [original with { DependsOn = [original.Id] }]
        };
        var cyclicResult = compiler.Compile(
            cyclic,
            CreateTarget(isWriteAllowed: true, availableBytes: long.MaxValue),
            CorrelationId.New());
        Assert.Contains(cyclicResult.Messages, message => message.Code == "test.plan.invalid_dag");
    }

    [Fact]
    public void CompilesRepeatedScheduleAsDistinctHashBoundSteps()
    {
        var definition = CreateDefinition();
        var task = definition.Tasks[0];
        definition = definition with
        {
            Schedule =
            [
                new("io-001", task.Id, [], true),
                new("io-002", task.Id, ["io-001"], true),
                new("io-003", task.Id, ["io-002"], true)
            ]
        };

        var result = new TestPlanCompiler().Compile(
            definition,
            CreateTarget(true, 2L * 1024 * 1024 * 1024),
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        var plan = result.Value!;
        Assert.Equal(3, plan.Steps.Count);
        Assert.Single(plan.Workspace.RegisteredFiles);
        Assert.Equal(["io-001"], plan.Steps[1].DependsOn);
        Assert.Equal(["io-002"], plan.Steps[2].DependsOn);
        Assert.True(TestPlanCompiler.HasValidHash(plan));
        Assert.False(
            TestPlanCompiler.HasValidHash(
                plan with
                {
                    Steps =
                    [
                        .. plan.Steps.Take(2),
                        plan.Steps[2] with { DependsOn = ["io-001"] }
                    ]
                }));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void RejectsNegativeWarmupOrCooldown(
        int warmupSeconds,
        int cooldownSeconds)
    {
        var definition = CreateDefinition();
        definition = definition with
        {
            Tasks =
            [
                definition.Tasks[0] with
                {
                    Workload = definition.Tasks[0].Workload! with
                    {
                        Warmup = TimeSpan.FromSeconds(warmupSeconds),
                        Cooldown = TimeSpan.FromSeconds(cooldownSeconds)
                    }
                }
            ]
        };

        var result = new TestPlanCompiler().Compile(
            definition,
            CreateTarget(true, long.MaxValue),
            CorrelationId.New());

        Assert.Contains(
            result.Messages,
            message => message.Code == "test.plan.invalid_workload");
    }

    [Fact]
    public async Task AuthorizationRequiresConfirmationAndBindsSupportActionsToPlanHash()
    {
        var plan = Assert.IsType<TestPlan>(
            new TestPlanCompiler().Compile(
                CreateDefinition(),
                CreateTarget(true, 2L * 1024 * 1024 * 1024),
                CorrelationId.New()).Value);
        var denied = await new TestRunAuthorizationCoordinator(
            (_, _) => Task.FromResult(false))
            .AuthorizeAsync(plan, CancellationToken.None);
        var accepted = await new TestRunAuthorizationCoordinator(
            (_, _) => Task.FromResult(true))
            .AuthorizeAsync(plan, CancellationToken.None);

        Assert.Equal(ApplicationStatus.RequiresAuthorization, denied.Status);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(plan, accepted.Value!.Plan);
        Assert.Equal(plan.Workspace, accepted.Value.Workspace.Plan);
    }

    [Fact]
    public async Task AuthorizationRejectsTamperedOrExpiredPlan()
    {
        var plan = Assert.IsType<TestPlan>(
            new TestPlanCompiler().Compile(
                CreateDefinition(),
                CreateTarget(true, 2L * 1024 * 1024 * 1024),
                CorrelationId.New()).Value);
        var coordinator = new TestRunAuthorizationCoordinator(
            (_, _) => Task.FromResult(true));

        var tampered = await coordinator.AuthorizeAsync(
            plan with { EstimatedWriteBytes = plan.EstimatedWriteBytes + 1 },
            CancellationToken.None);
        var expired = await new TestRunAuthorizationCoordinator(
            (_, _) => Task.FromResult(true),
            new FixedTimeProvider(plan.Workspace.ExpiresAtUtc.AddSeconds(1)))
            .AuthorizeAsync(
            plan,
            CancellationToken.None);
        var supportActionTampered = await coordinator.AuthorizeAsync(
            plan with
            {
                SupportActions =
                [
                    new UseTemporaryPowerPlanAction(Guid.NewGuid())
                ]
            },
            CancellationToken.None);
        var cleanupPolicyTampered = await coordinator.AuthorizeAsync(
            plan with
            {
                Workspace = plan.Workspace with
                {
                    CleanupPolicy = TestWorkspaceCleanupPolicy.KeepAll
                }
            },
            CancellationToken.None);

        Assert.Equal(ApplicationStatus.Rejected, tampered.Status);
        Assert.Contains(
            tampered.Messages,
            message => message.Code == "test.authorization.plan_hash_mismatch");
        Assert.Equal(ApplicationStatus.Rejected, expired.Status);
        Assert.Contains(
            expired.Messages,
            message => message.Code == "test.authorization.expired");
        Assert.Equal(ApplicationStatus.Rejected, supportActionTampered.Status);
        Assert.Contains(
            supportActionTampered.Messages,
            message => message.Code == "test.authorization.plan_hash_mismatch");
        Assert.Equal(ApplicationStatus.Rejected, cleanupPolicyTampered.Status);
        Assert.Contains(
            cleanupPolicyTampered.Messages,
            message => message.Code == "test.authorization.plan_hash_mismatch");
    }

    [Fact]
    public async Task InterruptedPlanCanResumeAfterPlanExpiryOnlyWithExactHashAndConfirmation()
    {
        var plan = Assert.IsType<TestPlan>(
            new TestPlanCompiler().Compile(
                CreateDefinition(),
                CreateTarget(true, 2L * 1024 * 1024 * 1024),
                CorrelationId.New()).Value);
        var now = plan.Workspace.ExpiresAtUtc.AddDays(1);
        var accepted = await new TestRunAuthorizationCoordinator(
                (_, _) => Task.FromResult(true),
                new FixedTimeProvider(now))
            .AuthorizeResumeAsync(
                plan,
                plan.PlanHash,
                CancellationToken.None);
        var denied = await new TestRunAuthorizationCoordinator(
                (_, _) => Task.FromResult(false),
                new FixedTimeProvider(now))
            .AuthorizeResumeAsync(
                plan,
                plan.PlanHash,
                CancellationToken.None);
        var foreign = await new TestRunAuthorizationCoordinator(
                (_, _) => Task.FromResult(true),
                new FixedTimeProvider(now))
            .AuthorizeResumeAsync(
                plan,
                new string('0', 64),
                CancellationToken.None);

        Assert.True(accepted.IsSuccess);
        Assert.True(accepted.Value!.Workspace.ExpiresAtUtc > now);
        Assert.Equal(
            ApplicationStatus.RequiresAuthorization,
            denied.Status);
        Assert.Contains(
            foreign.Messages,
            item => item.Code
                == "test.authorization.resume_hash_mismatch");
    }

    [Fact]
    public void CopyPlanRegistersGeneratedSourceAndDistinctDestination()
    {
        var source = CreateDefinition().Tasks[0];
        var copyId = TestTaskId.New();
        var copy = new TestTaskDefinition(
            copyId,
            "Copy",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceTaskId"] = new(
                    "sourceTaskId",
                    TestParameterKind.Text,
                    source.Id.Value.ToString("D"),
                    "test.source_task")
            });
        var definition = new TestDefinition(
            TestDefinitionId.New(),
            "Generate and copy",
            "1",
            new Dictionary<string, TestParameter>(),
            [source, copy],
            [
                new("generate", source.Id, [], true),
                new("copy", copyId, ["generate"], true)
            ],
            AlgorithmConfidence.Derived);

        var result = new TestPlanCompiler().Compile(
            definition,
            CreateTarget(true, 4L * 1024 * 1024 * 1024),
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        var plan = result.Value!;
        Assert.Equal(2L * 1024 * 1024 * 1024, plan.EstimatedWriteBytes);
        Assert.Equal(2, plan.Workspace.RegisteredFiles.Count);
        var copyStep = plan.Steps.Single(item => item.Id == "copy");
        var sourcePath = copyStep.Parameters["sourceRelativePath"].SerializedValue;
        var destinationPath = copyStep.Parameters["destinationRelativePath"].SerializedValue;
        Assert.NotEqual(sourcePath, destinationPath);
        Assert.Equal(
            Path.GetFileName(sourcePath),
            Path.GetFileName(destinationPath));
        Assert.All(
            new[] { sourcePath, destinationPath },
            path => Assert.Contains(
                plan.Workspace.RegisteredFiles,
                file => string.Equals(
                    file.RelativePath,
                    path,
                    StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void CopyVerificationResolvesOnlyItsRegisteredSourceAndDestination()
    {
        var source = CreateDefinition().Tasks[0] with
        {
            Action = TestActionKind.GenerateFile
        };
        var copyId = TestTaskId.New();
        var verifyId = TestTaskId.New();
        var copy = new TestTaskDefinition(
            copyId,
            "Copy",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceTaskId"] = TextTaskId("sourceTaskId", source.Id)
            });
        var verify = new TestTaskDefinition(
            verifyId,
            "Verify",
            TestActionKind.Verify,
            null,
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceTaskId"] = TextTaskId("sourceTaskId", source.Id),
                ["copyTaskId"] = TextTaskId("copyTaskId", copyId),
                ["verificationMode"] = new(
                    "verificationMode",
                    TestParameterKind.Choice,
                    RegisteredTestFileVerificationMode.SampledContent.ToString(),
                    "test.verification_mode")
            });
        var definition = new TestDefinition(
            TestDefinitionId.New(),
            "Generate, copy, verify",
            "1",
            new Dictionary<string, TestParameter>(),
            [source, copy, verify],
            [
                new("generate", source.Id, [], true),
                new("copy", copyId, ["generate"], true),
                new("verify", verifyId, ["copy"], true)
            ],
            AlgorithmConfidence.Derived);

        var result = new TestPlanCompiler().Compile(
            definition,
            CreateTarget(true, 4L * 1024 * 1024 * 1024),
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        var plan = result.Value!;
        var copyStep = plan.Steps.Single(item => item.Id == "copy");
        var verifyStep = plan.Steps.Single(item => item.Id == "verify");
        Assert.Equal(
            copyStep.Parameters["sourceRelativePath"].SerializedValue,
            verifyStep.Parameters["sourceRelativePath"].SerializedValue);
        Assert.Equal(
            copyStep.Parameters["destinationRelativePath"].SerializedValue,
            verifyStep.Parameters["destinationRelativePath"].SerializedValue);
        Assert.Equal(
            string.Join(
                ',',
                copyStep.Parameters["sourceRelativePath"].SerializedValue,
                copyStep.Parameters["destinationRelativePath"].SerializedValue),
            verifyStep.Parameters["relativePaths"].SerializedValue);
        Assert.True(TestPlanCompiler.HasValidHash(plan));
    }

    [Fact]
    public void MixedDirectoryPlanBindsQuotasCopyRootsAndVerification()
    {
        var source = CreateDefinition().Tasks[0] with
        {
            Action = TestActionKind.GenerateFile,
            Parameters = new Dictionary<string, TestParameter>
            {
                ["outputKind"] = new(
                    "outputKind",
                    TestParameterKind.Choice,
                    "directory",
                    "test.output_kind"),
                ["profile"] = new(
                    "profile",
                    TestParameterKind.Choice,
                    "mixed",
                    "test.profile"),
                ["targetCount"] = new(
                    "targetCount",
                    TestParameterKind.Integer,
                    "50505",
                    "test.target_count"),
                ["maximumFileCount"] = new(
                    "maximumFileCount",
                    TestParameterKind.Integer,
                    "50506",
                    "test.maximum_file_count")
            }
        };
        var copyId = TestTaskId.New();
        var verifyId = TestTaskId.New();
        var copy = new TestTaskDefinition(
            copyId,
            "Copy mixed tree",
            TestActionKind.Copy,
            new ToolId("windows.robocopy"),
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceTaskId"] = TextTaskId("sourceTaskId", source.Id)
            });
        var verify = new TestTaskDefinition(
            verifyId,
            "Verify mixed tree",
            TestActionKind.Verify,
            null,
            null,
            new Dictionary<string, TestParameter>
            {
                ["sourceTaskId"] = TextTaskId("sourceTaskId", source.Id),
                ["copyTaskId"] = TextTaskId("copyTaskId", copyId),
                ["verificationMode"] = new(
                    "verificationMode",
                    TestParameterKind.Choice,
                    RegisteredTestFileVerificationMode.SampledContent.ToString(),
                    "test.verification_mode")
            });
        var definition = new TestDefinition(
            TestDefinitionId.New(),
            "Mixed tree",
            "1",
            new Dictionary<string, TestParameter>(),
            [source, copy, verify],
            [
                new("generate", source.Id, [], true),
                new("copy", copy.Id, ["generate"], true),
                new("verify", verify.Id, ["copy"], true)
            ],
            AlgorithmConfidence.Derived);

        var result = new TestPlanCompiler().Compile(
            definition,
            CreateTarget(true, 4L * 1024 * 1024 * 1024),
            CorrelationId.New());

        Assert.True(result.IsSuccess);
        var plan = result.Value!;
        Assert.Empty(plan.Workspace.RegisteredFiles);
        Assert.Equal(2, plan.Workspace.RegisteredDirectories.Count);
        Assert.All(
            plan.Workspace.RegisteredDirectories,
            item =>
            {
                Assert.Equal(1L * 1024 * 1024 * 1024, item.MaximumBytes);
                Assert.Equal(50506, item.MaximumFileCount);
            });
        Assert.Equal(2L * 1024 * 1024 * 1024, plan.EstimatedWriteBytes);
        var generate = plan.Steps.Single(item => item.Id == "generate");
        var copyStep = plan.Steps.Single(item => item.Id == "copy");
        var verifyStep = plan.Steps.Single(item => item.Id == "verify");
        Assert.True(generate.Parameters.ContainsKey("targetRelativeDirectory"));
        Assert.Equal(
            generate.Parameters["targetRelativeDirectory"].SerializedValue,
            copyStep.Parameters["sourceRelativeDirectory"].SerializedValue);
        Assert.Equal(
            copyStep.Parameters["sourceRelativeDirectory"].SerializedValue,
            verifyStep.Parameters["sourceRelativeDirectory"].SerializedValue);
        Assert.Equal(
            copyStep.Parameters["destinationRelativeDirectory"].SerializedValue,
            verifyStep.Parameters["destinationRelativeDirectory"].SerializedValue);
        Assert.True(TestPlanCompiler.HasValidHash(plan));
        Assert.False(
            TestPlanCompiler.HasValidHash(
                plan with
                {
                    Workspace = plan.Workspace with
                    {
                        RegisteredDirectories =
                        [
                            plan.Workspace.RegisteredDirectories[0] with
                            {
                                MaximumBytes = long.MaxValue
                            },
                            plan.Workspace.RegisteredDirectories[1]
                        ]
                    }
                }));
    }

    [Fact]
    public void RejectsPatternReplayForArbitraryExternalCopyOutput()
    {
        var definition = CreateDefinition();
        var task = definition.Tasks[0];
        definition = definition with
        {
            Tasks =
            [
                task with
                {
                    Action = TestActionKind.Verify,
                    RequiredTool = null,
                    Workload = null,
                    Parameters = new Dictionary<string, TestParameter>
                    {
                        ["verificationMode"] = new(
                            "verificationMode",
                            TestParameterKind.Choice,
                            RegisteredTestFileVerificationMode.PatternReplay.ToString(),
                            "test.verification_mode")
                    }
                }
            ]
        };

        var result = new TestPlanCompiler().Compile(
            definition,
            CreateTarget(true, long.MaxValue),
            CorrelationId.New());

        Assert.Contains(
            result.Messages,
            item => item.Code == "test.plan.verification_mode_invalid");
    }

    [Fact]
    public void RejectsMissingOrUnexpectedToolBindings()
    {
        var definition = CreateDefinition();
        var missing = definition with
        {
            Tasks = [definition.Tasks[0] with { RequiredTool = null }]
        };
        var unexpected = definition with
        {
            Tasks =
            [
                definition.Tasks[0] with
                {
                    Action = TestActionKind.CheckSpace,
                    Workload = null
                }
            ]
        };

        var missingResult = new TestPlanCompiler().Compile(
            missing,
            CreateTarget(true, long.MaxValue),
            CorrelationId.New());
        var unexpectedResult = new TestPlanCompiler().Compile(
            unexpected,
            CreateTarget(true, long.MaxValue),
            CorrelationId.New());

        Assert.Contains(
            missingResult.Messages,
            item => item.Code == "test.plan.invalid_tool_binding");
        Assert.Contains(
            unexpectedResult.Messages,
            item => item.Code == "test.plan.invalid_tool_binding");
    }

    private static TestDefinition CreateDefinition()
    {
        var task = new TestTaskDefinition(
            TestTaskId.New(),
            "Sequential write",
            TestActionKind.RunIo,
            new ToolId("diskspd"),
            new TestWorkload(
                1L * 1024 * 1024 * 1024,
                64 * 1024,
                4,
                8,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(2),
                IoAccessPattern.Sequential,
                100,
                SoftwareCacheMode.Disabled,
                WriteThroughMode.Enabled,
                CollectLatency: true),
            new Dictionary<string, TestParameter>());
        return new(
            TestDefinitionId.New(),
            "Test",
            "1",
            new Dictionary<string, TestParameter>(),
            [task],
            [new("step-1", task.Id, [], IsCancellationBoundary: true)],
            AlgorithmConfidence.Derived);
    }

    private static TestTarget CreateTarget(bool isWriteAllowed, long availableBytes)
    {
        var systemId = SystemId.New();
        return new(
            systemId,
            new StorageObjectId(systemId, StorageObjectKind.Partition, "disk-provider"),
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WinPool-Test-Root")),
            availableBytes,
            isWriteAllowed);
    }

    private static TestParameter TextTaskId(string key, TestTaskId taskId) =>
        new(
            key,
            TestParameterKind.Text,
            taskId.Value.ToString("D"),
            $"test.{key}");

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
