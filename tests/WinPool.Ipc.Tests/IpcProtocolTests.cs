using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Ipc;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Ipc.Tests;

public sealed class IpcProtocolTests
{
    [Fact]
    public async Task ScopedInventoryFramePreservesExactIdentityClosureAndGeneration()
    {
        var system = SystemId.New();
        var target = new StorageObjectId(system, StorageObjectKind.PhysicalDisk, "physical:unique-id");
        var scope = new StorageInventoryScope(system, OperationId.New(), "verified-step", [target],
            [target.ProviderKey, "partition:removed"], [new(target, "MSFT_PhysicalDisk", "UniqueId", "50014E2E14568901")], 42);
        var request = new CaptureAgentManageScopedInventoryRequest(scope, CorrelationId.New());
        var envelope = new IpcEnvelope(IpcProtocol.CurrentVersion, Guid.NewGuid(), Guid.NewGuid(),
            AgentControlMessageTypes.CaptureManageScopedInventory, DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(request));
        await using var stream = new MemoryStream();
        await IpcFrameCodec.WriteAsync(stream, envelope);
        stream.Position = 0;
        var recovered = (await IpcFrameCodec.ReadAsync(stream)).Payload.Deserialize<CaptureAgentManageScopedInventoryRequest>()!;
        recovered.Scope.Validate();
        Assert.Equal(scope.SystemId, recovered.Scope.SystemId);
        Assert.Equal(scope.OperationId, recovered.Scope.OperationId);
        Assert.Equal(scope.StepId, recovered.Scope.StepId);
        Assert.Equal(42, recovered.Scope.Generation);
        Assert.Equal(scope.BeforeObjectIds, recovered.Scope.BeforeObjectIds);
        Assert.Equal(scope.BeforeLocators, recovered.Scope.BeforeLocators);
        Assert.Equal(request.CorrelationId, recovered.CorrelationId);
    }

    [Fact]
    public void CreationRangeResponsePreservesExactBytesInsteadOfRoundedDisplayUnits()
    {
        var target = new StorageObjectId(SystemId.New(), StorageObjectKind.StorageTier, "tier:exact");
        var range = new RealVirtualDiskCreationRange(target, 1073741824, 4000317439744, 268435456,
            33554432, [], "target-fingerprint", DateTimeOffset.UtcNow);
        var recovered = JsonSerializer.Deserialize<AgentRealVirtualDiskCreationRangeResponse>(
            JsonSerializer.Serialize(new AgentRealVirtualDiskCreationRangeResponse(range)))!;
        Assert.Equal(range, recovered.Range with { EnumeratedSizes = range.EnumeratedSizes });
        Assert.Equal(range.ResolveMaximum(), recovered.Range.ResolveMaximum());
    }

    [Fact]
    public async Task FrameCodecRoundTripsOneEnvelope()
    {
        using var document = JsonDocument.Parse("""{"state":"running"}""");
        var expected = new IpcEnvelope(
            IpcProtocol.CurrentVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "monitor.snapshot",
            DateTimeOffset.UtcNow,
            document.RootElement.Clone());
        await using var stream = new MemoryStream();

        await IpcFrameCodec.WriteAsync(stream, expected);
        stream.Position = 0;
        var actual = await IpcFrameCodec.ReadAsync(stream);

        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal("running", actual.Payload.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IpcProtocol.MaximumFrameBytes + 1)]
    public async Task FrameCodecRejectsInvalidLength(int length)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await IpcFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public void HandshakeBindsVersionNonceUserProcessAndTime()
    {
        var now = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid();
        var userHash = IpcIdentity.HashUserSid("S-1-5-21-1234");
        var valid = new AgentHandshakeRequest(
            IpcProtocol.CurrentVersion,
            nonce,
            userHash,
            Environment.ProcessId,
            Guid.NewGuid(),
            now);

        Assert.True(AgentHandshakeValidator.Validate(valid, nonce, userHash, now).IsAccepted);
        Assert.Equal(
            HandshakeRejection.ProtocolMismatch,
            AgentHandshakeValidator.Validate(
                valid with { ProtocolVersion = IpcProtocol.CurrentVersion - 1 },
                nonce,
                userHash,
                now).Rejection);
        Assert.Equal(
            HandshakeRejection.NonceMismatch,
            AgentHandshakeValidator.Validate(valid, Guid.NewGuid(), userHash, now).Rejection);
        Assert.Equal(
            HandshakeRejection.UserMismatch,
            AgentHandshakeValidator.Validate(
                valid,
                nonce,
                IpcIdentity.HashUserSid("S-1-5-21-OTHER"),
                now).Rejection);
        Assert.Equal(
            HandshakeRejection.Expired,
            AgentHandshakeValidator.Validate(
                valid with { SentAtUtc = now.AddMinutes(-1) },
                nonce,
                userHash,
                now).Rejection);
        Assert.Equal(
            HandshakeRejection.InvalidProcess,
            AgentHandshakeValidator.Validate(
                valid with { ProcessInstanceId = Guid.Empty },
                nonce,
                userHash,
                now).Rejection);
    }

    [Fact]
    public void PipeNamesExposeOnlySidHashPrefixAndUnpredictableNonce()
    {
        const string sid = "S-1-5-21-THIS-MUST-NOT-APPEAR";
        var hash = IpcIdentity.HashUserSid(sid);
        var first = IpcIdentity.CreateAgentControlPipeName(hash, Guid.NewGuid());
        var second = IpcIdentity.CreateAgentControlPipeName(hash, Guid.NewGuid());

        Assert.NotEqual(first, second);
        Assert.DoesNotContain(sid, first, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("WinPool.Agent.Control.", first, StringComparison.Ordinal);
    }

    [Fact]
    public void EventHandshakeBindsConnectionNonceActualPidAndShortExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var endpoint = new AgentEventPipeEndpoint(
            "WinPool.Agent.Events.test",
            Guid.NewGuid(),
            Guid.NewGuid(),
            now.AddSeconds(30));
        var request = new AgentEventHandshakeRequest(
            IpcProtocol.CurrentVersion,
            endpoint.ConnectionId,
            endpoint.Nonce,
            123,
            now);

        Assert.True(
            AgentEventHandshakeValidator.Validate(
                request,
                endpoint,
                123,
                123,
                now).IsAccepted);
        Assert.Equal(
            HandshakeRejection.ProtocolMismatch,
            AgentEventHandshakeValidator.Validate(
                request with { ProtocolVersion = IpcProtocol.CurrentVersion - 1 },
                endpoint,
                123,
                123,
                now).Rejection);
        Assert.Equal(
            HandshakeRejection.InvalidProcess,
            AgentEventHandshakeValidator.Validate(
                request,
                endpoint,
                123,
                456,
                now).Rejection);
        Assert.Equal(
            HandshakeRejection.NonceMismatch,
            AgentEventHandshakeValidator.Validate(
                request with { Nonce = Guid.NewGuid() },
                endpoint,
                123,
                123,
                now).Rejection);
        Assert.Equal(
            HandshakeRejection.Expired,
            AgentEventHandshakeValidator.Validate(
                request,
                endpoint with { ExpiresAtUtc = now },
                123,
                123,
                now).Rejection);
    }

    [Fact]
    public async Task CurrentUserOnlyPipeCarriesFramedEnvelope()
    {
        var hash = IpcIdentity.HashUserSid($"test-user-{Guid.NewGuid():N}");
        var pipeName = IpcIdentity.CreateAgentControlPipeName(hash, Guid.NewGuid());
        await using var server = CurrentUserPipeFactory.CreateServer(pipeName);
        await using var client = CurrentUserPipeFactory.CreateClient(pipeName);
        var accept = server.WaitForConnectionAsync();
        await client.ConnectAsync(5_000);
        await accept;
        Assert.Equal(
            Environment.ProcessId,
            CurrentUserPipeFactory.GetConnectedClientProcessId(server));

        using var document = JsonDocument.Parse("""{"request":"snapshot"}""");
        var expected = new IpcEnvelope(
            IpcProtocol.CurrentVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "agent.request",
            DateTimeOffset.UtcNow,
            document.RootElement.Clone());

        await IpcFrameCodec.WriteAsync(client, expected);
        var actual = await IpcFrameCodec.ReadAsync(server);

        Assert.Equal(expected.MessageId, actual.MessageId);
        Assert.Equal("snapshot", actual.Payload.GetProperty("request").GetString());
    }

    [Fact]
    public void PipeAclContainsOnlyExplicitCurrentUserAllowRules()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = Assert.IsType<SecurityIdentifier>(identity.User);
        var hash = IpcIdentity.HashUserSid(user.Value);
        var pipeName = IpcIdentity.CreateAgentControlPipeName(hash, Guid.NewGuid());
        using var server = CurrentUserPipeFactory.CreateServer(pipeName);

        var security = server.GetAccessControl();
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(user, rule.IdentityReference));
        Assert.Contains(
            rules,
            rule => rule.AccessControlType == AccessControlType.Allow
                    && rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        Assert.DoesNotContain(rules, rule => rule.IsInherited);
    }
}
