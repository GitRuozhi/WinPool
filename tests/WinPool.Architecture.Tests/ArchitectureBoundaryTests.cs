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
            ["WinPool.Inventory"] = ["WinPool.Application", "WinPool.Domain"],
            ["WinPool.Agent.Client"] = ["WinPool.Application", "WinPool.Ipc"],
            ["WinPool.Monitoring"] = ["WinPool.Application", "WinPool.Domain"],
            ["WinPool.Testing"] = ["WinPool.Application", "WinPool.Domain"],
            ["WinPool.Testing.Tools"] = ["WinPool.Application"],
            ["WinPool.ToolManagement"] = ["WinPool.Application"]
        };

    [Fact]
    public void NewDomainAndApplicationProjectsFollowApprovedDependencyDirection()
    {
        var root = FindRepositoryRoot();
        foreach (var (projectName, allowed) in AllowedReferences)
        {
            var projectFile = Path.Combine(root, "src", projectName, $"{projectName}.csproj");
            if (!File.Exists(projectFile))
            {
                continue;
            }

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
    public void CurrentPlanCarriesTheV02CompatibilityAuditAsExplicitDebt()
    {
        var root = FindRepositoryRoot();
        var currentPlan = File.ReadAllText(Path.Combine(root, "docs", "Plan.md"));

        Assert.Contains("DEBT-01", currentPlan, StringComparison.Ordinal);
        Assert.Contains("205 compatibility IDs", currentPlan, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentationArchitectureHasOneActivePlanAndACompleteV02Archive()
    {
        var root = FindRepositoryRoot();
        var requiredDocuments = new[]
        {
            "Product.md",
            "Development.md",
            "Quality.md",
            "Plan.md",
            "CHANGELOG.md"
        };

        Assert.False(Directory.Exists(Path.Combine(root, "Plan")));
        Assert.False(File.Exists(Path.Combine(root, "DEVELOP.md")));
        Assert.All(
            requiredDocuments,
            name => Assert.True(File.Exists(Path.Combine(root, "docs", name)), name));

        var archive = Path.Combine(root, "docs", "Archive", "V0.2");
        Assert.Equal(16, Directory.EnumerateFiles(archive, "*.md").Count());
        Assert.True(File.Exists(Path.Combine(root, "docs", "Archive", "README.md")));
        Assert.True(File.Exists(Path.Combine(
            root,
            "docs",
            "Reference",
            "AI-Agent-Harness-项目管理架构参考.md")));

        var operationalRules = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        var currentPlan = File.ReadAllText(Path.Combine(root, "docs", "Plan.md"));
        Assert.DoesNotContain("Do not create `Docs/docs`", operationalRules, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/Archive` 的提案已撤销", currentPlan, StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishDocumentationHasNonAuthoritativeChineseReadingCopies()
    {
        var root = FindRepositoryRoot();
        var pairs = new[]
        {
            ("README.md", "README.zh-CN.md"),
            ("AGENTS.md", "AGENTS.zh-CN.md"),
            ("docs/Product.md", "docs/Product.zh-CN.md"),
            ("docs/Development.md", "docs/Development.zh-CN.md"),
            ("docs/Quality.md", "docs/Quality.zh-CN.md"),
            ("docs/Plan.md", "docs/Plan.zh-CN.md"),
            ("docs/CHANGELOG.md", "docs/CHANGELOG.zh-CN.md"),
            ("docs/Archive/README.md", "docs/Archive/README.zh-CN.md")
        };

        Assert.False(File.Exists(Path.Combine(root, "README_CN.md")));
        Assert.All(
            pairs,
            pair =>
            {
                Assert.True(File.Exists(Path.Combine(root, pair.Item1)), pair.Item1);
                var readingCopy = Path.Combine(root, pair.Item2);
                Assert.True(File.Exists(readingCopy), pair.Item2);
                Assert.Contains("无 `.zh-CN` 后缀", File.ReadAllText(readingCopy));
            });
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
            "Process.Start(",
            "cmd.exe",
            "diskpart",
            "Format-Volume",
            "New-StoragePool",
            "Remove-StoragePool"
        };

        var source = string.Join(
            '\n',
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.All(
            forbidden,
            token => Assert.DoesNotContain(token, source, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AgentRuntimeIsPackagedInAnIsolatedSubdirectory()
    {
        var root = FindRepositoryRoot();
        var appProject = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "WinPool.App.csproj"));
        var appStartup = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "App.xaml.cs"));

        Assert.Contains(
            "$(OutDir)Agent\\%(RecursiveDir)",
            appProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$(OutDir)%(RecursiveDir)%(Filename)%(Extension)",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Agent\",\n            \"WinPool.Agent.exe\"",
            appStartup.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDataLocationSwitchUsesVerifiedMigrationInsteadOfLegacyDirectCopy()
    {
        var root = FindRepositoryRoot();
        var settingsPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "SettingsPage.xaml.cs"));

        Assert.Contains("new StorageLocationManager(", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ShutdownReason.StorageLocationSwitch", settingsPage, StringComparison.Ordinal);
        Assert.Contains("SourceManifestSha256", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageDataLocations.SetModeAsync", settingsPage, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPageSubmitsApplicationSimulationContractsInsteadOfCoreOperations()
    {
        var root = FindRepositoryRoot();
        var editPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "EditPage.xaml.cs"));
        var workspace = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "ViewModels", "WorkspaceViewModel.cs"));

        Assert.Contains(
            "WinPool.Application.SimulationEditRequest",
            editPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "_simulationEditCoordinator.ExecuteAsync",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_simulationOperations.Apply(ActiveDocument",
            workspace,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManageTopologyConsumesApplicationProjectionContract()
    {
        var root = FindRepositoryRoot();
        var workspace = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "ViewModels", "WorkspaceViewModel.cs"));
        var topologyViewModel = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "ViewModels", "TopologyNodeViewModel.cs"));
        var mainPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainPage.xaml.cs"));

        Assert.Contains(
            "_manageProjector.Project(document)",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "activeProjection.WorkspaceObjects",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TopologyProjector.Project(document.Snapshot)",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "ManageTopologyNodeView node",
            topologyViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TopologyNode node",
            topologyViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "new ManageObjectTarget(ObjectId, Role)",
            topologyViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private IReadOnlyList<PhysicalDiskInfo> OrderPhysicalDisks",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WorkspaceMapper.FromUnit",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "_manageComparisonProjector.Project(",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "_manageDetailsProjector.Project(",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "_manageNavigationProjector.Project(",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "item.Projection",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BuildComparisonRows(",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReportMemory(StorageSystemDocument",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var pool = ActiveSnapshot.StoragePools.First(x => x.StableId == unit.StableId);",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private string? RelatedPoolId(",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private string? RelatedPartitionId(",
            workspace,
            StringComparison.Ordinal);
        Assert.Contains(
            "ViewModel.GetSelectedCommandSurface()",
            mainPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "ManageCategoryCsvExporter.Create(",
            mainPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "using WinPool.Core;",
            mainPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ViewModel.ActiveSnapshot",
            mainPage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManageSelectionRulesPreserveFrozenCategoryMapping()
    {
        Assert.Equal(
            ManageWorkspaceCategory.System,
            ManageSelectionRules.CategoryFor(ManageObjectRole.System));
        Assert.Equal(
            ManageWorkspaceCategory.Pool,
            ManageSelectionRules.CategoryFor(ManageObjectRole.NetworkGroup));
        Assert.Equal(
            ManageWorkspaceCategory.Pool,
            ManageSelectionRules.CategoryFor(ManageObjectRole.OtherGroup));
        Assert.Equal(
            ManageWorkspaceCategory.Tier,
            ManageSelectionRules.CategoryFor(ManageObjectRole.StorageTier));
        Assert.Equal(
            ManageWorkspaceCategory.Disk,
            ManageSelectionRules.CategoryFor(ManageObjectRole.VirtualDisk));
        Assert.Equal(
            ManageWorkspaceCategory.Partition,
            ManageSelectionRules.CategoryFor(ManageObjectRole.NetworkDisk));
        Assert.Equal(
            ManageWorkspaceCategory.Partition,
            ManageSelectionRules.CategoryFor(ManageObjectRole.Partition));
    }

    [Fact]
    public void ManageCategoryCsvExporterPreservesUnionOrderAndEscaping()
    {
        var csv = ManageCategoryCsvExporter.Create(
            "Name",
            [
                new ManageExportColumn(
                    "Disk A",
                    [new("Model", "Alpha"), new("Path", "C:\\A,B")]),
                new ManageExportColumn(
                    "Disk B",
                    [new("Model", "Beta"), new("Extra", "say \"hi\"")])
            ]);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Name,Disk A,Disk B",
                "Model,Alpha,Beta",
                "Path,\"C:\\A,B\",",
                "Extra,,\"say \"\"hi\"\"\"",
                string.Empty),
            csv);
    }

    [Fact]
    public void NormalWorkspacePersistenceUsesAgentAndJsonRemainsDeveloperFallbackOnly()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));
        var agentService = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.Infrastructure.Windows",
                "AgentBackedWorkspaceStateService.cs"));

        Assert.Contains(
            "agentConnection is null\n            ? new LocalWorkspaceStateService()\n            : new AgentBackedWorkspaceStateService(agentConnection)",
            mainWindow.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new LocalWorkspaceStateService();",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "new LoadAgentWorkspaceStateRequest",
            agentService,
            StringComparison.Ordinal);
        Assert.Contains(
            "new SaveAgentWorkspaceStateRequest",
            agentService,
            StringComparison.Ordinal);
        Assert.DoesNotContain("LocalWorkspaceStateService", agentService, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalManageInventoryRunsThroughAgentInsteadOfUiPowerShell()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));

        Assert.Contains("new AgentBackedHardwareInventoryProvider(agentConnection)", mainWindow);
        Assert.Contains("new AgentBackedMachineRecordService(agentConnection)", mainWindow);
        Assert.Contains("new WindowsHardwareInventoryProvider()", mainWindow);
        Assert.Contains("agentConnection is null", mainWindow);
    }

    [Fact]
    public void MainWindowExposesStableKeyboardShortcutsForAllSixPages()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));

        Assert.Contains("RegisterShellKeyboardAccelerators();", mainWindow);
        Assert.Contains("VirtualKeyModifiers.Control", mainWindow);
        Assert.Contains("(VirtualKey.Number1, ShellPageKind.Manage)", mainWindow);
        Assert.Contains("(VirtualKey.Number2, ShellPageKind.Create)", mainWindow);
        Assert.Contains("(VirtualKey.Number3, ShellPageKind.Test)", mainWindow);
        Assert.Contains("(VirtualKey.Number4, ShellPageKind.Monitor)", mainWindow);
        Assert.Contains("(VirtualKey.Number5, ShellPageKind.Development)", mainWindow);
        Assert.Contains("(VirtualKey.Number6, ShellPageKind.Settings)", mainWindow);
        Assert.Contains("args.Handled = true;", mainWindow);
    }

    [Fact]
    public void ProductFacingVersionUsesTheRepositoryVersionSource()
    {
        var root = FindRepositoryRoot();
        var versionSource = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var productInformation = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.App",
                "Services",
                "ProductInformation.cs"));
        var settingsPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "SettingsPage.xaml.cs"));

        Assert.Contains("<WinPoolArchitectureVersion>V0.3</WinPoolArchitectureVersion>", versionSource, StringComparison.Ordinal);
        Assert.Contains("<WinPoolDisplayVersion>V0.31</WinPoolDisplayVersion>", versionSource, StringComparison.Ordinal);
        Assert.Contains("<WinPoolTechnicalVersion>0.3.1.0</WinPoolTechnicalVersion>", versionSource, StringComparison.Ordinal);
        Assert.Contains("<InformationalVersion>$(WinPoolDisplayVersion)</InformationalVersion>", versionSource, StringComparison.Ordinal);
        Assert.Contains("AssemblyInformationalVersionAttribute", productInformation, StringComparison.Ordinal);
        Assert.Contains("AssemblyFileVersionAttribute", productInformation, StringComparison.Ordinal);
        Assert.DoesNotContain("V0.21", productInformation, StringComparison.Ordinal);
        Assert.Contains(
            "AboutVersionValue.Text = ProductInformation.Version;",
            settingsPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "UserAgent.ParseAdd(ProductInformation.UserAgent);",
            settingsPage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WelcomeDialogUsesKeyboardAccessibleNativePrimaryButton()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.App",
                "MainWindow.xaml.cs"));

        Assert.Contains("PrimaryButtonText = localization[\"WelcomeConfirm\"]", source, StringComparison.Ordinal);
        Assert.Contains("DefaultButton = ContentDialogButton.Primary", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (_startupTarget is ApplicationStartupTarget.None",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "or ApplicationStartupTarget.Welcome)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HasShownWelcome", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkWelcomeShownAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("showAgainCheckBox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmButton.Click += (_, _) => dialog.Hide()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDiskPropertiesDoesNotRunAFullInventoryScan()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "DesktopAgentRuntime.cs"));
        var start = source.IndexOf(
            "public Task<ApplicationResult<AgentResponse>> OpenNativePropertiesAsync",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public Task<ApplicationResult<AgentResponse>> StartMonitoringAsync",
            start,
            StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("physicalDeviceIds", method, StringComparison.Ordinal);
        Assert.Contains("physicalDiskDeviceResolver.ResolvePnpDeviceId", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectLocalAsync", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentPublishIncludesEveryProcessRuntime()
    {
        var root = FindRepositoryRoot();
        var appProject = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "WinPool.App.csproj"));
        var agentProject = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "WinPool.Agent.csproj"));
        var stagingScript = File.ReadAllText(
            Path.Combine(root, "build", "Publish-Staged.ps1"));

        Assert.Contains("PublishAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.Contains("CopyAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<ProjectReference Include=\"..\\WinPool.Agent\\WinPool.Agent.csproj\"",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains("PublishTestWorkerRuntime", agentProject, StringComparison.Ordinal);
        Assert.Contains("PublishElevatedBrokerRuntime", agentProject, StringComparison.Ordinal);
        Assert.Contains("System.IO.Path]::GetFullPath", appProject, StringComparison.Ordinal);
        Assert.Contains("System.IO.Path]::GetFullPath", agentProject, StringComparison.Ordinal);
        Assert.Contains("WinPool.App.exe", stagingScript, StringComparison.Ordinal);
        Assert.Contains("Agent/WinPool.Agent.exe", stagingScript, StringComparison.Ordinal);
        Assert.Contains("Agent/TestWorker/WinPool.TestWorker.exe", stagingScript, StringComparison.Ordinal);
        Assert.Contains("Agent/Broker/WinPool.ElevatedBroker.exe", stagingScript, StringComparison.Ordinal);
        Assert.Contains("duplicate", stagingScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestPagePrimaryWorkflowHasStableKeyboardAccessKeys()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "TestPage.xaml"));

        Assert.Contains("x:Name=\"ChooseTargetButton\"", source, StringComparison.Ordinal);
        Assert.Contains("AccessKey=\"D\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PrepareButton\"", source, StringComparison.Ordinal);
        Assert.Contains("AccessKey=\"P\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StartButton\"", source, StringComparison.Ordinal);
        Assert.Contains("AccessKey=\"R\"", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CancelButton\"", source, StringComparison.Ordinal);
        Assert.Contains("AccessKey=\"C\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorPageBackgroundModeHasStableKeyboardAccessKey()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MonitorPage.xaml"));

        Assert.Contains("x:Name=\"BackgroundCheckBox\"", source, StringComparison.Ordinal);
        Assert.Contains("AccessKey=\"B\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceOperationsUseApplicationNotificationContracts()
    {
        var scan = WorkspaceNotificationFactory.ScanStarted();
        var failed = WorkspaceNotificationFactory.OperationFailed("operation:test");

        Assert.True(ApplicationNotificationValidator.IsValid(scan));
        Assert.Equal(ApplicationNotificationSeverity.Information, scan.Severity);
        Assert.False(scan.AutoDismiss);
        Assert.Equal("inventory:scanning", scan.OccurrenceKey);
        Assert.True(ApplicationNotificationValidator.IsValid(failed));
        Assert.Equal(ApplicationNotificationSeverity.Error, failed.Severity);

        var root = FindRepositoryRoot();
        var workspace = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "ViewModels", "WorkspaceViewModel.cs"));
        var mainPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainPage.xaml.cs"));
        Assert.Contains("WorkspaceNotificationFactory.ScanStarted()", workspace, StringComparison.Ordinal);
        Assert.Contains("WorkspaceNotificationFactory.ScanCompleted(", workspace, StringComparison.Ordinal);
        Assert.Contains("WorkspaceNotificationFactory.ScanFailed(", workspace, StringComparison.Ordinal);
        Assert.Contains("WorkspaceNotificationFactory.ExportCompleted(", mainPage, StringComparison.Ordinal);
        Assert.Contains("WorkspaceNotificationFactory.ImportCompleted(", mainPage, StringComparison.Ordinal);
        Assert.Contains("WorkspaceNotificationFactory.OperationFailed(", mainPage, StringComparison.Ordinal);
    }

    [Fact]
    public void TestOrchestrationKeepsDurableRecoveryAroundSchedulingAndPowerChanges()
    {
        var root = FindRepositoryRoot();
        var runtime = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "DesktopAgentRuntime.cs"));

        var register = runtime.IndexOf(
            "await workerProcessRepository.SaveAsync(",
            StringComparison.Ordinal);
        var schedulingPrepare = runtime.IndexOf(
            "await testProcessSchedulingScope.PrepareAsync(",
            register,
            StringComparison.Ordinal);
        var workerRun = runtime.IndexOf(
            "return await testWorkerHost.RunAsync(",
            StringComparison.Ordinal);
        var schedulingRestore = runtime.IndexOf(
            "await testProcessSchedulingScope.RestoreAsync(",
            schedulingPrepare,
            StringComparison.Ordinal);
        var powerPrepare = runtime.IndexOf(
            "powerPlanScope = await testPowerPlanScope.PrepareAsync(",
            StringComparison.Ordinal);
        var localExecution = runtime.IndexOf(
            "var localExecutor = new LocalTestStepExecutor(",
            powerPrepare,
            StringComparison.Ordinal);
        var powerRestore = runtime.IndexOf(
            "await testPowerPlanScope.RestoreAsync(",
            powerPrepare,
            StringComparison.Ordinal);
        var completeRun = runtime.IndexOf(
            "await testRunRepository.CompleteAsync(",
            powerRestore,
            StringComparison.Ordinal);

        Assert.True(workerRun >= 0 && register > workerRun);
        Assert.True(schedulingPrepare > register);
        Assert.True(schedulingRestore > schedulingPrepare);
        Assert.True(powerPrepare >= 0 && localExecution > powerPrepare);
        Assert.True(powerRestore > localExecution);
        Assert.True(completeRun > powerRestore);
    }

    [Fact]
    public void DevelopmentPageUsesClosedDiagnosticsAndHasNoFreeCommandInput()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DevelopmentPage.xaml.cs"));
        var view = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DevelopmentPage.xaml"));
        var projection = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "DevelopmentDiagnosticsProjection.cs"));

        Assert.Contains("new GetDevelopmentDiagnosticsRequest(10", page, StringComparison.Ordinal);
        Assert.Contains("WatchAsync(cancellationToken)", page, StringComparison.Ordinal);
        Assert.Contains("MonitorQueue buffered=", page, StringComparison.Ordinal);
        Assert.Contains("ParameterKeys", page, StringComparison.Ordinal);
        Assert.DoesNotContain("SerializedValue", projection, StringComparison.Ordinal);
        Assert.DoesNotContain("TextBox", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process.Start", page, StringComparison.Ordinal);
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
