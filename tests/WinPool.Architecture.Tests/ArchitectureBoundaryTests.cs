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
            ["WinPool.Monitoring"] = ["WinPool.Application", "WinPool.Domain"]
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
        var operationalRules = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        var currentPlan = File.ReadAllText(
            Path.Combine(root, "docs", "Archive", "V0.32", "Plan.md"));
        Assert.DoesNotContain("Do not create `Docs/docs`", operationalRules, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/Archive` 的提案已撤销", currentPlan, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentInternalDocumentsAreChineseAuthoritativeAndOnlyRootReadmeIsPaired()
    {
        var root = FindRepositoryRoot();
        Assert.True(File.Exists(Path.Combine(root, "README.md")), "README.md");
        Assert.True(File.Exists(Path.Combine(root, "README.zh-CN.md")), "README.zh-CN.md");
        Assert.False(File.Exists(Path.Combine(root, "README_CN.md")));
        Assert.False(File.Exists(Path.Combine(root, "AGENTS.zh-CN.md")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "Product.zh-CN.md")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "Development.zh-CN.md")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "Quality.zh-CN.md")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "CHANGELOG.zh-CN.md")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "Plan.zh-CN.md")));
        Assert.False(File.Exists(Path.Combine(root, "docs", "Archive", "README.zh-CN.md")));

        var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        Assert.Contains("内部开发文档以中文单一版本为准", agents, StringComparison.Ordinal);
        Assert.Contains("[Design 索引](docs/Design/README.md)", agents, StringComparison.Ordinal);

        var designIndex = Path.Combine(root, "docs", "Design", "README.md");
        Assert.True(File.Exists(designIndex), "docs/Design/README.md");
        var design = File.ReadAllText(designIndex);
        Assert.Contains("设计储备", design, StringComparison.Ordinal);
        Assert.Contains("不自动成为当前要求", design, StringComparison.Ordinal);
        var supersededDesignArchive = Path.Combine(
            root,
            "docs",
            "Archive",
            "20260916-design-review");
        Assert.True(File.Exists(Path.Combine(supersededDesignArchive, "README.md")));
        Assert.True(File.Exists(Path.Combine(
            supersededDesignArchive,
            "WinPool-Hardware-Inventory-Analysis-and-Refactor-Plan.md")));
        Assert.True(File.Exists(Path.Combine(
            supersededDesignArchive,
            "WinPool-Multi-Edition-Plan-Simplified.md")));
        Assert.Contains(
            "已被替代",
            File.ReadAllText(Path.Combine(supersededDesignArchive, "README.md")),
            StringComparison.Ordinal);

        var activePlanPath = Path.Combine(root, "docs", "Plan.md");
        // A completed phase has no active Plan.md; any future active plan remains Chinese.
        if (File.Exists(activePlanPath))
            Assert.Matches("[\\u4e00-\\u9fff]", File.ReadAllText(activePlanPath));
        var completedPlanPath = Path.Combine(root, "docs", "Archive", "V0.52", "Plan.md");
        Assert.True(File.Exists(completedPlanPath), "docs/Archive/V0.52/Plan.md");
        var completedPlan = File.ReadAllText(completedPlanPath);
        Assert.Contains("V0.52", completedPlan, StringComparison.Ordinal);
        Assert.Contains("真实存储只读", completedPlan, StringComparison.Ordinal);
        Assert.DoesNotContain("本 Plan 授权真实存储", completedPlan, StringComparison.Ordinal);

        var archivedPlan = Path.Combine(root, "docs", "Archive", "V0.48", "Plan.md");
        Assert.True(File.Exists(archivedPlan), "docs/Archive/V0.48/Plan.md");
        var plan = File.ReadAllText(archivedPlan);
        Assert.Contains("V0.48", plan, StringComparison.Ordinal);
        Assert.Contains("Design/README.md)不是任务来源", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("执行 V0.48 硬件", plan, StringComparison.Ordinal);
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
            "System.Diagnostics.Process",
            "Process.Start(",
            "ProcessStartInfo",
            "cmd.exe",
            "diskpart"
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
    public void AppAndAgentShareTheLocalRunTreeRoot()
    {
        var root = FindRepositoryRoot();
        var appProject = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "WinPool.App.csproj"));
        var appStartup = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "App.xaml.cs"));
        var agentStartupRegistration = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "AgentStartupRegistration.cs"));
        var agentStartup = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "Program.cs"));
        var tray = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Agent", "TrayApplicationContext.cs"));
        var directoryBuildProps = File.ReadAllText(
            Path.Combine(root, "Directory.Build.props"));
        var directoryBuildTargets = File.ReadAllText(
            Path.Combine(root, "Directory.Build.targets"));

        Assert.Contains(
            "artifacts\\$(Configuration)\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains(
            "artifacts\\trees\\$(Configuration)\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains(
            "<OutputPath>$(WinPoolLocalTreeRoot)App\\</OutputPath>",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains(
            "<OutputPath>$(WinPoolLocalTreeRoot)Agent\\</OutputPath>",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<OutputPath>$(WinPoolLocalOutputRoot)</OutputPath>",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains(
            "<OutputPath>$(WinPoolLocalTreeRoot)App\\</OutputPath>",
            directoryBuildTargets,
            StringComparison.Ordinal);
        Assert.Contains(
            "<OutputPath>$(WinPoolLocalTreeRoot)Agent\\</OutputPath>",
            directoryBuildTargets,
            StringComparison.Ordinal);
        Assert.Contains("Merge-RuntimeTrees.ps1", directoryBuildProps, StringComparison.Ordinal);
        Assert.Contains("MergeLocalAppAndAgentRuntime", directoryBuildTargets, StringComparison.Ordinal);
        Assert.Contains("SHA256", File.ReadAllText(
            Path.Combine(root, "build", "Merge-RuntimeTrees.ps1")), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$(WinPoolLocalOutputRoot)Agent\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$(WinPoolLocalOutputRoot)Agent\\",
            directoryBuildTargets,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$(WinPoolLocalOutputRoot)Agent\\TestWorker\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$(WinPoolLocalOutputRoot)Agent\\Broker\\",
            directoryBuildProps,
            StringComparison.Ordinal);
        Assert.Contains("BuildAgentRuntime", appProject, StringComparison.Ordinal);
        Assert.Contains("SelfContained=true", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RemoveProperties=\"RuntimeIdentifier;SelfContained;Platform;PublishDir\"",
            appProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PublishAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bin\\$(Platform)\\$(Configuration)\\net10.0-windows10.0.19041.0",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"WinPool.Agent.exe\"",
            appStartup.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"Agent\",\n            \"WinPool.Agent.exe\"",
            appStartup.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Environment.ProcessPath",
            agentStartupRegistration,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AppContext.BaseDirectory, \"WinPool.Agent.exe\"",
            agentStartupRegistration,
            StringComparison.Ordinal);
        Assert.Contains(
            "Path.Combine(\n                    AppContext.BaseDirectory,\n                    \"WinPool.App.exe\")",
            agentStartup.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"..\",\n                    \"WinPool.App.exe\"",
            agentStartup.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Path.Combine(AppContext.BaseDirectory, \"WinPool.App.exe\")",
            tray,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreferenceFilesHaveExactlyOneWriterProcessEach()
    {
        var root = FindRepositoryRoot();
        string ReadSources(string project) => string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(root, "src", project),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        var agentSource = ReadSources("WinPool.Agent");
        var appSource = ReadSources("WinPool.App");

        // app-settings.json: only the App persists user preferences; the Agent
        // (tray language projection) reads through IUserPreferencesReader.
        Assert.DoesNotContain("IUserPreferencesService", agentSource, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SaveAsync",
            File.ReadAllText(Path.Combine(
                root,
                "src",
                "WinPool.Agent",
                "TrayApplicationContext.cs")),
            StringComparison.Ordinal);

        // agent-settings.json: only the Agent persists background preferences;
        // the App reads through IAgentPreferencesReader and changes values only
        // through the typed SetAgentPreferenceRequest.
        Assert.DoesNotContain("IAgentPreferencesStore", appSource, StringComparison.Ordinal);

        // The HKCU Run autostart entry belongs to the Agent, which registers
        // its own executable path; the App never touches that registry key.
        Assert.DoesNotContain("CurrentVersion\\Run", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry.CurrentUser", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AppAlignsWindowsDesktopRuntimeWithoutWinFormsUi()
    {
        var root = FindRepositoryRoot();
        var appProject = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "WinPool.App.csproj"));

        Assert.Contains(
            "<FrameworkReference Include=\"Microsoft.WindowsDesktop.App.WindowsForms\" />",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains("align", appProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<UseWindowsForms>", appProject, StringComparison.OrdinalIgnoreCase);

        var appSource = string.Join(
            '\n',
            Directory.EnumerateFiles(
                    Path.Combine(root, "src", "WinPool.App"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.DoesNotContain("System.Windows.Forms", appSource, StringComparison.Ordinal);
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
    public void EditorPagesSubmitCanonicalApplicationSimulationOperations()
    {
        var root = FindRepositoryRoot();
        var structurePage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "StorageStructurePage.xaml.cs"));
        var diskPartitionPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DiskPartitionPage.xaml.cs"));
        var workspace = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "ViewModels", "WorkspaceViewModel.cs"));
        var draftPlanner = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Application", "SimulationDraftPlanner.cs"));

        Assert.Contains(
            "WinPool.Application.SimulationEditRequest",
            structurePage,
            StringComparison.Ordinal);
        Assert.Contains(
            "EditWorkspace.ProjectPoolWorkspace",
            structurePage,
            StringComparison.Ordinal);
        Assert.Contains("SimulationDraftPlanner.Build", structurePage, StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.CreateTieredPool", draftPlanner, StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.DeleteEmptyStoragePool", draftPlanner, StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.EvictPhysicalDiskFromTiers", draftPlanner, StringComparison.Ordinal);
        Assert.Contains(
            "EditWorkspace.DiskNeedsSamePoolTierAssignment",
            draftPlanner,
            StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.SetDiskUsage", draftPlanner, StringComparison.Ordinal);
        Assert.Contains(
            "EditWorkspace.ProjectPartitionWorkspace",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplySimulationOperationAsync",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApplyLocal",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "SimulationEditKind.ExtendPartition",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "SimulationEditKind.ShrinkPartition",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "StorageEditRules.Evaluate",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new SimulationOperationService",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Resize-Partition -",
            diskPartitionPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SimulationEditKind.CreateTieredPool",
            diskPartitionPage,
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
    public void EditorPagesOwnTheirSurfacesWithoutSectionTitles()
    {
        var root = FindRepositoryRoot();
        var structureXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "StorageStructurePage.xaml"));
        var structurePage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "StorageStructurePage.xaml.cs"));
        var diskPartitionXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DiskPartitionPage.xaml"));
        Assert.Contains("TopologyControl", structureXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"320\"", structureXaml, StringComparison.Ordinal);
        Assert.Contains("PoolFormGrid", structureXaml, StringComparison.Ordinal);
        Assert.Contains("TopologyControl", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"320\"", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("PartitionActionButton", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Equal(
            1,
            diskPartitionXaml.Split(
                new[] { "x:Name=\"PartitionActionButton\"" },
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("x:Name=\"CreatePartitionButton\"", diskPartitionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"FormatButton\"", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("PartitionChromeBorder", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("DiskLocationValue", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("StartOffsetValue", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("EndOffsetValue", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("ResetSizeButton", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("SizeChangedIndicator", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("ResetFileSystemButton", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("FileSystemChangedIndicator", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("ResetClusterButton", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("ClusterChangedIndicator", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("ResetQuickFormatButton", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("QuickFormatChangedIndicator", diskPartitionXaml, StringComparison.Ordinal);
        Assert.Contains("ChangeIndicator", structurePage, StringComparison.Ordinal);
        Assert.DoesNotContain("UndoButton", diskPartitionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DiskSectionTitle", diskPartitionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("磁盘与分区", diskPartitionXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("存储池与虚拟磁盘", structureXaml, StringComparison.Ordinal);
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
            "PropertyTableContextMenu.Show(",
            mainPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ManageCategoryCsvExporter",
            mainPage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ShowSourceDetailsAsync",
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
            ManageWorkspaceCategory.Partition,
            ManageSelectionRules.CategoryFor(ManageObjectRole.Volume));
    }

    [Fact]
    public void PropertyTablesExposeOnlyExplicitCopyAndRawFieldContextActions()
    {
        var root = FindRepositoryRoot();
        var contextMenu = File.ReadAllText(Path.Combine(
            root, "src", "WinPool.App", "Controls", "PropertyTableContextMenu.cs"));
        var mainPage = File.ReadAllText(Path.Combine(root, "src", "WinPool.App", "MainPage.xaml.cs"));
        var hardwarePage = File.ReadAllText(Path.Combine(root, "src", "WinPool.App", "HardwarePage.cs"));

        Assert.Contains("CopyCurrentValueText", contextMenu, StringComparison.Ordinal);
        Assert.Contains("CopyGroupText", contextMenu, StringComparison.Ordinal);
        Assert.Contains("RawFieldsText", contextMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Select all", contextMenu, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ColumnCell_RightTapped", mainPage, StringComparison.Ordinal);
        Assert.Contains("GroupCell_RightTapped", hardwarePage, StringComparison.Ordinal);
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
        Assert.Contains("(VirtualKey.Number2, ShellPageKind.StorageStructure)", mainWindow);
        Assert.Contains("(VirtualKey.Number3, ShellPageKind.DiskPartition)", mainWindow);
        Assert.Contains("(VirtualKey.Number4, ShellPageKind.Test)", mainWindow);
        Assert.Contains("(VirtualKey.Number5, ShellPageKind.Monitor)", mainWindow);
        Assert.Contains("(VirtualKey.Number6, ShellPageKind.Development)", mainWindow);
        Assert.Contains("(VirtualKey.Number7, ShellPageKind.Settings)", mainWindow);
        Assert.Contains("args.Handled = true;", mainWindow);
    }

    [Fact]
    public void DeveloperNavigationIsDisabledByDefaultAndGatesAllThreePages()
    {
        var root = FindRepositoryRoot();
        var preferences = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Domain", "Preferences.cs"));
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));
        var settings = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "SettingsPage.xaml"));

        Assert.Contains("bool DeveloperMode = false", preferences, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CurrentPreferences.DeveloperMode", mainWindow, StringComparison.Ordinal);
        Assert.Contains("IsDeveloperPage(page)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ShellPageKind.Hardware or ShellPageKind.Test or ShellPageKind.Development", mainWindow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DeveloperModeSwitch\"", settings, StringComparison.Ordinal);
        Assert.True(
            mainWindow.IndexOf("ShellPageKind.Hardware, string.Empty", StringComparison.Ordinal)
            < mainWindow.IndexOf("ShellPageKind.Manage, string.Empty", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsKeepDeveloperModeOutOfTheAppearanceCardAndStopUpdatingAfterElevationHandoff()
    {
        var root = FindRepositoryRoot();
        var settingsMarkup = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "SettingsPage.xaml"));
        var settingsCode = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "SettingsPage.xaml.cs"));
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));

        var firstCardEnd = settingsMarkup.IndexOf("</Border>", StringComparison.Ordinal);
        var developerMode = settingsMarkup.IndexOf("x:Name=\"DeveloperModeSwitch\"", StringComparison.Ordinal);

        Assert.True(firstCardEnd >= 0);
        Assert.True(developerMode > firstCardEnd);
        Assert.Contains("if (await mainWindow.RequestExecutionModeAsync(requestedMode))", settingsCode, StringComparison.Ordinal);
        Assert.Contains("return true;", mainWindow, StringComparison.Ordinal);
        Assert.Contains("if (_closingForElevationHandoff)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ViewModel.Monitoring.Dispose();", mainWindow, StringComparison.Ordinal);
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
        Assert.Contains("<WinPoolVersionMinor>5</WinPoolVersionMinor>", versionSource, StringComparison.Ordinal);
        Assert.Contains("<WinPoolVersionIteration>5</WinPoolVersionIteration>", versionSource, StringComparison.Ordinal);
        Assert.Contains("$(WinPoolArchitectureVersion)0", versionSource, StringComparison.Ordinal);
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
            "OpenAsync(ProductInformation.UpdateUri)",
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
    public void MainWindowPreviewsSavedWorkspaceBeforeAgentRestore()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));
        var windowXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml"));
        var pageXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainPage.xaml"));
        var notificationCardXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "Controls", "NotificationCard.xaml"));
        var loadedStart = source.IndexOf(
            "private async void RootGrid_Loaded",
            StringComparison.Ordinal);
        var loadedEnd = source.IndexOf(
            "private async void MainWindow_Closed",
            StringComparison.Ordinal);
        Assert.True(loadedStart >= 0 && loadedEnd > loadedStart);
        var loaded = source[loadedStart..loadedEnd];
        var hide = loaded.IndexOf("RootFrame.Visibility = Visibility.Collapsed", StringComparison.Ordinal);
        var preferences = loaded.IndexOf("await ViewModel.InitializePreferencesAsync()", StringComparison.Ordinal);
        var navigate = loaded.IndexOf("NavigateStartupPage()", StringComparison.Ordinal);
        var preview = loaded.IndexOf("LoadStartupWorkspacePreviewAsync(", StringComparison.Ordinal);
        var wait = loaded.IndexOf("await agentConnectionTask", StringComparison.Ordinal);
        Assert.True(hide >= 0 && hide < preferences);
        Assert.True(navigate > preferences && preview > navigate && wait > preview);
        var history = loaded.IndexOf("_ = _agentInventorySynchronizer.LoadHistoryAsync()", StringComparison.Ordinal);
        Assert.True(history > navigate && history < preview);
        Assert.Contains("ViewModel.ApplyWorkspaceStartupPreview", loaded, StringComparison.Ordinal);
        Assert.Contains("RootFrame.IsHitTestVisible = false", loaded, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressRing", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowInventoryStatus", pageXaml, StringComparison.Ordinal);
        // Cards moved into their own reusable control. Keep the presentation
        // to one InfoBar surface without restoring a decorative wrapper.
        Assert.Contains("<InfoBar", notificationCardXaml, StringComparison.Ordinal);
        Assert.Equal(
            1,
            notificationCardXaml.Split(new[] { "<InfoBar" }, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("<Border", notificationCardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"Transparent\"", notificationCardXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GlobalNotificationStack\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("BeginWorkspacePrepare()", loaded, StringComparison.Ordinal);
        Assert.Contains("CompleteWorkspacePrepare()", loaded, StringComparison.Ordinal);
    }

    [Fact]
    public void TitleBarProvidesStorageSystemSelector()
    {
        var root = FindRepositoryRoot();
        var windowXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml"));
        var windowSource = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"ActiveSystemSelector\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Grid.Column=\"2\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Right\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("<ComboBox", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"320\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("DropDownClosed=\"ActiveSystemSelector_DropDownClosed\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"ActiveSystemSelector_SelectionChanged\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment\" Value=\"Left\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment = HorizontalAlignment.Left", windowSource, StringComparison.Ordinal);
        Assert.Contains("if (ActiveSystemSelector.IsDropDownOpen)", windowSource, StringComparison.Ordinal);
        Assert.Contains("_systemSelectorRefreshPending = true", windowSource, StringComparison.Ordinal);
        Assert.Contains("_pendingSystemSelectionId = systemId", windowSource, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectSystem(systemId)", windowSource, StringComparison.Ordinal);
        Assert.Contains("RefreshSelectedSystemEditor();", windowSource, StringComparison.Ordinal);
        var refreshEditor = windowSource[windowSource.IndexOf(
            "private void RefreshSelectedSystemEditor()", StringComparison.Ordinal)..];
        refreshEditor = refreshEditor[..refreshEditor.IndexOf(
            "private void ViewModel_WorkspaceSelectionChanged", StringComparison.Ordinal)];
        Assert.Contains("if (_editorSystemId == ViewModel.SelectedSystem.SystemId)", refreshEditor, StringComparison.Ordinal);
        Assert.True(refreshEditor.IndexOf("if (_editorSystemId ==", StringComparison.Ordinal)
            < refreshEditor.IndexOf("SelectShellPage(SelectedShellItem.Page)", StringComparison.Ordinal));
        Assert.Equal(2, windowSource.Split("_editorSystemId = ViewModel.SelectedSystem.SystemId;").Length - 1);
        Assert.Contains("ShellPageKind.StorageStructure or ShellPageKind.DiskPartition", windowSource, StringComparison.Ordinal);
        Assert.Contains("ActiveSystemSelector.BorderBrush = accent", windowSource, StringComparison.Ordinal);
        Assert.Contains("elements.Add(ActiveSystemSelector)", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshWorkspaceDefaultsToTheLocalSystem()
    {
        var root = FindRepositoryRoot();
        var workspace = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "ViewModels", "WorkspaceViewModel.cs"));

        Assert.Contains(
            "SelectedSystem = SystemCatalog.Systems.First(system => system.IsLocal);",
            workspace,
            StringComparison.Ordinal);
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
                     "AgentInventoryCoordinator",
                     "AgentShutdownWorkflow",
                     "AgentSessionCoordinator"
                 })
        {
            Assert.True(File.Exists(Path.Combine(agentRoot, coordinator + ".cs")), coordinator);
        }

        Assert.Contains("AgentInventoryCoordinator", runtime, StringComparison.Ordinal);
        Assert.Contains("MonitoringSessionCoordinator", runtime, StringComparison.Ordinal);
        Assert.Contains("inventoryCoordinator.CaptureManageAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("inventoryCoordinator.CaptureComparisonAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("monitoring.StartAsync", runtime, StringComparison.Ordinal);
        Assert.Contains("monitoring.StopAsync", runtime, StringComparison.Ordinal);
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
        var mergeScript = File.ReadAllText(
            Path.Combine(root, "build", "Merge-RuntimeTrees.ps1"));

        Assert.DoesNotContain("PublishAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.Contains("BuildAgentRuntime", appProject, StringComparison.Ordinal);
        Assert.Contains("<SelfContained>true</SelfContained>", agentProject, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildTestWorkerRuntime", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildElevatedBrokerRuntime", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishTestWorkerRuntime", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishElevatedBrokerRuntime", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyAgentRuntimeBesideApp", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "<ProjectReference Include=\"..\\WinPool.Agent\\WinPool.Agent.csproj\"",
            appProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain("PublishTestWorkerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishElevatedBrokerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildTestWorkerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildElevatedBrokerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyTestWorkerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyElevatedBrokerRuntime", agentProject, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO.Path]::GetFullPath", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("System.IO.Path]::GetFullPath", agentProject, StringComparison.Ordinal);
        Assert.Contains("SHA256", mergeScript, StringComparison.Ordinal);
        Assert.Contains("collision", mergeScript, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestPageIsAWinPoolTwoRoadmapPlaceholder()
    {
        var root = FindRepositoryRoot();
        var view = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "TestPage.xaml"));
        var page = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "TestPage.xaml.cs"));

        Assert.Contains("WinPool 2.0", view, StringComparison.Ordinal);
        Assert.Contains("WinPool 1.x", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ChooseTargetButton", view, StringComparison.Ordinal);
        Assert.DoesNotContain("NumberBox", view, StringComparison.Ordinal);
        Assert.DoesNotContain("TestDefinitionFactory", page, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgentConnection", page, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorPageBackgroundModeHasStableKeyboardAccessKey()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MonitorPage.xaml"));

        Assert.Contains("x:Name=\"ContinuousMonitoringSwitch\"", source, StringComparison.Ordinal);
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
        var scan = WorkspaceNotificationFactory.ScanStarted("inventory:manual:storage");
        var failed = WorkspaceNotificationFactory.OperationFailed("operation:test");

        Assert.True(ApplicationNotificationValidator.IsValid(scan));
        Assert.Equal(ApplicationNotificationSeverity.Information, scan.Severity);
        Assert.False(scan.AutoDismiss);
        Assert.Equal("inventory:manual:storage", scan.OccurrenceKey);
        Assert.True(ApplicationNotificationValidator.IsValid(failed));
        Assert.Equal(ApplicationNotificationSeverity.Error, failed.Severity);

        var root = FindRepositoryRoot();
        var workspace = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "ViewModels", "WorkspaceViewModel.cs"));
        var mainPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainPage.xaml.cs"));
        Assert.Contains("WorkspaceNotificationFactory.ScanStarted(progressKey)", workspace, StringComparison.Ordinal);
        Assert.Contains("WorkspaceNotificationFactory.ScanCompleted(", workspace, StringComparison.Ordinal);
        Assert.Contains("WorkspaceNotificationFactory.ScanFailed(", workspace, StringComparison.Ordinal);
        Assert.Contains("ManualProgressKey", workspace, StringComparison.Ordinal);
        Assert.Contains("inventory:manual:", workspace, StringComparison.Ordinal);
        // Workspace UI publishes the final localized text through one metadata
        // carrying entry point. It must not regress to the retired factory
        // calls that lost the current system and target identity.
        Assert.Contains("PublishWorkspaceInfo(", mainPage, StringComparison.Ordinal);
        Assert.Contains("ViewModel.NotificationService.Publish(", mainPage, StringComparison.Ordinal);
        Assert.Contains("new GlobalNotificationOptions", mainPage, StringComparison.Ordinal);
        Assert.Contains("Code = code", mainPage, StringComparison.Ordinal);
        Assert.Contains("SystemId = ViewModel.SelectedSystem.Id", mainPage, StringComparison.Ordinal);
        Assert.Contains("Target = OperationTarget()", mainPage, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceNotificationFactory.ExportCompleted(", mainPage, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceNotificationFactory.ImportCompleted(", mainPage, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceNotificationFactory.OperationFailed(", mainPage, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentPageRemainsGatedAndShowsOnlyInMemoryMessages()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DevelopmentPage.xaml.cs"));
        var viewPath = Path.Combine(root, "src", "WinPool.App", "DevelopmentPage.xaml");
        var view = File.ReadAllText(viewPath);
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace toolkit = "using:CommunityToolkit.WinUI.Controls";
        var layout = XDocument.Load(viewPath).Root?.Element(presentation + "Grid")
            ?? throw new InvalidOperationException("The Development page must define its root layout grid.");
        var areas = layout.Elements(presentation + "Border").ToArray();
        Assert.Equal(3, areas.Length);
        Assert.Equal(
            new[] { "MessageListArea", "RightTopEmptyArea", "LowerEmptyArea" },
            areas.Select(area => (string?)area.Attribute(xaml + "Name")).ToArray());
        var rowDefinitions = layout.Element(presentation + "Grid.RowDefinitions")
            ?? throw new InvalidOperationException("The Development page must define three rows.");
        var columnDefinitions = layout.Element(presentation + "Grid.ColumnDefinitions")
            ?? throw new InvalidOperationException("The Development page must define three columns.");
        Assert.Equal(3, rowDefinitions.Elements(presentation + "RowDefinition").Count());
        Assert.Equal(3, columnDefinitions.Elements(presentation + "ColumnDefinition").Count());
        Assert.Equal("0", (string?)areas[0].Attribute("Grid.Row"));
        Assert.Equal("0", (string?)areas[0].Attribute("Grid.Column"));
        Assert.Equal("0", (string?)areas[1].Attribute("Grid.Row"));
        Assert.Equal("2", (string?)areas[1].Attribute("Grid.Column"));
        Assert.Equal("2", (string?)areas[2].Attribute("Grid.Row"));
        Assert.Equal("3", (string?)areas[2].Attribute("Grid.ColumnSpan"));
        Assert.Empty(areas[1].Elements());
        var lowerAreaContent = areas[2].Elements().ToArray();
        Assert.Single(lowerAreaContent);
        Assert.Equal("TextBlock", lowerAreaContent[0].Name.LocalName);
        Assert.Equal("AiEntryHint", (string?)lowerAreaContent[0].Attribute(xaml + "Name"));
        var splitters = layout.Elements(toolkit + "GridSplitter").ToArray();
        Assert.Equal(2, splitters.Length);
        Assert.Equal(
            new[] { "TopAreaColumnSplitter", "TopBottomAreaSplitter" },
            splitters.Select(splitter => (string?)splitter.Attribute(xaml + "Name")).ToArray());
        Assert.Contains(splitters, splitter => (string?)splitter.Attribute("ResizeDirection") == "Columns");
        Assert.Contains(splitters, splitter => (string?)splitter.Attribute("ResizeDirection") == "Rows");

        Assert.Contains("if (ViewModel.CurrentPreferences.DeveloperMode)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ShellPageKind.Development", mainWindow, StringComparison.Ordinal);
        Assert.Contains("private static bool IsDeveloperPage(ShellPageKind page)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("!IsDeveloperPage(page) || ViewModel.CurrentPreferences.DeveloperMode", mainWindow, StringComparison.Ordinal);
        Assert.Contains("if (!IsShellPageAvailable(page))", mainWindow, StringComparison.Ordinal);
        Assert.Contains("MessageList", view, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Single\"", view, StringComparison.Ordinal);
        Assert.Contains("DoubleTapped=\"MessageList_DoubleTapped\"", view, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Summary}\"", view, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"NoWrap\"", view, StringComparison.Ordinal);
        Assert.Contains("MessageRowVisual_PointerEntered", view, StringComparison.Ordinal);
        Assert.Contains("MessageRowSelectedFill", view, StringComparison.Ordinal);
        Assert.Contains("MessageRowSelectionIndicator", view, StringComparison.Ordinal);
        Assert.Contains("MessageList_SelectionChanged", view, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageListHeader", view, StringComparison.Ordinal);
        Assert.Contains("EmptyMessageListText.Text = Text(\"本次运行没有消息。\", \"No messages in this run.\")", page, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(MessageList, Text(\"消息列表\", \"Message list\"))", page, StringComparison.Ordinal);
        Assert.DoesNotContain("日志", page, StringComparison.Ordinal);
        Assert.DoesNotContain("本次运行没有日志", page, StringComparison.Ordinal);
        Assert.DoesNotContain("LogArea", page, StringComparison.Ordinal);
        Assert.Contains("FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)", page, StringComparison.Ordinal);
        Assert.Contains("row?.Content is not SessionMessageItem item", page, StringComparison.Ordinal);
        Assert.Contains("ShowMessageDetails(item.Notification)", page, StringComparison.Ordinal);
        var detailOverlay = layout.Elements(presentation + "Grid")
            .Single(element => (string?)element.Attribute(xaml + "Name") == "MessageDetailOverlay");
        Assert.Equal("3", (string?)detailOverlay.Attribute("Grid.RowSpan"));
        Assert.Equal("3", (string?)detailOverlay.Attribute("Grid.ColumnSpan"));
        Assert.Equal("#80000000", (string?)detailOverlay.Attribute("Background"));
        Assert.Equal("Collapsed", (string?)detailOverlay.Attribute("Visibility"));
        var detailContent = detailOverlay.Elements().ToArray();
        Assert.Single(detailContent);
        Assert.Equal("TextBox", detailContent[0].Name.LocalName);
        Assert.Equal("True", (string?)detailContent[0].Attribute("IsReadOnly"));
        Assert.Equal("Wrap", (string?)detailContent[0].Attribute("TextWrapping"));
        Assert.Equal("Auto", (string?)detailContent[0].Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        Assert.Contains("MessageDetailOverlay_Tapped", page, StringComparison.Ordinal);
        Assert.Contains("CloseMessageDetails()", page, StringComparison.Ordinal);
        Assert.Contains("FindAncestor<TextBox>(e.OriginalSource as DependencyObject)", page, StringComparison.Ordinal);
        Assert.Contains("textMeasure.Measure(new Size(contentWidth, double.PositiveInfinity))", page, StringComparison.Ordinal);
        Assert.DoesNotContain("new Flyout", page, StringComparison.Ordinal);
        Assert.DoesNotContain("flyout.ShowAt", page, StringComparison.Ordinal);
        Assert.Contains("人工智能入口，功能正在开发中。", page, StringComparison.Ordinal);
        Assert.Contains("AI entry — feature in development.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<InfoBar", view, StringComparison.Ordinal);
        Assert.Contains("NotificationService.History", page, StringComparison.Ordinal);
        Assert.DoesNotContain("DiagnosticsPathText", view, StringComparison.Ordinal);
        Assert.DoesNotContain("DiagnosticsDirectoryPath", page, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearHistory()", page, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyAllMessagesButton", view, StringComparison.Ordinal);
        Assert.DoesNotContain("<Button", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.CreateDirectory", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.EnumerateFiles", page, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Read", page, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Open", page, StringComparison.Ordinal);
        Assert.DoesNotContain("GetDevelopmentDiagnosticsRequest", page, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgentConnection", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", page, StringComparison.Ordinal);
    }

    [Fact]
    public void NotificationShellKeepsThreeSimpleCardsAndDeveloperOnlyDetails()
    {
        var root = FindRepositoryRoot();
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));
        var windowXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml"));
        var card = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "Controls", "NotificationCard.xaml.cs"));
        var cardXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "Controls", "NotificationCard.xaml"));
        var notificationService = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Application", "GlobalNotificationService.cs"));
        var developmentPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DevelopmentPage.xaml.cs"));
        var monitorXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MonitorPage.xaml"));
        var monitorPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MonitorPage.xaml.cs"));
        var testPage = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "TestPage.xaml"));

        Assert.Contains("MaximumVisibleNotificationCards = 3", mainWindow, StringComparison.Ordinal);
        Assert.Contains("DefaultActiveCapacity = 3", notificationService, StringComparison.Ordinal);
        Assert.Contains("VisibleNotificationCapacity = DefaultActiveCapacity", notificationService, StringComparison.Ordinal);
        Assert.Contains("NotificationService = new GlobalNotificationService()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("if (_notifications.Count >= activeCapacity)", notificationService, StringComparison.Ordinal);
        Assert.Contains(".Take(GetMaximumVisibleNotificationCards())", mainWindow, StringComparison.Ordinal);
        Assert.Contains("RootGrid.ActualHeight", mainWindow, StringComparison.Ordinal);
        Assert.Contains("MaximumNotificationCardHeightDip = 200", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(count, 1, MaximumVisibleNotificationCards)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("Width=\"384\"", cardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinHeight=", cardXaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"200\"", cardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Height=\"128\"", cardXaml, StringComparison.Ordinal);
        Assert.Contains("To=\"420\"", cardXaml, StringComparison.Ordinal);
        Assert.Contains("Duration=\"0:0:0.24\"", cardXaml, StringComparison.Ordinal);
        Assert.Contains("ExitAnimationCompleted=\"NotificationCard_ExitAnimationCompleted\"", windowXaml, StringComparison.Ordinal);
        Assert.Contains("if (VisibleNotifications.Any(item => item.IsDismissing))", mainWindow, StringComparison.Ordinal);
        Assert.Contains("CompleteNotificationExit(item)", mainWindow, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationService.History", mainWindow, StringComparison.Ordinal);
        Assert.Contains("DismissExpired()", mainWindow, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(1)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ShowErrorNotificationMessageAsync", mainWindow, StringComparison.Ordinal);
        Assert.Contains("await DialogCoordinator.ShowAsync(dialog, RootGrid.XamlRoot)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("NotificationService.History", developmentPage, StringComparison.Ordinal);
        Assert.Contains("进入开发页消息列表查看详情", card, StringComparison.Ordinal);
        Assert.Contains("Open the Developer message list for details", card, StringComparison.Ordinal);
        Assert.Contains("notification.Message", card, StringComparison.Ordinal);
        Assert.DoesNotContain("CreatedAt <= cutoff", mainWindow, StringComparison.Ordinal);
        Assert.Contains("NotificationCard", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Invoked=\"NotificationCard_Invoked\"", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationOverflowButton", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalRealOperationsWarning", windowXaml, StringComparison.Ordinal);
        Assert.Contains("NotificationCard_Tapped", card, StringComparison.Ordinal);
        Assert.Contains("NotificationCard_KeyDown", card, StringComparison.Ordinal);
        Assert.Contains("进入开发页消息列表查看详情", card, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailsButton", card, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseButton", card, StringComparison.Ordinal);
        Assert.Contains("IsChinese", card, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties", card, StringComparison.Ordinal);
        Assert.Contains("DefaultErrorAutoDismissDuration", notificationService, StringComparison.Ordinal);
        Assert.DoesNotContain("MergeRepeated", notificationService, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveOverflow", notificationService, StringComparison.Ordinal);
        Assert.DoesNotContain("MonitorIssueRows", monitorXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DismissMonitorIssue", monitorPage, StringComparison.Ordinal);
        Assert.Contains("UpdateIssueStates(", monitorPage, StringComparison.Ordinal);
        Assert.Contains("PublishMonitorIssueTransitions(", monitorPage, StringComparison.Ordinal);
        Assert.DoesNotContain("<InfoBar", testPage, StringComparison.Ordinal);
    }

    [Fact]
    public void IpcProtocolCurrentVersionIsEleven()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Ipc", "IpcProtocol.cs"));

        Assert.Contains("public const int CurrentVersion = 11;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("= 4;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SqliteStoreSchemaVersionIsSeventeen()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Infrastructure.Sqlite", "WinPoolSqliteStore.cs"));

        Assert.Contains("public const int CurrentSchemaVersion = 17;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("= 14;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsTargetedProjectsShareThe26100Tfm()
    {
        var root = FindRepositoryRoot();
        const string expected = "net10.0-windows10.0.26100.0";
        var projectFiles = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                return relative.StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith("tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            });

        foreach (var projectFile in projectFiles)
        {
            var tfm = XDocument.Load(projectFile)
                .Descendants("TargetFramework")
                .Select(element => element.Value.Trim())
                .FirstOrDefault();
            if (tfm is null || !tfm.StartsWith("net10.0-windows", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.Equal(expected, tfm);
        }

        var appProject = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "WinPool.App.csproj"));
        Assert.Contains(
            "Microsoft.Windows.SDK.BuildTools\" Version=\"10.0.28000.2705\"",
            appProject,
            StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.26100.7705", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("net10.0-windows10.0.28000.0", appProject, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsAppSdkIs24Stable()
    {
        var root = FindRepositoryRoot();
        var appProject = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "WinPool.App.csproj"));
        Assert.Contains(
            "Microsoft.WindowsAppSDK\" Version=\"2.4.0\"",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK.AI", appProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK.ML", appProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK.Search", appProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK.Widgets", appProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.AI.MachineLearning", appProject, StringComparison.Ordinal);
        Assert.Contains("<ExcludeAssets>all</ExcludeAssets>", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("1.8.260416003", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("experimental", appProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preview", appProject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SolutionDoesNotContainRetiredProjects()
    {
        var root = FindRepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(root, "WinPool.slnx"));
        var retired = new[]
        {
            "WinPool.Testing",
            "WinPool.Testing.Tools",
            "WinPool.ToolManagement",
            "WinPool.TestWorker",
            "WinPool.ElevatedBroker"
        };

        Assert.All(
            retired,
            name => Assert.DoesNotContain(name, solution, StringComparison.Ordinal));
    }

    [Fact]
    public void NoProductionProjectReferencesRetiredSubsystems()
    {
        var root = FindRepositoryRoot();
        var retired = new[]
        {
            "WinPool.Testing",
            "WinPool.Testing.Tools",
            "WinPool.ToolManagement",
            "WinPool.TestWorker",
            "WinPool.ElevatedBroker"
        };
        var projectFiles = Directory.EnumerateFiles(
                Path.Combine(root, "src"),
                "*.csproj",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.All(
            projectFiles,
            path =>
            {
                var content = File.ReadAllText(path);
                Assert.All(
                    retired,
                    name => Assert.DoesNotContain(
                        $"Include=\"..\\{name}\\{name}.csproj\"",
                        content,
                        StringComparison.Ordinal));
            });
    }

    [Fact]
    public void TestAndDevelopmentPagesRemainBehindDeveloperModeNavigationGate()
    {
        var root = FindRepositoryRoot();
        var testPageXaml = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "TestPage.xaml"));
        var testPageCode = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "TestPage.xaml.cs"));
        var developmentPageCode = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DevelopmentPage.xaml.cs"));
        var mainWindow = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "MainWindow.xaml.cs"));

        Assert.Contains("WinPool 2.0", testPageXaml, StringComparison.Ordinal);
        Assert.Contains("WinPool 1.x", testPageXaml, StringComparison.Ordinal);
        Assert.Contains("ShellPageKind.Test or ShellPageKind.Development", mainWindow, StringComparison.Ordinal);
        Assert.Contains("!IsDeveloperPage(page) || ViewModel.CurrentPreferences.DeveloperMode", mainWindow, StringComparison.Ordinal);
        Assert.Contains("if (!IsShellPageAvailable(page))", mainWindow, StringComparison.Ordinal);

        foreach (var pageCode in new[] { testPageCode, developmentPageCode })
        {
            Assert.DoesNotContain("IAgentConnection", pageCode, StringComparison.Ordinal);
            Assert.DoesNotContain("TestDefinitionFactory", pageCode, StringComparison.Ordinal);
            Assert.DoesNotContain("GetDevelopmentDiagnosticsRequest", pageCode, StringComparison.Ordinal);
            Assert.DoesNotContain("Process.Start", pageCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TopologySurfacesKeepPerSurfaceViewportState()
    {
        var root = FindRepositoryRoot();
        var appDirectory = Path.Combine(root, "src", "WinPool.App");
        var sources = Directory.EnumerateFiles(appDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // No shared topology viewport state may exist: Manage, the storage
        // structure editor, and the disk/partition editor each keep their own
        // host width, so resizing one surface can never mutate another
        // surface's layout inputs.
        Assert.All(
            sources,
            source => Assert.DoesNotContain(
                "TopologyViewportWidth",
                File.ReadAllText(source),
                StringComparison.Ordinal));

        var workspace = File.ReadAllText(
            Path.Combine(appDirectory, "ViewModels", "WorkspaceViewModel.cs"));
        Assert.Contains("DefaultSurfaceViewportWidth", workspace, StringComparison.Ordinal);

        var nodeViewModel = File.ReadAllText(
            Path.Combine(appDirectory, "ViewModels", "TopologyNodeViewModel.cs"));
        Assert.Contains("SetSurfaceViewportWidth", nodeViewModel, StringComparison.Ordinal);

        // Each editor page keeps its own surface width and applies it to
        // each recreated topology root.
        var structurePage = File.ReadAllText(
            Path.Combine(appDirectory, "StorageStructurePage.xaml.cs"));
        var diskPartitionPage = File.ReadAllText(
            Path.Combine(appDirectory, "DiskPartitionPage.xaml.cs"));
        Assert.Contains("_viewportWidth", structurePage, StringComparison.Ordinal);
        Assert.Contains("_viewportWidth", diskPartitionPage, StringComparison.Ordinal);
    }

    [Fact]
    public void StructureEditorNeverCreatesASecondVirtualDisk()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "StorageStructurePage.xaml.cs"));
        var draftPlanner = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.Application", "SimulationDraftPlanner.cs"));
        // One virtual disk is the 1.0 contract: the structure page can
        // create the one virtual disk only while the pool has none (SC4),
        // and Delete removes the selected virtual disk including the last
        // one. A pool that arrives with several may only be reduced to one;
        // there is no path that adds a second disk to a pool that has one.
        Assert.Contains("SimulationDraftPlanner.Build", page, StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.CreateVirtualDisk", draftPlanner, StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.DeleteVirtualDisk", draftPlanner, StringComparison.Ordinal);
        Assert.Contains("AppendPartitionIntents(enriched);", page, StringComparison.Ordinal);
        Assert.Contains("createVdisk?.AllocatedOsDiskId is not null", page, StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.CreatePartition", page, StringComparison.Ordinal);
        Assert.Contains("EditWorkspace.HasMultipleVirtualDisks", page, StringComparison.Ordinal);
        Assert.Contains("SimulationEditKind.CreateTieredPool", draftPlanner, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskPartitionFormRestoresFileSystemChoicesForEachSelection()
    {
        var root = FindRepositoryRoot();
        var page = File.ReadAllText(
            Path.Combine(root, "src", "WinPool.App", "DiskPartitionPage.xaml.cs"));

        Assert.Contains("FillFileSystemChoices(partition);", page, StringComparison.Ordinal);
        Assert.Contains("case \"EfiSystem\":", page, StringComparison.Ordinal);
        Assert.Contains("case \"MicrosoftReserved\":", page, StringComparison.Ordinal);
        Assert.Contains("case \"WindowsRecovery\":", page, StringComparison.Ordinal);
        Assert.Contains("FillFileSystemBox();", page, StringComparison.Ordinal);
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
