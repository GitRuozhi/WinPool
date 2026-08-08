using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WinPool.Application;
using WinPool.Domain;

namespace WinPool.Monitoring;

public interface IMonitorSessionPersistence : IAsyncDisposable
{
    Task StartAsync(
        MonitoringSession session,
        CancellationToken cancellationToken);

    bool TryWrite(MonitorSample sample);

    Task AddDroppedSamplesAsync(
        long count,
        CancellationToken cancellationToken);

    Task FlushAsync(CancellationToken cancellationToken);

    Task CompleteAsync(
        MonitoringSessionState finalState,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken);
}

public interface IMonitorSessionPersistenceFactory
{
    IMonitorSessionPersistence Create(SessionId sessionId);
}

public sealed class NullMonitorSessionPersistenceFactory
    : IMonitorSessionPersistenceFactory
{
    public IMonitorSessionPersistence Create(SessionId sessionId) =>
        new NullMonitorSessionPersistence();

    private sealed class NullMonitorSessionPersistence
        : IMonitorSessionPersistence
    {
        public Task StartAsync(
            MonitoringSession session,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public bool TryWrite(MonitorSample sample) => true;

        public Task AddDroppedSamplesAsync(
            long count,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAsync(
            MonitoringSessionState finalState,
            DateTimeOffset endedAtUtc,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class MonitoringSessionCoordinator : IMonitoringCoordinator
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromSeconds(5);

    private readonly IMonitorSource source;
    private readonly IMonitorSessionPersistenceFactory persistenceFactory;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<SessionId, ActiveSession> sessions = new();
    private readonly int latestWindowCapacity;
    private readonly int subscriberCapacity;

    public MonitoringSessionCoordinator(
        IMonitorSource source,
        IMonitorSessionPersistenceFactory? persistenceFactory = null,
        int latestWindowCapacity = 1_200,
        int subscriberCapacity = 1_200,
        TimeProvider? timeProvider = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.persistenceFactory = persistenceFactory
            ?? new NullMonitorSessionPersistenceFactory();
        if (latestWindowCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(latestWindowCapacity));
        }

        if (subscriberCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(subscriberCapacity));
        }

        this.latestWindowCapacity = latestWindowCapacity;
        this.subscriberCapacity = subscriberCapacity;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public MonitoringSession? CurrentSession =>
        sessions.Values
            .Select(value => value.Snapshot())
            .FirstOrDefault(value => value.State is
                MonitoringSessionState.Starting or MonitoringSessionState.Running);

    public IReadOnlyList<MonitorSample> CurrentSamples =>
        sessions.Values
            .Where(value => value.Snapshot().State is
                MonitoringSessionState.Starting or MonitoringSessionState.Running)
            .SelectMany(value => value.Latest.Snapshot())
            .OrderBy(sample => sample.SampledAtUtc)
            .ToArray();

    public MonitorRuntimeDiagnostics CurrentDiagnostics
    {
        get
        {
            var active = sessions.Values.FirstOrDefault(
                value => value.Snapshot().State is
                    MonitoringSessionState.Starting or MonitoringSessionState.Running);
            return active is null
                ? new(0, 0)
                : active.Diagnostics();
        }
    }

    internal int TrackedSessionCount => sessions.Count;

    public async Task<ApplicationResult<MonitoringSession>> StartAsync(
        MonitorRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlationId = CorrelationId.New();
        var validation = Validate(request, correlationId);
        if (validation is not null)
        {
            return validation;
        }

        if (sessions.Values.Any(session =>
                session.Snapshot().State is
                    MonitoringSessionState.Starting or MonitoringSessionState.Running))
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message("monitor.session.already_running"));
        }

        var createdAt = timeProvider.GetUtcNow();
        var initial = new MonitoringSession(
            request.SessionId,
            request,
            MonitoringSessionState.Starting,
            createdAt,
            null);
        var persistence = persistenceFactory.Create(request.SessionId);
        var active = new ActiveSession(
            initial,
            persistence,
            latestWindowCapacity,
            subscriberCapacity);
        if (!sessions.TryAdd(request.SessionId, active))
        {
            await persistence.DisposeAsync();
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message("monitor.session.duplicate_id"));
        }

        try
        {
            await persistence.StartAsync(initial, cancellationToken);
            active.SetState(MonitoringSessionState.Running);
            active.RunTask = RunSessionAsync(active);
            return ApplicationResult<MonitoringSession>.Succeeded(
                active.Snapshot(),
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sessions.TryRemove(request.SessionId, out _);
            await DisposePersistenceAsync(persistence);
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Cancelled,
                correlationId,
                Message("monitor.session.start_cancelled"));
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            active.SetState(MonitoringSessionState.Failed, timeProvider.GetUtcNow());
            sessions.TryRemove(request.SessionId, out _);
            await DisposePersistenceAsync(persistence);
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Failed,
                correlationId,
                Message("monitor.session.start_failed"));
        }
    }

    public async Task<ApplicationResult<MonitoringSession>> StopAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var correlationId = CorrelationId.New();
        if (!sessions.TryGetValue(sessionId, out var active))
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message("monitor.session.not_found"));
        }

        active.SetState(MonitoringSessionState.Stopping);
        active.Cancellation.Cancel();
        try
        {
            await active.RunTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Cancelled,
                correlationId,
                Message("monitor.session.stop_wait_cancelled"));
        }

        return ApplicationResult<MonitoringSession>.Succeeded(
            active.Snapshot(),
            correlationId);
    }

    public async Task<ApplicationResult<MonitoringSession>> FlushAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        var correlationId = CorrelationId.New();
        if (!sessions.TryGetValue(sessionId, out var active))
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message("monitor.session.not_found"));
        }

        try
        {
            await active.Persistence.FlushAsync(cancellationToken);
            return ApplicationResult<MonitoringSession>.Succeeded(
                active.Snapshot(),
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Cancelled,
                correlationId,
                Message("monitor.session.flush_cancelled"));
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Failed,
                correlationId,
                Message("monitor.session.flush_failed"));
        }
    }

    public async IAsyncEnumerable<MonitorSample> WatchAsync(
        SessionId sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(sessionId, out var active))
        {
            yield break;
        }

        var subscription = active.Subscribe(out var initialSamples);
        foreach (var sample in initialSamples)
        {
            yield return sample;
        }

        try
        {
            await foreach (var sample in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                yield return sample;
            }
        }
        finally
        {
            active.Unsubscribe(subscription);
        }
    }

    private async Task RunSessionAsync(ActiveSession active)
    {
        var finalState = MonitoringSessionState.Stopped;
        try
        {
            await foreach (var sample in source.SampleAsync(
                               active.Request,
                               active.Cancellation.Token))
            {
                if (!active.Accepts(sample))
                {
                    active.IncrementRejectedSourceSamples();
                    continue;
                }

                active.AddAndPublish(sample);
                if (!active.Persistence.TryWrite(sample))
                {
                    active.IncrementPersistenceDrops();
                }
            }
        }
        catch (OperationCanceledException) when (active.Cancellation.IsCancellationRequested)
        {
            finalState = MonitoringSessionState.Stopped;
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            finalState = MonitoringSessionState.Failed;
        }
        finally
        {
            var endedAt = timeProvider.GetUtcNow();
            var dropped = active.TotalDroppedSamples;
            try
            {
                if (dropped > 0)
                {
                    await active.Persistence.AddDroppedSamplesAsync(
                        dropped,
                        CancellationToken.None);
                }

                await active.Persistence.CompleteAsync(
                    finalState,
                    endedAt,
                    CancellationToken.None);
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                finalState = MonitoringSessionState.Failed;
            }

            try
            {
                await active.Persistence.DisposeAsync();
            }
            catch (Exception exception) when (IsPersistenceFailure(exception))
            {
                finalState = MonitoringSessionState.Failed;
            }

            active.SetState(finalState, endedAt);
            active.CompleteSubscribers();
            sessions.TryRemove(active.Request.SessionId, out _);
        }
    }

    private static bool IsPersistenceFailure(Exception exception) =>
        exception is IOException
            or InvalidOperationException
            or UnauthorizedAccessException
            or TimeoutException
            or System.Data.Common.DbException;

    private static async Task DisposePersistenceAsync(
        IMonitorSessionPersistence persistence)
    {
        try
        {
            await persistence.DisposeAsync();
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
        }
    }

    private static ApplicationResult<MonitoringSession>? Validate(
        MonitorRequest request,
        CorrelationId correlationId)
    {
        if (request.SessionId.Value == Guid.Empty
            || request.SystemId.Value == Guid.Empty
            || request.Targets.Count == 0
            || request.Metrics.Count == 0)
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message("monitor.request.incomplete"));
        }

        if (request.SamplingInterval < MinimumInterval
            || request.SamplingInterval > MaximumInterval)
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message("monitor.request.rate_out_of_range"));
        }

        if (request.Targets.Any(target =>
                target.ObjectId.System != request.SystemId
                || string.IsNullOrWhiteSpace(target.CounterIdentity)))
        {
            return ApplicationResult<MonitoringSession>.FromStatus(
                ApplicationStatus.Rejected,
                correlationId,
                Message("monitor.request.target_mismatch"));
        }

        return null;
    }

    private static ApplicationMessage Message(string code) =>
        new(
            code,
            code,
            string.Empty,
            ApplicationMessageSeverity.Warning,
            []);

    private sealed class ActiveSession
    {
        private readonly object gate = new();
        private readonly List<Subscriber> subscribers = [];
        private readonly int subscriberCapacity;
        private MonitoringSession session;
        private long persistenceDrops;
        private long subscriberDrops;
        private long rejectedSourceSamples;

        public ActiveSession(
            MonitoringSession session,
            IMonitorSessionPersistence persistence,
            int latestWindowCapacity,
            int subscriberCapacity)
        {
            this.session = session;
            Persistence = persistence;
            Latest = new LatestMonitorWindow(latestWindowCapacity);
            this.subscriberCapacity = subscriberCapacity;
            Targets = session.Request.Targets
                .Select(target => target.ObjectId)
                .ToHashSet();
        }

        public MonitorRequest Request => session.Request;
        public IMonitorSessionPersistence Persistence { get; }
        public LatestMonitorWindow Latest { get; }
        public HashSet<StorageObjectId> Targets { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task RunTask { get; set; } = Task.CompletedTask;

        public long TotalDroppedSamples =>
            Latest.DroppedSamples
            + Interlocked.Read(ref persistenceDrops)
            + Interlocked.Read(ref subscriberDrops)
            + Interlocked.Read(ref rejectedSourceSamples);

        public MonitorRuntimeDiagnostics Diagnostics()
        {
            lock (gate)
            {
                return new(
                    TotalDroppedSamples,
                    Latest.Snapshot().Count,
                    Latest.DroppedSamples,
                    Interlocked.Read(ref persistenceDrops),
                    Interlocked.Read(ref subscriberDrops),
                    Interlocked.Read(ref rejectedSourceSamples),
                    subscribers.Count,
                    subscribers.Sum(subscriber => subscriber.Reader.Count),
                    checked(subscribers.Count * subscriberCapacity));
            }
        }

        public MonitoringSession Snapshot()
        {
            lock (gate)
            {
                return session;
            }
        }

        public void SetState(
            MonitoringSessionState state,
            DateTimeOffset? endedAtUtc = null)
        {
            lock (gate)
            {
                session = session with { State = state, EndedAtUtc = endedAtUtc };
            }
        }

        public Subscriber Subscribe(out IReadOnlyList<MonitorSample> initialSamples)
        {
            var channel = Channel.CreateBounded<MonitorSample>(
                new BoundedChannelOptions(subscriberCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
            var subscriber = new Subscriber(channel);
            lock (gate)
            {
                initialSamples = Latest.Snapshot();
                subscribers.Add(subscriber);
            }

            return subscriber;
        }

        public void Unsubscribe(Subscriber subscriber)
        {
            lock (gate)
            {
                subscribers.Remove(subscriber);
                subscriber.Writer.TryComplete();
            }
        }

        public void AddAndPublish(MonitorSample sample)
        {
            lock (gate)
            {
                Latest.Add(sample);
                foreach (var subscriber in subscribers)
                {
                    if (!subscriber.Writer.TryWrite(sample))
                    {
                        if (subscriber.Reader.TryRead(out _))
                        {
                            Interlocked.Increment(ref subscriberDrops);
                        }

                        if (!subscriber.Writer.TryWrite(sample))
                        {
                            Interlocked.Increment(ref subscriberDrops);
                        }
                    }
                }
            }
        }

        public void CompleteSubscribers()
        {
            lock (gate)
            {
                foreach (var subscriber in subscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                subscribers.Clear();
            }
        }

        public void IncrementPersistenceDrops() =>
            Interlocked.Increment(ref persistenceDrops);

        public void IncrementRejectedSourceSamples() =>
            Interlocked.Increment(ref rejectedSourceSamples);

        public bool Accepts(MonitorSample sample) =>
            sample.SessionId == Request.SessionId
            && sample.TargetId.System == Request.SystemId
            && (Targets.Contains(sample.TargetId)
                || Request.Targets.Any(target =>
                    target.CounterIdentity == "*"
                    && target.ObjectId.Kind == sample.TargetId.Kind));

        public sealed class Subscriber(Channel<MonitorSample> channel)
        {
            public ChannelReader<MonitorSample> Reader => channel.Reader;
            public ChannelWriter<MonitorSample> Writer => channel.Writer;
        }
    }
}
