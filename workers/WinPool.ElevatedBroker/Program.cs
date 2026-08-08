using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using WinPool.Application;
using WinPool.Infrastructure.Windows;
using WinPool.Ipc;
using WinPool.ToolManagement;

namespace WinPool.ElevatedBroker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = BrokerStartupOptions.Parse(args);
            EnsureElevatedCurrentUser(options);
            EnsureExpectedAgentExecutable(options.AgentProcessId);

            await using var pipe = CurrentUserPipeFactory.CreateClient(options.PipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            if (CurrentUserPipeFactory.GetConnectedServerProcessId(pipe) != options.AgentProcessId)
            {
                return 11;
            }

            await WriteAsync(
                pipe,
                ElevatedBrokerMessageTypes.HandshakeRequest,
                new ElevatedBrokerHandshakeRequest(
                    IpcProtocol.CurrentVersion,
                    options.Nonce,
                    options.AgentSessionId,
                    options.UserSidHash,
                    options.AgentProcessId,
                    Environment.ProcessId,
                    options.ExpiresAtUtc,
                    DateTimeOffset.UtcNow),
                timeout.Token).ConfigureAwait(false);
            var reply = await IpcFrameCodec.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (reply.MessageType != ElevatedBrokerMessageTypes.HandshakeReply)
            {
                return 12;
            }

            var handshake = reply.Payload.Deserialize<ElevatedBrokerHandshakeReply>(JsonOptions);
            if (handshake is null ||
                handshake.AgentSessionId != options.AgentSessionId ||
                handshake.AgentProcessId != options.AgentProcessId ||
                handshake.BrokerProcessId != Environment.ProcessId)
            {
                return 13;
            }

            var envelope = await IpcFrameCodec.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (envelope.MessageType != ElevatedBrokerMessageTypes.Execute)
            {
                return 14;
            }

            var request = envelope.Payload.Deserialize<ElevatedBrokerExecutionRequest>(JsonOptions)
                ?? throw new InvalidDataException("The elevated Broker request is empty.");
            var result = await ExecuteAsync(options, request, timeout.Token).ConfigureAwait(false);
            await WriteAsync(
                pipe,
                result.Succeeded
                    ? ElevatedBrokerMessageTypes.Completed
                    : ElevatedBrokerMessageTypes.Rejected,
                result,
                timeout.Token,
                envelope.CorrelationId).ConfigureAwait(false);
            return result.Succeeded ? 0 : 20;
        }
        catch (OperationCanceledException)
        {
            return 21;
        }
        catch
        {
            return 22;
        }
    }

    private static async Task<ElevatedBrokerExecutionResult> ExecuteAsync(
        BrokerStartupOptions options,
        ElevatedBrokerExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var roots = CreateCleanupRoots(options.DataRoot);
        var temporaryFiles = new WindowsTemporaryFileCleanupPort(roots);
        var pathPolicy = new TemporaryCleanupPathPolicy(roots);
        var runner = new ProcessWindowsCommandRunner();
        var volumeResolver = CreateVolumeResolver(request);
        var volumes = new WindowsVolumeMaintenancePort(
            volumeResolver,
            new WindowsVolumeFlushApi(),
            runner);
        var ramMap = CreateRamMapPort(options.DataRoot, runner);
        var dispatcher = new ElevatedBrokerDispatcher(
            new ElevatedBrokerExecutionPorts(
                temporaryFiles,
                pathPolicy,
                ramMap,
                volumes,
                new WindowsTemporaryPowerPlanPort(runner),
                new WindowsMsiToolInstallPort(
                    options.DataRoot,
                    new ToolCatalog(),
                    runner)),
            options.Nonce,
            options.AgentSessionId,
            options.AgentProcessId,
            options.UserSidHash);
        return await dispatcher.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static IWindowsVolumeTargetBindingResolver CreateVolumeResolver(
        ElevatedBrokerExecutionRequest request)
    {
        if (request.VolumeTarget is null)
        {
            return new BoundWindowsVolumeTargetResolver([]);
        }

        return new BoundWindowsVolumeTargetResolver(
            [
                new WindowsVolumeTargetBinding(
                    request.PlanHash,
                    request.VolumeTarget.VolumeId,
                    request.VolumeTarget.DisplayIdentity,
                    request.VolumeTarget.StableIdentity)
            ]);
    }

    private static IRamMapCacheClearPort CreateRamMapPort(
        string dataRoot,
        IWindowsCommandRunner runner)
    {
        var catalog = new ToolCatalog();
        if (!catalog.TryGet(KnownToolIds.RamMap, out var descriptor))
        {
            return new MissingRamMapPort();
        }

        var discovery = new ToolPathDiscovery(
            new JsonToolPathConfiguration(Path.Combine(dataRoot, "tool-paths.json")),
            new EnvironmentToolSearchPath());
        var result = discovery.Find(descriptor);
        return result.Found && result.ExecutablePath is not null
            ? new DirectElevatedRamMapCacheClearPort(
                result.ExecutablePath,
                new WindowsRamMapExecutableIdentityProbe(),
                runner,
                isInsideElevatedBroker: true)
            : new MissingRamMapPort();
    }

    private static TemporaryCleanupRoots CreateCleanupRoots(string dataRoot)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new TemporaryCleanupRoots(
            Path.Combine(dataRoot, "temp"),
            Path.GetTempPath(),
            Path.Combine(windows, "Temp"),
            windows,
            []);
    }

    private static void EnsureElevatedCurrentUser(BrokerStartupOptions options)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("The Broker user has no SID.");
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator) ||
            !StringComparer.Ordinal.Equals(IpcIdentity.HashUserSid(sid), options.UserSidHash))
        {
            throw new UnauthorizedAccessException("The Broker identity is invalid.");
        }
    }

    private static void EnsureExpectedAgentExecutable(int agentProcessId)
    {
        using var process = Process.GetProcessById(agentProcessId);
        var actual = process.MainModule?.FileName;
        var brokerDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var expected = Path.GetFullPath(
            Path.Combine(brokerDirectory, "..", "WinPool.Agent.exe"));
        if (string.IsNullOrWhiteSpace(actual) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(actual), expected))
        {
            throw new UnauthorizedAccessException("The Broker server is not the packaged WinPool Agent.");
        }
    }

    private static ValueTask WriteAsync<T>(
        Stream stream,
        string messageType,
        T payload,
        CancellationToken cancellationToken,
        Guid? correlationId = null)
    {
        var element = JsonSerializer.SerializeToElement(payload, JsonOptions);
        return IpcFrameCodec.WriteAsync(
            stream,
            new IpcEnvelope(
                IpcProtocol.CurrentVersion,
                Guid.NewGuid(),
                correlationId ?? Guid.NewGuid(),
                messageType,
                DateTimeOffset.UtcNow,
                element),
            cancellationToken);
    }

    private sealed class MissingRamMapPort : IRamMapCacheClearPort
    {
        public bool SupportsElevatedBroker => true;

        public Task<RamMapToolIdentity?> DetectIdentityAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<RamMapToolIdentity?>(null);

        public Task<RamMapCacheClearEvidence> ClearAsync(
            RamMapCacheClearRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("RAMMap is not installed or configured.");
    }
}

internal sealed record BrokerStartupOptions(
    string PipeName,
    Guid Nonce,
    Guid AgentSessionId,
    int AgentProcessId,
    string UserSidHash,
    DateTimeOffset ExpiresAtUtc,
    string DataRoot)
{
    public static BrokerStartupOptions Parse(IReadOnlyList<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count ||
                !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("The Broker startup arguments are invalid.");
            }

            if (!values.TryAdd(arguments[index], arguments[index + 1]))
            {
                throw new ArgumentException("A Broker startup argument was repeated.");
            }
        }

        var pipeName = Required(values, "--pipe");
        if (!pipeName.StartsWith("WinPool.Broker.", StringComparison.Ordinal))
        {
            throw new ArgumentException("The Broker pipe name is invalid.");
        }

        var nonce = Guid.ParseExact(Required(values, "--nonce"), "N");
        var session = Guid.ParseExact(Required(values, "--agent-session"), "N");
        var agentProcessId = int.Parse(Required(values, "--agent-pid"));
        var userHash = Required(values, "--user-hash");
        var expires = DateTimeOffset.Parse(
            Required(values, "--expires-utc"),
            null,
            System.Globalization.DateTimeStyles.RoundtripKind);
        var dataRoot = Path.GetFullPath(Required(values, "--data-root"));
        if (nonce == Guid.Empty ||
            session == Guid.Empty ||
            agentProcessId <= 0 ||
            userHash.Length != 64 ||
            userHash.Any(character => !Uri.IsHexDigit(character)) ||
            expires <= DateTimeOffset.UtcNow ||
            expires - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentException("The Broker startup identity is invalid.");
        }

        return new(
            pipeName,
            nonce,
            session,
            agentProcessId,
            userHash,
            expires,
            dataRoot);
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing Broker startup argument: {key}.");
}
