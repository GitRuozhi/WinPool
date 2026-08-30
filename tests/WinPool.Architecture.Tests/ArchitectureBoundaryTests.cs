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
    public void V032AcceptanceRecordCarriesTheV02CompatibilityAuditAsExplicitDebt()
    {
        var root = FindRepositoryRoot();
        var currentPlan = File.ReadAllText(
            Path.Combine(root, "docs", "Archive", "V0.32", "Plan.md"));

        Assert.Contains("DEBT-01", currentPlan, StringComparison.Ordinal);
        Assert.Contains("205 compatibility IDs", currentPlan, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreProjectsAreRetiredAndProductionUsesApplicationContracts()
    {
        var root = FindRepositoryRoot();
        Assert.False(Directory.Exists(Path.Combine(root, "src", "WinPool.Core")));
        Assert.False(Directory.Exists(Path.Combine(root, "tests", "WinPool.Core.Tests")));

        var projectFiles = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.All(
            projectFiles,
            path => Assert.DoesNotContain(
                "WinPool.Core",
                File.ReadAllText(path),
                StringComparison.Ordinal));

        var productionSource = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(root, "src"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("WinPool.Core", productionSource, StringComparison.Ordinal);
        var productionXaml = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(root, "src"),
                    "*.xaml",
                    SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        Assert.DoesNotContain("WinPool.Core", productionXaml, StringComparison.Ordinal);
        Assert.Contains("StorageSystemDocument", productionSource, StringComparison.Ordinal);

        var preferenceDefinitions = Directory.EnumerateFiles(
                Path.Combine(root, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Count(path => File.ReadAllText(path).Contains(
                "public sealed record UserPreferences(",
                StringComparison.Ordinal));
        Assert.Equal(1, preferenceDefinitions);
    }

    [Fact]
    public void DocumentationArchitectureSupportsAnOptionalActivePlanAndACompleteV02Archive()
    {
        var root = FindRepositoryRoot();
        var requiredDocuments = new[]
        {
            "Product.md",
            "Development.md",
            "Quality.md",
            "CHANGELOG.md"
        };

        Assert.False(Directory.Exists(Path.Combine(root, "Plan")));
        Assert.False(File.Exists(Path.Combine(root, "DEVELOP.md")));
        Assert.All(
            requiredDocuments,
            name => Assert.True(File.Exists(Path.Combine(root, "docs", name)), name));

        var activePlan = Path.Combine(root, "docs", "Plan.md");
        if (File.Exists(activePlan))
        {
            Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(activePlan)));
        }

        var archive = Path.Combine(root, "docs", "Archive", "V0.2");
        Assert.Equal(16, Directory.EnumerateFiles(archive, "*.md").Count());
        Assert.True(File.Exists(Path.Combine(root, "docs", "Archive", "README.md")));
        Assert.True(File.Exists(Path.Combine(
            root,
            "docs",
            "Reference",
            "AI-Agent-Harness-项目管理架构参考.md")));

        var operationalRules = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        var currentPlan = File.ReadAllText(
            Path.Combine(root, "docs", "Archive", "V0.32", "Plan.md"));
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
            ("docs/Archive/V0.32/Plan.md", "docs/Archive/V0.32/Plan.zh-CN.md"),
            ("docs/Archive/V0.33/Plan.md", "docs/Archive/V0.33/Plan.zh-CN.md"),
            ("docs/Archive/V0.33/README.md", "docs/Archive/V0.33/README.zh-CN.md"),
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
        var directoryBuildProps = File.ReadAllText(
            Path.Combine(root, "Directory.Build.props"));

        Assert.Contains(
            "artifacts\\$(Configuration)\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains(
            "$(WinPoolLocalOutputRoot)Agent\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains(
            "$(WinPoolLocalOutputRoot)Agent\\TestWorker\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains(
            "$(WinPoolLocalOutputRoot)Agent\\Broker\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains("BuildAgentRuntime", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bin\\$(Platform)\\$(Configuration)\\net10.0-windows10.0.19041.0",
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
        var switchRuntime = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.App",
                "Services",
                "DataLocationSwitchRuntime.cs"));

        Assert.Contains("DataLocationSwitchRuntime.CreateManager()", settingsPage, StringComparison.Ordinal);
        Assert.Contains("ShutdownReason.StorageLocationSwitch", settingsPage, StringComparison.Ordinal);
        Assert.Contains("SourceManifestSha256", settingsPage, StringComparison.Ordinal);
        Assert.Contains("StorageLocationManager", switchRuntime, StringComparison.Ordinal);
        Assert.Contains("IStorageWriteQuiescenceCoordinator", switchRuntime, StringComparison.Ordinal);
        Assert.Contains("Local\\\\WinPool.Agent.", switchRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageDataLocations.SetModeAsync", settingsPage, StringComparison.Ordinal);
    }

    [Fact]
    public void EditPageSubmitsCanonicalApplicationSimulationOperations()
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
            "ManageSelectionKey? _selectedSelection",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SelectedTopologyStableId",
            workspace,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private WorkspaceCategory _selectedCategory",
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
            ManageWorkspaceCategory.Tier,
            ManageSelectionRules.CategoryFor(ManageObjectRole.DirectDiskGroup));
        Assert.Equal(
            ManageWorkspaceCategory.Disk,
            ManageSelectionRules.CategoryFor(ManageObjectRole.VirtualDisk));
        Assert.Equal(
            ManageWorkspaceCategory.Partition,
            ManageSelectionRules.CategoryFor(ManageObjectRole.NetworkDisk));
        Assert.Equal(
            ManageWorkspaceCategory.Partition,
            ManageSelectionRules.CategoryFor(ManageObjectRole.Partition));
        Assert.Equal(
            ManageWorkspaceCategory.Volume,
            ManageSelectionRules.CategoryFor(ManageObjectRole.Volume));
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
            "agentConnection is null\n            ? new EphemeralWorkspaceStateService()\n            : new AgentBackedWorkspaceStateService(agentConnection)",
            mainWindow.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new LocalWorkspaceStateService()",
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

        Assert.Contains("<WinPoolVersionMajor>0</WinPoolVersionMajor>", versionSource, StringComparison.Ordinal);
        Assert.Contains("<WinPoolVersionMinor>4</WinPoolVersionMinor>", versionSource, StringComparison.Ordinal);
        Assert.Contains("<WinPoolVersionIteration>2</WinPoolVersionIteration>", versionSource, StringComparison.Ordinal);
        Assert.Contains("$(WinPoolArchitectureVersion)$(WinPoolVersionIteration)", versionSource, StringComparison.Ordinal);
        Assert.Contains("<InformationalVersion>$(WinPoolVersion)</InformationalVersion>", versionSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TechnicalVersion", versionSource, StringComparison.Ordinal);
        Assert.Contains("AssemblyInformationalVersionAttribute", productInformation, StringComparison.Ordinal);
        Assert.Contains("$\"{Name}/{Version}\"", productInformation, StringComparison.Ordinal);
        Assert.DoesNotContain("AssemblyFileVersionAttribute", productInformation, StringComparison.Ordinal);
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
        var windowSource = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.App",
                "WelcomeWindow.xaml.cs"));
        var windowXaml = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.App",
                "WelcomeWindow.xaml"));
        var source = File.ReadAllText(
            Path.Combine(
                root,
                "src",
                "WinPool.App",
                "MainWindow.xaml.cs"));

        Assert.Contains("ConfirmButton.Content = localization[\"WelcomeConfirm\"]", windowSource, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConfirmButton\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ConfirmButton_Click\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CycleButton\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"CycleButton_Click\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("WelcomeMascotCatalog.RandomKey", windowSource, StringComparison.Ordinal);
        Assert.Contains("GetDpiForWindow", windowSource, StringComparison.Ordinal);
        Assert.Contains("AppWindowPlacement.ScaleLogicalSize", windowSource, StringComparison.Ordinal);
        Assert.Contains("_xamlRoot.Changed += XamlRoot_Changed", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyDpiAwareSize(sender.RasterizationScale", windowSource, StringComparison.Ordinal);
        Assert.Contains("AppWindowPlacement.CenterOnWorkArea(AppWindow)", windowSource, StringComparison.Ordinal);
        Assert.Contains("AppWindowPlacement.CenterOnWorkArea(AppWindow)", source, StringComparison.Ordinal);
        Assert.Contains("AppWindowPlacement.GetWindowScale(this)", source, StringComparison.Ordinal);
        Assert.Contains("presenter.IsResizable = false", windowSource, StringComparison.Ordinal);
        Assert.Contains("SetBorderAndTitleBar(false, false)", windowSource, StringComparison.Ordinal);
        Assert.Contains("NonClientRegionKind.Caption", windowSource, StringComparison.Ordinal);
        Assert.Contains("NonClientRegionKind.Passthrough", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtendsContentIntoTitleBar = true", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTitleBar(RootLayout)", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPressed", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"#B3000000\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StrokeTop\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StrokeRight\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StrokeBottom\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StrokeLeft\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConfirmButton\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Stretch=\"Uniform\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UniformToFill", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DecodePixelHeight", windowSource, StringComparison.Ordinal);
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
        Assert.DoesNotContain("ContentDialog", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBarSeparatesTheActiveSystemFromNavigation()
    {
        var root = FindRepositoryRoot();
        var windowXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml"));
        var windowSource = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"ActiveSystemBadge\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"1\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"4\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("ActiveSystemBadge.BorderBrush = accent", windowSource, StringComparison.Ordinal);
        Assert.Contains("ActiveSystemBadge.Visibility = system is null", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDiskPropertiesDoesNotRunAFullInventoryScan()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "DesktopAgentRuntime.cs"));
        var coordinator = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "AgentInventoryCoordinator.cs"));
        var start = source.IndexOf(
            "public Task<ApplicationResult<AgentResponse>> OpenNativePropertiesAsync",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public Task<ApplicationResult<AgentResponse>> StartMonitoringAsync",
            start,
            StringComparison.Ordinal);
        var method = source[start..end];

        Assert.Contains("inventoryCoordinator.ResolvePhysicalDeviceId", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectLocalAsync", method, StringComparison.Ordinal);
        Assert.Contains("physicalDeviceIds", coordinator, StringComparison.Ordinal);
        Assert.Contains("deviceResolver.ResolvePnpDeviceId", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("CollectLocalAsync", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopAgentRuntimeDelegatesToFocusedAgentWorkflows()
    {
        var root = FindRepositoryRoot();
        var agentRoot = Path.Combine(root, "src", "WinPool.Agent");
        var runtime = File.ReadAllText(Path.Combine(agentRoot, "DesktopAgentRuntime.cs"));

        foreach (var coordinator in new[]
                 {
                     "AgentTestCoordinator",
                     "AgentSystemSupportCoordinator",
                     "AgentInventoryCoordinator",
                     "TestWorkerSupervisor",
                     "CopyBatchRecoveryCoordinator",
                     "CopyBatchExecutionCoordinator",
                     "AgentTestRunWorkflow",
                     "TestRunStartCoordinator"
                 })
        {
            Assert.True(File.Exists(Path.Combine(agentRoot, coordinator + ".cs")), coordinator);
            Assert.Contains(coordinator, runtime, StringComparison.Ordinal);
        }

        Assert.Contains("systemSupportCoordinator.ReviewAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("systemSupportCoordinator.ExecuteAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("inventoryCoordinator.CaptureManageAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("inventoryCoordinator.CaptureComparisonAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("testCoordinator.TryCancel", runtime, StringComparison.Ordinal);
        Assert.Contains("testRunStartCoordinator.StartAsync", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("RunTestAsync(", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteCopyBatchStepAsync(", runtime, StringComparison.Ordinal);
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
        Assert.Contains("BuildAgentRuntime", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<ProjectReference Include=\"..\\WinPool.Agent\\WinPool.Agent.csproj\"",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains("PublishTestWorkerRuntime", agentProject, StringComparison.Ordinal);
        Assert.Contains("PublishElevatedBrokerRuntime", agentProject, StringComparison.Ordinal);
        Assert.Contains("BuildTestWorkerRuntime", agentProject, StringComparison.Ordinal);
        Assert.Contains("BuildElevatedBrokerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyTestWorkerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyElevatedBrokerRuntime", agentProject, StringComparison.Ordinal);
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

        Assert.Contains("x:Name=\"ContinuousMonitoringCheckBox\"", source, StringComparison.Ordinal);
        Assert.Contains("AccessKey=\"C\"", source, StringComparison.Ordinal);
        Assert.Contains("TextAlignment=\"Right\"", source, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment", source, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyle", source, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TableScroll\"", source, StringComparison.Ordinal);

        var code = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MonitorPage.xaml.cs"));
        Assert.Contains("GridUnitType.Pixel", code, StringComparison.Ordinal);
        Assert.Contains("TableScroll_SizeChanged", code, StringComparison.Ordinal);
        Assert.Contains("SetRateAsync", code, StringComparison.Ordinal);

        var monitoring = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "Services", "MonitoringService.cs"));
        Assert.Contains("existingRate - rateHz", monitoring, StringComparison.Ordinal);
        Assert.Contains("RestartRemoteAsync", monitoring, StringComparison.Ordinal);
        Assert.DoesNotContain("await StopAsync();\r\n        Start(rateHz);", monitoring, StringComparison.Ordinal);
        Assert.DoesNotContain("await StopAsync();\n        Start(rateHz);", monitoring, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomAccentColorDoesNotMutateWinUiThemeDictionaries()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));
        var resources = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "App.xaml"));

        Assert.Contains("SetOwnedBrushColor", source, StringComparison.Ordinal);
        Assert.Contains("AccentFillColorDefaultBrush", source, StringComparison.Ordinal);
        Assert.Contains("UIColorType.AccentLight2", source, StringComparison.Ordinal);
        Assert.Contains("TextOnAccentFillColorPrimaryBrush", resources, StringComparison.Ordinal);
        Assert.Contains("ApplyAccentColor(ViewModel.CurrentPreferences.AccentColor)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WalkResourceDictionaries", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemFillColorAttentionBrush", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SystemControlForegroundAccentBrush", source, StringComparison.Ordinal);
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
        var agentRoot = Path.Combine(root, "src", "WinPool.Agent");
        var workerSupervisor = File.ReadAllText(
            Path.Combine(agentRoot, "TestWorkerSupervisor.cs"));
        var workflow = File.ReadAllText(
            Path.Combine(agentRoot, "AgentTestRunWorkflow.cs"));

        var register = workerSupervisor.IndexOf(
            "await workerProcessRepository.SaveAsync(",
            StringComparison.Ordinal);
        var schedulingPrepare = workerSupervisor.IndexOf(
            "await testProcessSchedulingScope.PrepareAsync(",
            register,
            StringComparison.Ordinal);
        var workerRun = workerSupervisor.IndexOf(
            "return await testWorkerHost.RunAsync(",
            StringComparison.Ordinal);
        var schedulingRestore = workerSupervisor.IndexOf(
            "await testProcessSchedulingScope.RestoreAsync(",
            schedulingPrepare,
            StringComparison.Ordinal);
        var powerPrepare = workflow.IndexOf(
            "powerPlanScope = await testPowerPlanScope.PrepareAsync(",
            StringComparison.Ordinal);
        var localExecution = workflow.IndexOf(
            "var localExecutor = new LocalTestStepExecutor(",
            powerPrepare,
            StringComparison.Ordinal);
        var powerRestore = workflow.IndexOf(
            "await testPowerPlanScope.RestoreAsync(",
            powerPrepare,
            StringComparison.Ordinal);
        var completeRun = workflow.IndexOf(
            "await testRunRepository.CompleteAsync(",
            powerRestore,
            StringComparison.Ordinal);

        Assert.True(workerRun >= 0 && register >= 0);
        Assert.True(schedulingPrepare > register);
        Assert.True(schedulingRestore > schedulingPrepare);
        Assert.True(powerPrepare >= 0 && localExecution > powerPrepare);
        Assert.True(powerRestore > localExecution);
        Assert.True(completeRun > powerRestore);
    }

    [Fact]
    public void TestPageDelegatesDefinitionGraphConstructionToTestingLayer()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "TestPage.xaml.cs"));
        var factory = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Testing", "TestDefinitionFactory.cs"));

        Assert.Contains("new TestDefinitionFactory(", page, StringComparison.Ordinal);
        Assert.Contains(".Build(workload, repeatCount)", page, StringComparison.Ordinal);
        Assert.Contains("copyBatchThresholdMiB", factory, StringComparison.Ordinal);
        Assert.Contains("MixedFileCopyVerification", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildMixedDirectoryDefinition(", page, StringComparison.Ordinal);
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
