using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinPool.Application;

namespace WinPool.Infrastructure.Windows;

/// <summary>A closed request protocol over one persistent, globally admitted PowerShell 5.1 process.</summary>
public sealed class WindowsPowerShellHost : IAsyncDisposable
{
    public static WindowsPowerShellHost Shared { get; } = new();
    public const int RequestLimit = 4 * 1024 * 1024;
    public const int ResultLimit = 64 * 1024 * 1024;
    public const int WaitingLimit = 8;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    private readonly object gate = new();
    private readonly LinkedList<Work> queue = new();
    private readonly SemaphoreSlim available = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private Task? worker;
    private Session? session;
    private bool closed;
    private Work? current;
    private readonly PowerShellHostTemplates templates;
    public WindowsPowerShellHost() : this(PowerShellHostTemplates.Product) { }
    internal WindowsPowerShellHost(PowerShellHostTemplates templates) => this.templates = templates;

    public bool HasPossibleWrite { get { lock (gate) return session is { PossibleWrite: true } active && !active.Process.HasExited; } }
    public bool CanBeginShutdown { get { lock (gate) return !HasPossibleWrite && current is not ReservationWork { IsReleased: false }; } }
    public string? ShutdownBlockedReason { get; private set; }
    public Task<bool> PrepareShutdownAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (!CanBeginShutdown)
            { ShutdownBlockedReason = $"Current host work is {current?.GetType().Name}; possible write: {HasPossibleWrite}."; return Task.FromResult(false); }
            var result = PowerShellHostProcess.VerifyShutdownOccupancy(
                session is { } active && !active.Process.HasExited ? active.Process.Id : null, out var reason);
            ShutdownBlockedReason = reason;
            return Task.FromResult(result);
        }
    }

    public async Task<WindowsPowerShellWriteLease> ReserveWriteAsync(CancellationToken cancellationToken)
    {
        var work = new ReservationWork(this, cancellationToken);
        Enqueue(work);
        return await work.Reserved.Task.ConfigureAwait(false);
    }

    internal Task<string> QueryAsync(WindowsPowerShellQuery query, object parameters, CancellationToken token)
    {
        var work = new QueryWork(this, query, parameters, token);
        Enqueue(work);
        return work.Result.Task;
    }
    internal Task WaitForCurrentExitSignalAsync(CancellationToken token) =>
        session?.Process.WaitForExitSignalAsync(token) ?? Task.CompletedTask;

    private void Enqueue(Work work)
    {
        lock (gate)
        {
            if (closed) { work.Dispose(); throw new ObjectDisposedException(nameof(WindowsPowerShellHost)); }
            if (queue.Count >= WaitingLimit) { work.Dispose(); throw new InvalidOperationException("powershell.host.busy: the eight-request waiting queue is full."); }
            work.Node = queue.AddLast(work);
            work.RegisterCancellation();
            worker ??= Task.Run(WorkLoopAsync);
            available.Release();
        }
    }

    private void CancelQueued(Work work)
    {
        var removed = false;
        lock (gate)
        {
            if (work.Node is not { List: not null }) return;
            queue.Remove(work.Node); work.Node = null;
            work.Fail(new OperationCanceledException(work.Token));
            removed = true;
        }
        if (removed) work.Dispose();
    }

    private async Task WorkLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await available.WaitAsync(lifetime.Token).ConfigureAwait(false);
                Work work;
                lock (gate)
                {
                    if (queue.First is null) continue;
                    work = queue.First.Value; queue.RemoveFirst(); work.Node = null; current = work;
                }
                try
                {
                    work.Token.ThrowIfCancellationRequested();
                    StorageOperationTiming.Record("host.queue", work.QueuedAt, scope: work.GetType().Name);
                    var active = await EnsureReadyAsync(work.Token).ConfigureAwait(false);
                    await work.RunAsync(active).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    if (work is QueryWork && session is { PossibleWrite: false } failedRead)
                        failedRead.RecordReadFailure(error);
                    work.Fail(error);
                    // Returning the expired request does not wait for host termination.
                    // No replacement can be created until this serial cleanup proves exit.
                    if (session is { PossibleWrite: false } old && (old.Fault is not null || work.Token.IsCancellationRequested))
                        await RecycleAsync(old).ConfigureAwait(false);
                }
                finally
                {
                    // Remove the published current owner before disposing its
                    // cancellation source. Shutdown cancels only a live owner.
                    lock (gate) current = null;
                    work.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task<Session> EnsureReadyAsync(CancellationToken token)
    {
        if (session is { } existing)
        {
            if (existing.Process.HasExited)
            {
                existing.Dispose(); session = null;
            }
            else
            {
                if (existing.Fault is not null) throw new IOException($"The existing PowerShell host is unavailable; its exit is not confirmed (PID {existing.Process.Id}, possible write {existing.PossibleWrite}, recycle {existing.RecycleState}).", existing.Fault);
                return existing;
            }
        }
        token.ThrowIfCancellationRequested();
        var startup = Stopwatch.GetTimestamp();
        var process = PowerShellHostProcess.Start(PowerShellHostScript.EncodedBootstrap);
        var created = new Session(process);
        session = created;
        created.ErrorDrained = created.DrainErrorAsync();
        _ = created.ReadAsync();
        try
        {
            await WriteFrameAsync(process.Input, new
            {
                Protocol = 1, created.Incarnation,
                Inventory = templates.Inventory,
                Write = templates.Write,
                Query = templates.Query,
                ImportStorage = templates.ImportStorage
            }, token).ConfigureAwait(false);
            await created.Ready.Task.WaitAsync(token).ConfigureAwait(false);
            StorageOperationTiming.Record("host.start", startup, scope: "WindowsPowerShell5.1", hostIncarnation: created.Incarnation);
            return created;
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
            {
                try { await created.ErrorDrained.WaitAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (Exception drainError) when (drainError is TimeoutException or OperationCanceledException) { }
            }
            var failure = new IOException($"PowerShell host initialization failed (PID {process.Id}; native exit 0x{process.ExitCode:X8}; stderr: {created.ErrorTail}).", error);
            created.Fault = failure; throw failure;
        }
    }

    private async Task RecycleAsync(Session old)
    {
        if (old.PossibleWrite) return;
        old.RecycleState = "terminating exact read host";
        try
        {
            if (!await old.Process.TerminateReadOnlyAsync().ConfigureAwait(false)) { old.RecycleState = "exit unconfirmed after termination"; return; }
            old.RecycleState = "exit confirmed";
            old.Dispose();
            if (ReferenceEquals(session, old)) session = null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { old.Fault = new IOException("The read-only host could not be recycled.", error); }
    }

    internal static async Task WriteFrameAsync(Stream stream, object frame, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        if (bytes.Length > RequestLimit) throw new InvalidDataException("The PowerShell host request exceeds 4 MiB.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal async Task<string> ExecuteReservedAsync(WindowsPowerShellWriteLease lease, object payload, CancellationToken token)
    {
        var active = lease.Session;
        if (!ReferenceEquals(session, active) || active.Fault is not null || active.Process.HasExited)
            throw new IOException("The reserved PowerShell host is unavailable. The durable call must be reconciled.");
        token.ThrowIfCancellationRequested();
        // No cancellation, forced timeout, host replacement or replay beyond this point.
        active.PossibleWrite = true;
        try { return await active.ExecuteAsync("Write", payload, lease.HostCall.RequestId, CancellationToken.None).ConfigureAwait(false); }
        finally { if (active.CompletedRequestId == lease.HostCall.RequestId) lease.RecordCompletion(); }
    }

    public async ValueTask DisposeAsync()
    {
        Work[] rejected;
        lock (gate)
        {
            if (!CanBeginShutdown) throw new InvalidOperationException($"The PowerShell host may still be executing a write; shutdown is busy (work {current?.GetType().Name}, possible write {HasPossibleWrite}).");
            closed = true;
            rejected = queue.ToArray();
            foreach (var work in rejected) work.Node = null;
            queue.Clear();
            current?.Cancel();
        }
        // A cancellation callback may be waiting for gate. Never wait for its
        // registration disposal while holding that same gate.
        foreach (var work in rejected) { work.Fail(new ObjectDisposedException(nameof(WindowsPowerShellHost))); work.Dispose(); }
        if (worker is not null)
        {
            lifetime.Cancel();
            await worker.ConfigureAwait(false);
        }
        if (session is { } active)
        {
            if (active.PossibleWrite && !active.Process.HasExited) throw new InvalidOperationException("The write host cannot be disposed.");
            await active.Process.DisposeAsync().ConfigureAwait(false); session = null;
        }
        lifetime.Dispose(); available.Dispose();
    }

    internal sealed class Session(PowerShellHostProcess process) : IDisposable
    {
        public PowerShellHostProcess Process { get; } = process;
        public Guid Incarnation { get; } = Guid.NewGuid();
        public bool PossibleWrite;
        public Exception? Fault;
        public string RecycleState = "not requested";
        public Task ErrorDrained = Task.CompletedTask;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<string>? result;
        internal TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid request;
        internal Guid CompletedRequestId { get; private set; }
        private bool started;
        private long executionStarted;
        private string commandKind = "";
        private readonly object errorGate = new();
        private string errorTail = "";
        public string ErrorTail { get { lock (errorGate) return errorTail; } }

        internal void RecordReadFailure(Exception error)
        {
            if (request == Guid.Empty || request == CompletedRequestId || commandKind == "Write") return;
            try
            {
                StorageOperationTiming.Sink(JsonSerializer.Serialize(new
                {
                    phase = "host.read.failure", scope = commandKind,
                    hostIncarnation = Incarnation, correlationId = request,
                    hostProcessId = Process.Id, hostStartedAtUtc = Process.StartedAtUtc,
                    requestStarted = started, errorType = error.GetType().FullName,
                    // This is the existing bounded, separately drained stderr tail. It is diagnostic,
                    // never a completion receipt or proof that the native query stopped.
                    recentStderrTail = ErrorTail,
                    buildId = typeof(WindowsPowerShellHost).Assembly.ManifestModule.ModuleVersionId,
                    processId = Environment.ProcessId
                }));
            }
            catch (Exception diagnosticError) when (diagnosticError is not OutOfMemoryException)
            { /* Best-effort diagnostics cannot change read failure or host cleanup. */ }
        }

        public async Task<string> ExecuteAsync(string kind, object parameters, Guid requestId, CancellationToken token)
        {
            request = requestId; started = false; commandKind = kind;
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            result = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await WriteFrameAsync(Process.Input, new { Kind = kind, Incarnation, RequestId = requestId, Parameters = parameters }, token).ConfigureAwait(false);
                return await result.Task.WaitAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) { Fault = error; throw; }
        }

        public async Task ReadAsync()
        {
            try
            {
                var prefix = new byte[4];
                while (true)
                {
                    await Process.Output.ReadExactlyAsync(prefix).ConfigureAwait(false);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
                    // JSON escaping can enlarge the raw result; the decoded ResultJson is separately bounded.
                    if (length <= 0 || length > ResultLimit * 6 + 4096) throw new InvalidDataException("Invalid PowerShell host frame length.");
                    var bytes = new byte[length]; await Process.Output.ReadExactlyAsync(bytes).ConfigureAwait(false);
                    using var json = JsonDocument.Parse(bytes); var frame = json.RootElement;
                    if (frame.GetProperty("Incarnation").GetGuid() != Incarnation) throw new InvalidDataException("Foreign PowerShell host incarnation.");
                    var kind = frame.GetProperty("Kind").GetString();
                    if (kind == "ready")
                    {
                        if (Ready.Task.IsCompleted || frame.GetProperty("Protocol").GetInt32() != 1) throw new InvalidDataException("Invalid host handshake.");
                        Ready.TrySetResult(); continue;
                    }
                    if (!Ready.Task.IsCompletedSuccessfully || result is null || frame.GetProperty("RequestId").GetGuid() != request)
                        throw new InvalidDataException("Unexpected PowerShell host response.");
                    if (kind == "started")
                    {
                        if (started || result.Task.IsCompleted) throw new InvalidDataException("Duplicate host started frame.");
                        started = true; executionStarted = Stopwatch.GetTimestamp(); Started.TrySetResult(); continue;
                    }
                    if (kind != "result" || !started || result.Task.IsCompleted) throw new InvalidDataException("Invalid host result ordering.");
                    var payload = frame.GetProperty("ResultJson").GetString() ?? "";
                    var rawBytes = Encoding.UTF8.GetByteCount(payload);
                    if (rawBytes > ResultLimit) throw new InvalidDataException("The raw PowerShell result exceeds 64 MiB.");
                    StorageOperationTiming.Record("real.provider", executionStarted, scope: commandKind, captureCount: commandKind == "Write" ? 0 : 1,
                        hostIncarnation: Incarnation, correlationId: request, wireBytes: length + 4L);
                    // A final protocol result proves this runspace call ended, even if the provider reported an error.
                    var succeeded = frame.GetProperty("Success").GetBoolean();
                    CompletedRequestId = request;
                    PossibleWrite = false;
                    if (succeeded) result.TrySetResult(payload);
                    else result.TrySetException(new InvalidDataException(payload));
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Fault = error;
                // EOF requests a graceful exit after the current fixed command;
                // closing input never interrupts or kills a possible provider write.
                try { Process.Input.Dispose(); } catch (IOException) { }
                Ready.TrySetException(error); Started.TrySetException(error);
                result?.TrySetException(new IOException("The PowerShell host response was lost. " + ErrorTail, error));
            }
        }

        public async Task DrainErrorAsync()
        {
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    var count = await Process.Error.ReadAsync(buffer).ConfigureAwait(false); if (count == 0) return;
                    lock (errorGate)
                    {
                        errorTail += Encoding.UTF8.GetString(buffer, 0, count);
                        while (Encoding.UTF8.GetByteCount(errorTail) > 16 * 1024) errorTail = errorTail[Math.Min(1024, errorTail.Length)..];
                    }
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
        public void Dispose() => Process.Dispose();
    }

    private abstract class Work(WindowsPowerShellHost owner, CancellationToken cancellationToken) : IDisposable
    {
        protected WindowsPowerShellHost Owner { get; } = owner;
        protected CancellationToken CallerToken { get; } = cancellationToken;
        public long QueuedAt { get; } = Stopwatch.GetTimestamp();
        private readonly CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private CancellationTokenRegistration registration;
        public CancellationToken Token => timeout.Token;
        public LinkedListNode<Work>? Node;
        public void RegisterCancellation() { timeout.CancelAfter(TimeSpan.FromSeconds(60)); registration = Token.Register(() => { Fail(new OperationCanceledException(Token)); Owner.CancelQueued(this); }); }
        public void Cancel() => timeout.Cancel();
        public abstract Task RunAsync(Session active);
        public abstract void Fail(Exception error);
        public void Dispose() { registration.Dispose(); timeout.Dispose(); }
    }
    private sealed class QueryWork(WindowsPowerShellHost owner, WindowsPowerShellQuery query, object parameters, CancellationToken token) : Work(owner, token)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task RunAsync(Session active) => Result.TrySetResult(await active.ExecuteAsync(query.ToString(), parameters, Guid.NewGuid(), Token).ConfigureAwait(false));
        public override void Fail(Exception error) => Result.TrySetException(error is OperationCanceledException && !CallerToken.IsCancellationRequested ? new TimeoutException("The PowerShell read exceeded its remaining budget (maximum 60 seconds).", error) : error);
    }
    private sealed class ReservationWork(WindowsPowerShellHost owner, CancellationToken token) : Work(owner, token)
    {
        public TaskCompletionSource<WindowsPowerShellWriteLease> Reserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WindowsPowerShellWriteLease? lease;
        public bool IsReleased => lease?.Released.Task.IsCompleted == true;
        public override async Task RunAsync(Session active)
        {
            lease = new(Owner, active);
            if (!Reserved.TrySetResult(lease)) { await lease.DisposeAsync(); return; }
            // The owner completes the durable CallIssued boundary while this item owns the only send slot.
            await lease.Released.Task.ConfigureAwait(false);
        }
        public override void Fail(Exception error) { Reserved.TrySetException(error); if (lease is { HasSent: false }) _ = lease.DisposeAsync(); }
    }
}

public sealed class WindowsPowerShellWriteLease : IAsyncDisposable
{
    private readonly WindowsPowerShellHost owner;
    internal WindowsPowerShellHost.Session Session { get; }
    internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool HasSent { get; private set; }
    internal bool RequestCompleted { get; private set; }
    internal void RecordCompletion() => RequestCompleted = true;
    public PowerShellHostCall HostCall { get; }
    internal WindowsPowerShellWriteLease(WindowsPowerShellHost owner, WindowsPowerShellHost.Session session)
    { this.owner = owner; Session = session; HostCall = new(session.Incarnation, session.Process.Id, session.Process.StartedAtUtc, Guid.NewGuid()); }
    internal async Task<string> ExecuteAsync(object payload, CancellationToken token)
    {
        if (HasSent || Released.Task.IsCompleted) throw new InvalidOperationException("A reserved write may only be sent once.");
        token.ThrowIfCancellationRequested(); HasSent = true;
        try { return await owner.ExecuteReservedAsync(this, payload, token).ConfigureAwait(false); }
        finally { Released.TrySetResult(); }
    }
    internal Task WaitUntilStartedAsync(CancellationToken token) => Session.Started.Task.WaitAsync(token);
    public ValueTask DisposeAsync() { if (!HasSent) Released.TrySetResult(); return ValueTask.CompletedTask; }
}

internal enum WindowsPowerShellQuery { Inventory, PartitionSize, VirtualDiskSize, VirtualDiskProbe, TierSize, TierCapability, VolumeFormat, StorageJobs, BitLocker, BitLockerCandidates, PhysicalDevice, NativeMsrAttributes, MachineIdentity }

internal sealed record PowerShellHostTemplates(string Inventory, string Write, string Query, bool ImportStorage)
{
    internal static PowerShellHostTemplates Product => new(EmbeddedStorageInventoryScript.HostTemplate,
        WindowsRealStoragePowerShellScript.Source, PowerShellStorageQueryScript.Source, true);
}
