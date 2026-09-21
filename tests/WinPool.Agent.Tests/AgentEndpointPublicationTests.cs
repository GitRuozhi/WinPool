using WinPool.Agent;
using WinPool.Ipc;
using System.Text.Json;

namespace WinPool.Agent.Tests;

public sealed class AgentEndpointPublicationTests
{
    [Fact]
    public void EndpointCleanupAcceptsOnlyThePublishingAgentIdentity()
    {
        var endpoint = new AgentEndpointRecord(
            IpcProtocol.CurrentVersion,
            "WinPool.Agent.Control.test",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1234,
            DateTimeOffset.UtcNow);

        Assert.True(Program.IsCurrentEndpoint(endpoint, endpoint));
        Assert.False(Program.IsCurrentEndpoint(
            endpoint with { AgentSessionId = Guid.NewGuid() },
            endpoint));
        Assert.False(Program.IsCurrentEndpoint(
            endpoint with { Nonce = Guid.NewGuid() },
            endpoint));
        Assert.False(Program.IsCurrentEndpoint(
            endpoint with { ProcessId = endpoint.ProcessId + 1 },
            endpoint));
    }

    [Fact]
    public void EndpointPublicationCleansItsOwnFileOnStartupFailureButPreservesReplacement()
    {
        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "WinPool.AgentEndpoint.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var endpointPath = Path.Combine(fixtureRoot, "agent-endpoint.json");
        var endpoint = new AgentEndpointRecord(
            IpcProtocol.CurrentVersion,
            "WinPool.Agent.Control.test",
            Guid.NewGuid(),
            Guid.NewGuid(),
            1234,
            DateTimeOffset.UtcNow);

        File.WriteAllText(endpointPath, JsonSerializer.Serialize(endpoint));

        Action failStartup = () =>
        {
            using var publication = new Program.PublishedEndpointLease(endpointPath, endpoint);
            throw new InvalidOperationException("Injected startup failure.");
        };
        Assert.Throws<InvalidOperationException>(failStartup);

        Assert.False(File.Exists(endpointPath));

        var replacement = endpoint with
        {
            AgentSessionId = Guid.NewGuid(),
            Nonce = Guid.NewGuid(),
            ProcessId = endpoint.ProcessId + 1,
        };
        File.WriteAllText(endpointPath, JsonSerializer.Serialize(endpoint));

        Action failAfterReplacement = () =>
        {
            using var publication = new Program.PublishedEndpointLease(endpointPath, endpoint);
            File.WriteAllText(endpointPath, JsonSerializer.Serialize(replacement));
            throw new InvalidOperationException("Injected startup failure after endpoint replacement.");
        };
        Assert.Throws<InvalidOperationException>(failAfterReplacement);

        Assert.True(File.Exists(endpointPath));
        var persisted = JsonSerializer.Deserialize<AgentEndpointRecord>(File.ReadAllText(endpointPath));
        Assert.Equal(replacement, persisted);

        // Keep the isolated fixture for inspection; never clean a user data root.
    }
}
