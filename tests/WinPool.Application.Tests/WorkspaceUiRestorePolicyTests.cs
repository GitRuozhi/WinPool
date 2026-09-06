using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class WorkspaceUiRestorePolicyTests
{
    [Fact]
    public void PersistIsBlockedWhileRestoreIsInProgressOrNotYetAllowed()
    {
        Assert.False(WorkspaceUiRestorePolicy.ShouldPersist(restoreInProgress: true, persistAllowed: true));
        Assert.False(WorkspaceUiRestorePolicy.ShouldPersist(restoreInProgress: false, persistAllowed: false));
        Assert.True(WorkspaceUiRestorePolicy.ShouldPersist(restoreInProgress: false, persistAllowed: true));
    }

    [Fact]
    public void EmptyOrMissingStateIsSatisfiedSoFirstLaunchCanPersist()
    {
        Assert.True(WorkspaceUiRestorePolicy.IsRestoreSatisfied(null, "simulation:one", true, false));
        Assert.True(WorkspaceUiRestorePolicy.IsRestoreSatisfied(
            new WorkspaceUiState(),
            "simulation:one",
            savedSystemExists: true,
            rememberedObjectResolved: false));
    }

    [Fact]
    public void MissingSavedSystemIsSatisfiedSoRestoreDoesNotBlockPersistForever()
    {
        var state = Saved("simulation:gone", ManageWorkspaceCategory.Disk, "disk:1");
        Assert.True(WorkspaceUiRestorePolicy.IsRestoreSatisfied(
            state,
            "simulation:one",
            savedSystemExists: false,
            rememberedObjectResolved: false));
    }

    [Fact]
    public void UnresolvedLocalObjectKeepsRestoreUnsatisfied()
    {
        var state = Saved("local:machine", ManageWorkspaceCategory.Disk, "disk:os");
        Assert.False(WorkspaceUiRestorePolicy.IsRestoreSatisfied(
            state,
            "local:machine",
            savedSystemExists: true,
            rememberedObjectResolved: false));
        Assert.True(WorkspaceUiRestorePolicy.IsRestoreSatisfied(
            state,
            "local:machine",
            savedSystemExists: true,
            rememberedObjectResolved: true));
    }

    [Fact]
    public void SystemMismatchStaysUnsatisfiedUntilSwitchCompletes()
    {
        var state = Saved("local:machine", ManageWorkspaceCategory.System, "local:machine");
        Assert.False(WorkspaceUiRestorePolicy.IsRestoreSatisfied(
            state,
            "simulation:one",
            savedSystemExists: true,
            rememberedObjectResolved: true));
    }

    [Fact]
    public void WantedObjectKeyPrefersTheCategorySelection()
    {
        var state = new WorkspaceUiState(
            "Manage",
            "local:machine",
            ManageWorkspaceCategory.Pool,
            new Dictionary<ManageWorkspaceCategory, string>
            {
                [ManageWorkspaceCategory.Pool] = "pool:data"
            },
            "pool:highlighted");
        Assert.Equal("pool:data", WorkspaceUiRestorePolicy.WantedObjectKey(state));
        Assert.Equal(
            "pool:highlighted",
            WorkspaceUiRestorePolicy.WantedObjectKey(new WorkspaceUiState(
                "Manage",
                "local:machine",
                ManageWorkspaceCategory.Pool,
                null,
                "pool:highlighted")));
    }

    private static WorkspaceUiState Saved(
        string systemId,
        ManageWorkspaceCategory category,
        string objectKey) =>
        new(
            "Manage",
            systemId,
            category,
            new Dictionary<ManageWorkspaceCategory, string> { [category] = objectKey },
            objectKey);
}
