using WinPool.Infrastructure.Windows;
using WinPool.Ipc;

namespace WinPool.Infrastructure.Tests;

public sealed class WindowsElevationRestartServiceTests
{
    [Fact]
    public void ElevationHandoffSidHashMatchesTheIpcIdentityConvention()
    {
        const string sid = " s-1-5-21-123456789-987654321-111111111-1001 ";

        Assert.Equal(IpcIdentity.HashUserSid(sid), WindowsElevationRestartService.HashUserSid(sid));
    }
}
