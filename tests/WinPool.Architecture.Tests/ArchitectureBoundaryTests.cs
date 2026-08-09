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
    public void CompatibilityAuditRegistersEveryOneOfThe205AuthoritativeIds()
    {
        var root = FindRepositoryRoot();
        var specification = File.ReadAllText(
            Path.Combine(root, "Plan", "06_现有功能兼容清单.md"));
        var audit = File.ReadAllText(
            Path.Combine(root, "Plan", "10_兼容性审计台账.md"));
        const string pattern = @"\b(?:SH|MG|IN|ED|TS|MO|DV|ST)-\d{3}\b";
        var expected = System.Text.RegularExpressions.Regex.Matches(
                specification,
                pattern)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);
        var actual = System.Text.RegularExpressions.Regex.Matches(audit, pattern)
            .Select(match => match.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(205, expected.Count);
        Assert.True(expected.SetEquals(actual));
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
    public void ProductFacingVersionIsUnifiedToV02()
    {
        var root = FindRepositoryRoot();
        var productInformation = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.App",
                "Services",
                "ProductInformation.cs"));
        var settingsPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "SettingsPage.xaml.cs"));

        Assert.Contains("public const string Version = \"V0.2\";", productInformation);
        Assert.DoesNotContain("V0.13", productInformation, StringComparison.Ordinal);
        Assert.Contains(
            "AboutVersionValue.Text = ProductInformation.Version;",
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
        Assert.Contains("showAgainCheckBox.Focus(FocusState.Programmatic)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmButton.Click += (_, _) => dialog.Hide()", source, StringComparison.Ordinal);
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
