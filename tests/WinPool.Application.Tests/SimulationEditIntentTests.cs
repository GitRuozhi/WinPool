using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinPool.Application;

namespace WinPool.Application.Tests;

public sealed class SimulationEditIntentTests
{
    [Fact]
    public void StreamingHashPreservesTheFrozenIntentByteContract()
    {
        var edit = SimulationEditIntent.Freeze(new SimulationEditRequest(
            SimulationEditKind.Rename, "physical:one", Name: "引号\"\\line\n🙂"),
            "exact-commit", DateTimeOffset.UnixEpoch);
        var intent = new SimulationCommitIntent("simulation:one", 7, "exact-commit", [edit]);
        var expected = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(intent)))).ToLowerInvariant();
        Assert.Equal(expected, intent.IntentHash);
        Assert.NotEqual(intent.IntentHash, (intent with { ExpectedRevision = 8 }).IntentHash);
    }
}
