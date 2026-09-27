using WinPool.Agent;
using WinPool.Application;

namespace WinPool.Agent.Tests;

public sealed class AgentRealModeGateTests
{
    [Fact]
    public void SameOsProcessCanReconnectWithoutLosingArmedState()
    {
        var image = Path.GetFullPath("WinPool.App.exe");
        var agentSessionId = Guid.NewGuid();
        var witness = new ProcessIncarnation(
            42, image, DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000));
        var verifier = new MutableProcessVerifier(witness);
        var gate = new AgentRealModeGate(agentSessionId, image, verifier);
        var peer = new AgentVerifiedPeer(
            witness.ProcessId, witness.StartedAtUtc, image,
            ProcessInstanceId.New(), agentSessionId);

        Assert.True(gate.TryEnter(peer, "product-session", out _));
        Assert.True(gate.TryUseForNewWrite(peer with { }, "product-session", out _));
        Assert.False(gate.TryUseForNewWrite(
            peer with { ProcessInstanceId = ProcessInstanceId.New() },
            "product-session", out _));
        Assert.False(gate.TryUseForNewWrite(peer, "other-product-session", out _));
        Assert.True(gate.TryUseForNewWrite(peer, "product-session", out _));
        Assert.False(gate.TryEnter(
            peer with { ProcessInstanceId = ProcessInstanceId.New() },
            "product-session", out _));
        Assert.True(gate.TryUseForNewWrite(peer, "product-session", out _));
    }

    [Fact]
    public void ExitPidReuseAndAgentRestartRevokeNewWriteAdmission()
    {
        var image = Path.GetFullPath("WinPool.App.exe");
        var agentSessionId = Guid.NewGuid();
        var started = DateTimeOffset.FromUnixTimeMilliseconds(1_725_000_000_000);
        var verifier = new MutableProcessVerifier(new ProcessIncarnation(42, image, started));
        var gate = new AgentRealModeGate(agentSessionId, image, verifier);
        var peer = new AgentVerifiedPeer(
            42, started, image, ProcessInstanceId.New(), agentSessionId);

        Assert.True(gate.TryEnter(peer, "session", out _));
        verifier.Current = new ProcessIncarnation(42, image, started.AddSeconds(1));
        Assert.False(gate.TryUseForNewWrite(peer, "session", out _));
        Assert.False(gate.TryEnter(peer, "session", out _));
        verifier.Current = new ProcessIncarnation(42, image, started);
        Assert.False(gate.TryUseForNewWrite(peer, "session", out _));
        Assert.True(gate.TryEnter(peer, "session", out _));
        Assert.False(gate.TryUseForNewWrite(
            peer with { AgentSessionId = Guid.NewGuid() }, "session", out _));
        Assert.True(gate.TryExit(peer, "session"));
        Assert.False(gate.TryUseForNewWrite(peer, "session", out _));
    }

    private sealed class MutableProcessVerifier(ProcessIncarnation? current)
        : IProcessIncarnationVerifier
    {
        public ProcessIncarnation? Current { get; set; } = current;

        public ProcessIncarnation? TryRead(int processId) =>
            Current?.ProcessId == processId ? Current : null;

        public bool IsExpectedExecutable(int processId, string expectedExecutablePath) =>
            ProcessIncarnationMatcher.HasExpectedImage(
                TryRead(processId), processId, expectedExecutablePath);

        public bool Matches(
            AgentManagedProcess registration,
            string expectedExecutablePath) =>
            ProcessIncarnationMatcher.Matches(
                TryRead(registration.ProcessId), registration, expectedExecutablePath);
    }
}
