using Microsoft.Data.Sqlite;
using WinPool.Application;
using WinPool.Domain;
using WinPool.Infrastructure.Sqlite;
using WinPool.Monitoring;

namespace WinPool.Persistence.Tests;

public sealed class RotatingMonitorSessionPersistenceTests
{
    [Fact]
    public async Task ContinuousProducerPreservesExactMultiSetAcrossMultipleRotations()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 16_384,
            WriterChannelCapacity: 16_384,
            WriterMaximumBatchSize: 32,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(5),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "continuous-rotation-agent",
            archive,
            options,
            () => Interlocked.Exchange(ref forceRotationBytes, 0));
        await factory.InitializeAsync();

        var session = CreateTwoTargetSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        var accepted = new List<MonitorSample>();
        var producedFrames = 0;
        var producer = Task.Run(async () =>
        {
            for (var index = 0; index < 1_600; index++)
            {
                var timestamp = session.CreatedAtUtc.AddMilliseconds(index);
                var first = new MonitorSample(
                    session.SessionId,
                    session.Request.Targets[0].ObjectId,
                    timestamp,
                    [new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, index + 1)]);
                var second = new MonitorSample(
                    session.SessionId,
                    session.Request.Targets[1].ObjectId,
                    timestamp,
                    [
                        new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, 0),
                        new MonitorMetricValue(MonitorMetricKind.ReadBytesPerSecond, 0)
                    ]);
                Assert.True(persistence.TryWrite(first));
                accepted.Add(first);
                Assert.True(persistence.TryWrite(second));
                accepted.Add(second);
                Interlocked.Increment(ref producedFrames);
                await Task.Delay(TimeSpan.FromMilliseconds(2));
            }
        });

        for (var expectedGeneration = 1; expectedGeneration <= 3; expectedGeneration++)
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref producedFrames) >= expectedGeneration * 300,
                TimeSpan.FromSeconds(15));
            Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
            await WaitUntilAsync(
                () => archive.Snapshot().Count >= expectedGeneration,
                TimeSpan.FromSeconds(20));
        }

        await producer;
        await persistence.FlushAsync(CancellationToken.None);
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await archive.WaitForIdleAsync(TimeSpan.FromSeconds(45));

        var expected = ToSampleMultiSet(accepted);
        var actual = await ReadSampleMultiSetAsync(Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName));
        var archived = archive.Snapshot()
            .Where(record => record.Stage == MonitoringArchiveStage.SourceReleased)
            .ToArray();
        Assert.True(archived.Length >= 3, "The producer must span at least three sealed databases.");
        foreach (var record in archived)
        {
            var extractionDirectory = Path.Combine(fixture.DataRoot, $"continuous-{record.GenerationId}");
            Directory.CreateDirectory(extractionDirectory);
            var result = await new ControlledProcessRunner().RunAsync(
                new ControlledProcessInvocation(
                    fixture.SevenZipPath,
                    ["x", "-y", "-bso0", "-bsp0", $"-o{extractionDirectory}", record.ArchivePath]),
                CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            Merge(
                actual,
                await ReadSampleMultiSetAsync(Path.Combine(
                    extractionDirectory,
                    Path.GetFileName(record.RawDatabasePath))));
        }

        AssertMultiSetEqual(expected, actual);
    }

    [Fact]
    public async Task RotationKeepsEveryAcceptedSampleAcrossSealedArchiveAndNewActiveDatabase()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 8_192,
            WriterChannelCapacity: 8_192,
            WriterMaximumBatchSize: 256,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(10),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "rotation-test-agent",
            archive,
            options,
            () => Interlocked.Exchange(ref forceRotationBytes, 0));
        await factory.InitializeAsync();

        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        const int sampleCount = 6_000;
        for (var index = 0; index < sampleCount; index++)
        {
            Assert.True(persistence.TryWrite(CreateSample(session, index)));
        }
        await persistence.FlushAsync(CancellationToken.None);
        Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
        await WaitUntilAsync(
            () => archive.Snapshot().Any(record => record.Stage != MonitoringArchiveStage.PendingSeal),
            TimeSpan.FromSeconds(20));
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await archive.WaitForIdleAsync(TimeSpan.FromSeconds(30));

        var archived = archive.Snapshot()
            .Where(record => record.Stage == MonitoringArchiveStage.SourceReleased)
            .ToArray();
        Assert.NotEmpty(archived);
        var persistedCount = await CountSamplesAsync(
            Path.Combine(fixture.DataRoot, RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName));
        foreach (var record in archived)
        {
            var extractionDirectory = Path.Combine(fixture.DataRoot, $"extract-{record.GenerationId}");
            Directory.CreateDirectory(extractionDirectory);
            var result = await new ControlledProcessRunner().RunAsync(
                new ControlledProcessInvocation(
                    fixture.SevenZipPath,
                    ["x", "-y", "-bso0", "-bsp0", $"-o{extractionDirectory}", record.ArchivePath]),
                CancellationToken.None);
            Assert.Equal(0, result.ExitCode);
            persistedCount += await CountSamplesAsync(
                Path.Combine(extractionDirectory, Path.GetFileName(record.RawDatabasePath)));
        }

        Assert.Equal(sampleCount, persistedCount);
    }

    [Fact]
    public async Task RotationManifestIncludesEverySessionInTheSealedFixedDatabase()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 512,
            WriterChannelCapacity: 512,
            WriterMaximumBatchSize: 16,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(5),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "multi-session-rotation-agent",
            archive,
            options,
            () => Interlocked.Exchange(ref forceRotationBytes, 0));
        await factory.InitializeAsync();

        var firstSession = CreateSession();
        await using (var firstPersistence = factory.Create(firstSession.SessionId))
        {
            await firstPersistence.StartAsync(firstSession, CancellationToken.None);
            Assert.True(firstPersistence.TryWrite(CreateSample(firstSession, 1)));
            await firstPersistence.CompleteAsync(
                MonitoringSessionState.Stopped,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        var rotationSession = CreateSession();
        await using (var rotationPersistence = factory.Create(rotationSession.SessionId))
        {
            await rotationPersistence.StartAsync(rotationSession, CancellationToken.None);
            Assert.True(rotationPersistence.TryWrite(CreateSample(rotationSession, 2)));
            await rotationPersistence.FlushAsync(CancellationToken.None);
            Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
            await WaitUntilAsync(
                () => archive.Snapshot().Any(record =>
                    record.Stage == MonitoringArchiveStage.SourceReleased),
                TimeSpan.FromSeconds(30));
            await rotationPersistence.CompleteAsync(
                MonitoringSessionState.Stopped,
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        }

        var sealedRecord = Assert.Single(
            archive.Snapshot(),
            record => record.Stage == MonitoringArchiveStage.SourceReleased);
        Assert.Equal(
            new[]
            {
                firstSession.SessionId.Value.ToString("N"),
                rotationSession.SessionId.Value.ToString("N")
            }.OrderBy(value => value, StringComparer.Ordinal),
            sealedRecord.SessionIds.OrderBy(value => value, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CsvReadLeaseDefersRotationUntilReaderReleases()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var rotationRequested = 0;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 256,
            WriterChannelCapacity: 256,
            WriterMaximumBatchSize: 16,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(10),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(fixture.DataRoot, fixture.DataRoot, () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "reader-lease-agent",
            archive,
            options,
            () =>
            {
                var requested = Interlocked.Exchange(ref forceRotationBytes, 0);
                if (requested > 0)
                {
                    Interlocked.Increment(ref rotationRequested);
                }

                return requested;
            });
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        await using var readerLease = await factory.AcquireReadLeaseAsync(CancellationToken.None);
        for (var index = 0; index < 128; index++)
        {
            Assert.True(persistence.TryWrite(CreateSample(session, index)));
        }

        await persistence.FlushAsync(CancellationToken.None);
        Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
        await WaitUntilAsync(
            () => Volatile.Read(ref rotationRequested) > 0,
            TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.Empty(archive.Snapshot());
        Assert.True(File.Exists(Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName)));

        await readerLease.DisposeAsync();
        await WaitUntilAsync(
            () => archive.Snapshot().Any(record => record.Stage != MonitoringArchiveStage.PendingSeal),
            TimeSpan.FromSeconds(20));
        await persistence.CompleteAsync(MonitoringSessionState.Stopped, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task StopWhileCsvLeaseBlocksRotationKeepsTheOldWriterAndAllAcceptedSamples()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var rotationRequested = 0;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 256,
            WriterChannelCapacity: 256,
            WriterMaximumBatchSize: 16,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(5),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "stop-reader-lease-agent",
            archive,
            options,
            () =>
            {
                var requested = Interlocked.Exchange(ref forceRotationBytes, 0);
                if (requested > 0)
                {
                    Interlocked.Increment(ref rotationRequested);
                }

                return requested;
            });
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        var readerLease = await factory.AcquireReadLeaseAsync(CancellationToken.None);
        try
        {
            var accepted = new List<MonitorSample>();
            for (var index = 0; index < 128; index++)
            {
                var sample = CreateSample(session, index);
                Assert.True(persistence.TryWrite(sample));
                accepted.Add(sample);
            }

            await persistence.FlushAsync(CancellationToken.None);
            Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
            await WaitUntilAsync(
                () => Volatile.Read(ref rotationRequested) > 0
                    && Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                        .GetDiagnostics().RotationInProgress,
                TimeSpan.FromSeconds(10));

            // These samples are accepted only by the rotation buffer while
            // the CSV lease prevents the exclusive switch. They prove Stop
            // cancels that wait without abandoning accepted buffered values.
            for (var index = 128; index < 256; index++)
            {
                var sample = CreateSample(session, index);
                Assert.True(persistence.TryWrite(sample));
                accepted.Add(sample);
            }

            // Stop cancels the wait for the exclusive CSV lease. It must leave
            // the existing writer/lease alone until it has completed and
            // flushed that same fixed database.
            await persistence.CompleteAsync(
                MonitoringSessionState.Stopped,
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Empty(archive.Snapshot());
            var diagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                .GetDiagnostics();
            Assert.False(diagnostics.IsPaused);
            Assert.False(diagnostics.RotationInProgress);
            Assert.Equal(0, diagnostics.ConfirmedLostSamples);
            AssertMultiSetEqual(
                ToSampleMultiSet(accepted),
                await ReadSampleMultiSetAsync(Path.Combine(
                    fixture.DataRoot,
                    RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName)));
        }
        finally
        {
            await readerLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepeatedOrdinaryWriterFailuresPauseBufferAndRecoverWithoutDoubleCounting()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: long.MaxValue,
            RotationBufferCapacity: 256,
            WriterChannelCapacity: 256,
            WriterMaximumBatchSize: 1,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(10),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "writer-recovery-agent",
            archive,
            options);
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        var activePath = Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName);

        await using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = activePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await blocker.OpenAsync();
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            await begin.ExecuteNonQueryAsync();
        }

        var lostBeforeRecovery = CreateSample(session, 1);
        Assert.True(persistence.TryWrite(lostBeforeRecovery));
        await WaitUntilAsync(
            () =>
            {
                var diagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                    .GetDiagnostics();
                return diagnostics.IsPaused
                    && diagnostics.Failure is not null
                    && diagnostics.ConfirmedLostSamples == 1;
            },
            TimeSpan.FromSeconds(15));

        var buffered = new[] { CreateSample(session, 2), CreateSample(session, 3) };
        Assert.All(buffered, sample => Assert.True(persistence.TryWrite(sample)));
        var paused = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
            .GetDiagnostics();
        Assert.True(paused.IsPaused);
        Assert.Equal(buffered.Length, paused.RotationBufferedSamples);
        Assert.Equal(1, paused.ConfirmedLostSamples);

        await using (var rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        await WaitUntilAsync(
            () =>
            {
                var diagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                    .GetDiagnostics();
                return !diagnostics.IsPaused
                    && !diagnostics.RotationInProgress
                    && diagnostics.RotationBufferedSamples == 0
                    && diagnostics.Failure is null
                    && diagnostics.ConfirmedLostSamples == 1;
            },
            TimeSpan.FromSeconds(15));
        await persistence.FlushAsync(CancellationToken.None);

        await using (var beginAgain = blocker.CreateCommand())
        {
            beginAgain.CommandText = "BEGIN EXCLUSIVE;";
            await beginAgain.ExecuteNonQueryAsync();
        }

        var lostDuringSecondFault = CreateSample(session, 4);
        Assert.True(persistence.TryWrite(lostDuringSecondFault));
        await WaitUntilAsync(
            () =>
            {
                var diagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                    .GetDiagnostics();
                return diagnostics.IsPaused
                    && diagnostics.Failure is not null
                    && diagnostics.ConfirmedLostSamples == 2;
            },
            TimeSpan.FromSeconds(15));
        var bufferedAfterSecondFault = CreateSample(session, 5);
        Assert.True(persistence.TryWrite(bufferedAfterSecondFault));
        Assert.Equal(
            1,
            Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                .GetDiagnostics().RotationBufferedSamples);

        await using (var rollbackAgain = blocker.CreateCommand())
        {
            rollbackAgain.CommandText = "ROLLBACK;";
            await rollbackAgain.ExecuteNonQueryAsync();
        }
        await WaitUntilAsync(
            () =>
            {
                var diagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                    .GetDiagnostics();
                return !diagnostics.IsPaused
                    && !diagnostics.RotationInProgress
                    && diagnostics.RotationBufferedSamples == 0
                    && diagnostics.Failure is null
                    && diagnostics.ConfirmedLostSamples == 2;
            },
            TimeSpan.FromSeconds(15));
        await persistence.FlushAsync(CancellationToken.None);
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Empty(archive.Snapshot());
        AssertMultiSetEqual(
            ToSampleMultiSet(buffered.Append(bufferedAfterSecondFault)),
            await ReadSampleMultiSetAsync(activePath));
        Assert.Equal(2, await ReadDroppedSamplesAsync(activePath, session.SessionId));
    }

    [Fact]
    public async Task InsufficientReplacementSpaceLeavesTheLiveFixedWriterUntouched()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 256,
            WriterChannelCapacity: 256,
            WriterMaximumBatchSize: 16,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(5),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "insufficient-space-agent",
            archive,
            options,
            () => Interlocked.Exchange(ref forceRotationBytes, 0),
            availableDataRootBytesProvider: () => 0);
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        var first = CreateSample(session, 1);
        Assert.True(persistence.TryWrite(first));
        await persistence.FlushAsync(CancellationToken.None);

        Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
        await WaitUntilAsync(
            () =>
            {
                var diagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                    .GetDiagnostics();
                return diagnostics.Failure?.Contains("insufficient free space", StringComparison.OrdinalIgnoreCase) == true
                    && !diagnostics.IsPaused
                    && !diagnostics.RotationInProgress;
            },
            TimeSpan.FromSeconds(10));

        Assert.Empty(archive.Snapshot());
        var second = CreateSample(session, 2);
        Assert.True(persistence.TryWrite(second));
        await persistence.FlushAsync(CancellationToken.None);
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        AssertMultiSetEqual(
            ToSampleMultiSet([first, second]),
            await ReadSampleMultiSetAsync(Path.Combine(
                fixture.DataRoot,
                RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName)));
    }

    [Fact]
    public async Task PausedRotationRetriesSealedSourceRecoveryAfterReplacementInitializationAndMoveFailures()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var initializationCount = 0;
        var replacementInitializationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReplacementInitializationFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 256,
            WriterChannelCapacity: 256,
            WriterMaximumBatchSize: 16,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(5),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "paused-recovery-agent",
            archive,
            options,
            () => Interlocked.Exchange(ref forceRotationBytes, 0),
            initializeMonitoringStoreAsync: async (store, token) =>
            {
                if (Interlocked.Increment(ref initializationCount) == 2)
                {
                    replacementInitializationStarted.TrySetResult();
                    await allowReplacementInitializationFailure.Task.WaitAsync(token);
                    throw new IOException("injected replacement database initialization failure");
                }

                await store.InitializeAsync(token);
            });
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        var beforeRotation = CreateSample(session, 1);
        Assert.True(persistence.TryWrite(beforeRotation));
        await persistence.FlushAsync(CancellationToken.None);

        Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
        await replacementInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = Assert.Single(archive.Snapshot());
        Assert.Equal(MonitoringArchiveStage.PendingSeal, pending.Stage);
        Assert.True(File.Exists(pending.RawDatabasePath));
        await using var rawMoveBlocker = new FileStream(
            pending.RawDatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        allowReplacementInitializationFailure.TrySetResult();

        var activePath = Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName);
        await WaitUntilAsync(
            () => Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                .GetDiagnostics().IsPaused,
            TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(activePath));
        Assert.True(File.Exists(pending.RawDatabasePath));

        var buffered = new[] { CreateSample(session, 2), CreateSample(session, 3) };
        Assert.All(buffered, sample => Assert.True(persistence.TryWrite(sample)));
        Assert.Equal(
            buffered.Length,
            Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                .GetDiagnostics().RotationBufferedSamples);

        await rawMoveBlocker.DisposeAsync();
        await WaitUntilAsync(
            () =>
            {
                var diagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                    .GetDiagnostics();
                return !diagnostics.IsPaused
                    && diagnostics.RotationBufferedSamples == 0
                    && archive.Snapshot().Single().Stage == MonitoringArchiveStage.RotationAborted;
            },
            TimeSpan.FromSeconds(15));
        Assert.True(File.Exists(activePath));
        Assert.False(File.Exists(pending.RawDatabasePath));

        await persistence.FlushAsync(CancellationToken.None);
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        AssertMultiSetEqual(
            ToSampleMultiSet(buffered.Prepend(beforeRotation)),
            await ReadSampleMultiSetAsync(activePath));
    }

    [Fact]
    public async Task PausedRotationBufferFullStopReportsExactKnownLossWithoutDoubleCounting()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var initializationCount = 0;
        var replacementInitializationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowReplacementInitializationFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 2,
            WriterChannelCapacity: 2,
            WriterMaximumBatchSize: 1,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(5),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "paused-stop-loss-agent",
            archive,
            options,
            () => Interlocked.Exchange(ref forceRotationBytes, 0),
            initializeMonitoringStoreAsync: async (store, token) =>
            {
                if (Interlocked.Increment(ref initializationCount) == 2)
                {
                    replacementInitializationStarted.TrySetResult();
                    await allowReplacementInitializationFailure.Task.WaitAsync(token);
                    throw new IOException("injected replacement initialization failure for paused-stop test");
                }

                await store.InitializeAsync(token);
            });
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        Assert.True(persistence.TryWrite(CreateSample(session, 1)));
        await persistence.FlushAsync(CancellationToken.None);

        Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
        await replacementInitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pending = Assert.Single(archive.Snapshot());
        await using var rawMoveBlocker = new FileStream(
            pending.RawDatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        allowReplacementInitializationFailure.TrySetResult();
        await WaitUntilAsync(
            () => Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                .GetDiagnostics().IsPaused,
            TimeSpan.FromSeconds(10));

        Assert.True(persistence.TryWrite(CreateSample(session, 2)));
        Assert.True(persistence.TryWrite(CreateSample(session, 3)));
        // This third post-pause sample was never accepted. The monitoring
        // coordinator owns its separate source-rejection total; it must not
        // be folded into the exact count for the two accepted buffer entries.
        Assert.False(persistence.TryWrite(CreateSample(session, 4)));
        var full = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
            .GetDiagnostics();
        Assert.Equal(2, full.RotationBufferedSamples);
        Assert.Equal(0, full.ConfirmedLostSamples);

        await Assert.ThrowsAsync<IOException>(() => persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        var stopped = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
            .GetDiagnostics();
        Assert.True(stopped.IsPaused);
        Assert.Equal(0, stopped.RotationBufferedSamples);
        Assert.Equal(2, stopped.ConfirmedLostSamples);
        Assert.True(File.Exists(pending.RawDatabasePath));
        Assert.False(File.Exists(Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName)));

        // Complete can be observed again by shutdown paths; no accepted
        // sample may be added twice merely because the first stop reported a
        // necessary persistence failure.
        await Assert.ThrowsAsync<IOException>(() => persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None));
        Assert.Equal(
            2,
            Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                .GetDiagnostics().ConfirmedLostSamples);
    }

    [Fact]
    public async Task BusyCheckpointDoesNotMoveFixedActiveDatabaseAndRestoresWriting()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        long forceRotationBytes = 0;
        var rotationRequested = 0;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: 1,
            RotationBufferCapacity: 256,
            WriterChannelCapacity: 256,
            WriterMaximumBatchSize: 8,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            ThresholdPollInterval: TimeSpan.FromMilliseconds(5),
            FailureRetryDelay: TimeSpan.FromMilliseconds(50));
        var archive = new MonitoringArchiveCoordinator(fixture.DataRoot, fixture.DataRoot, () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "checkpoint-failure-agent",
            archive,
            options,
            () =>
            {
                var requested = Interlocked.Exchange(ref forceRotationBytes, 0);
                if (requested > 0)
                {
                    Interlocked.Increment(ref rotationRequested);
                }

                return requested;
            });
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        Assert.True(persistence.TryWrite(CreateSample(session, 1)));
        await persistence.FlushAsync(CancellationToken.None);

        var activeStore = Assert.IsType<MonitoringSqliteStore>(factory.GetCurrentDatabase());
        await using var blocker = await activeStore.OpenConnectionAsync();
        await using (var begin = blocker.CreateCommand())
        {
            begin.CommandText = "BEGIN;";
            await begin.ExecuteNonQueryAsync();
        }

        await using (var read = blocker.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(*) FROM monitor_samples;";
            Assert.Equal(1L, Convert.ToInt64(await read.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        Interlocked.Exchange(ref forceRotationBytes, options.RotationThresholdBytes);
        await WaitUntilAsync(
            () => Volatile.Read(ref rotationRequested) > 0,
            TimeSpan.FromSeconds(10));
        // The store's busy timeout is five seconds. Let the failed TRUNCATE
        // checkpoint return and the original fixed database be resumed.
        await Task.Delay(TimeSpan.FromSeconds(6));

        Assert.Empty(archive.Snapshot());
        var activePath = Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName);
        Assert.True(File.Exists(activePath));
        var sealedRoot = Path.Combine(
            fixture.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.SealedDirectoryName);
        Assert.False(Directory.Exists(sealedRoot)
            && Directory.EnumerateFiles(sealedRoot, "*.db", SearchOption.TopDirectoryOnly).Any());
        var persistenceDiagnostics = Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
            .GetDiagnostics();
        Assert.Contains("locked", persistenceDiagnostics.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.False(persistenceDiagnostics.IsPaused);

        await using (var rollback = blocker.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        Assert.True(persistence.TryWrite(CreateSample(session, 2)));
        using (var flushCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            await persistence.FlushAsync(flushCancellation.Token);
        }

        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.Equal(2, await CountSamplesAsync(activePath));
    }

    [Fact]
    public async Task ActualWalFootprintCrossesThresholdBeforePrimaryFileAndTriggersRotation()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        var activePath = Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName);
        var seed = new MonitoringSqliteStore(activePath);
        await seed.InitializeAsync();
        await seed.CheckpointAndCloseAsync();
        var initialPrimaryBytes = new FileInfo(activePath).Length;
        var options = new MonitoringRotationOptions(
            RotationThresholdBytes: initialPrimaryBytes + 512L * 1024,
            RotationBufferCapacity: 32_768,
            WriterChannelCapacity: 32_768,
            WriterMaximumBatchSize: 64,
            WriterMaximumBatchDelay: TimeSpan.FromMilliseconds(5),
            // Let the writer drain the deliberately grown WAL before the
            // first threshold poll. This isolates the test to byte-accounting
            // rather than racing a writer shutdown against enqueueing.
            ThresholdPollInterval: TimeSpan.FromSeconds(10),
            FailureRetryDelay: TimeSpan.FromSeconds(1));
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "actual-wal-threshold-agent",
            archive,
            options);
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        // This is the production CSV-style reader gate. It deliberately keeps
        // the threshold-triggered switch before the old writer is touched.
        await using var rotationBlocker = await factory.AcquireReadLeaseAsync(CancellationToken.None);

        // Use a separate private-cache read-only connection, like an external
        // CSV reader. The store itself uses shared-cache connections, which
        // would add a same-process table lock unrelated to WAL retention.
        await using var walReader = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = activePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        await walReader.OpenAsync();
        await using (var begin = walReader.CreateCommand())
        {
            begin.CommandText = "BEGIN;";
            await begin.ExecuteNonQueryAsync();
        }

        // Establish a read snapshot before the writer grows the WAL. This is
        // a real SQLite connection, not the factory's injectable byte seam.
        await using (var read = walReader.CreateCommand())
        {
            read.CommandText = "SELECT COUNT(*) FROM monitor_samples;";
            Assert.Equal(0L, Convert.ToInt64(
                await read.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));
        }

        const int sampleCount = 20_000;
        for (var index = 0; index < sampleCount; index++)
        {
            Assert.True(persistence.TryWrite(CreateSample(session, index)));
        }
        await persistence.FlushAsync(CancellationToken.None);

        MonitoringDatabaseFootprint crossedFootprint = default;
        await WaitUntilAsync(
            () =>
            {
                crossedFootprint = GetDatabaseFootprint(activePath);
                return crossedFootprint.PrimaryBytes < options.RotationThresholdBytes
                    && crossedFootprint.WalBytes > 0
                    && crossedFootprint.TotalBytes >= options.RotationThresholdBytes;
            },
            TimeSpan.FromSeconds(15));
        Assert.True(crossedFootprint.PrimaryBytes < options.RotationThresholdBytes);
        Assert.True(crossedFootprint.WalBytes > 0);
        Assert.True(crossedFootprint.TotalBytes >= options.RotationThresholdBytes);

        // The production loop has no injected footprint provider here. It
        // must therefore have observed primary + WAL and requested its
        // exclusive rotation lease, which this test is deliberately holding.
        await WaitUntilAsync(
            () => Assert.IsAssignableFrom<IMonitorSessionPersistenceDiagnostics>(persistence)
                .GetDiagnostics().RotationInProgress,
            TimeSpan.FromSeconds(25));
        Assert.Empty(archive.Snapshot());
        Assert.True(File.Exists(activePath));

        await using (var rollback = walReader.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        // Orderly stop cancels the blocked exclusive acquisition before it
        // drains/disposes the old writer, retaining this same fixed database.
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        Assert.Equal(sampleCount, await CountSamplesAsync(activePath));
    }

    [Fact]
    public async Task PendingRotationWithNeitherActiveNorSealedDatabaseRefusesToCreateEmptyActive()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        var archive = new MonitoringArchiveCoordinator(fixture.DataRoot, fixture.DataRoot, () => fixture.SevenZipPath);
        await archive.InitializeAsync();
        var rawPath = Path.Combine(
            fixture.DataRoot,
            MonitoringArchiveCoordinator.ArchiveDirectoryName,
            MonitoringArchiveCoordinator.SealedDirectoryName,
            "missing-pending-source.db");
        await archive.CreatePendingSealAsync(rawPath, SessionId.New(), Guid.NewGuid().ToString("N"));
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "missing-pending-agent",
            archive);

        await Assert.ThrowsAsync<IOException>(() => factory.InitializeAsync());
        Assert.False(File.Exists(Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName)));
    }

    [Fact]
    public async Task FactoryDoesNotReleaseActiveWriterDuringOrderlyShutdown()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        var archive = new MonitoringArchiveCoordinator(
            fixture.DataRoot,
            fixture.DataRoot,
            () => fixture.SevenZipPath);
        await using var factory = new RotatingMonitorSessionPersistenceFactory(
            fixture.DataRoot,
            "shutdown-drain-agent",
            archive);
        await factory.InitializeAsync();
        var session = CreateSession();
        await using var persistence = factory.Create(session.SessionId);
        await persistence.StartAsync(session, CancellationToken.None);
        Assert.True(persistence.TryWrite(CreateSample(session, 1)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.DisposeAsync().AsTask());
        Assert.True(persistence.TryWrite(CreateSample(session, 2)));
        await persistence.CompleteAsync(
            MonitoringSessionState.Stopped,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(
            2,
            await CountSamplesAsync(Path.Combine(
                fixture.DataRoot,
                RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName)));
    }

    [Fact]
    public async Task CompletedSessionShutdownReleasesTheFixedDatabaseHandle()
    {
        await using var fixture = await RotationFixture.CreateAsync();
        var databasePath = Path.Combine(
            fixture.DataRoot,
            RotatingMonitorSessionPersistenceFactory.MonitoringDatabaseFileName);
        await using (var factory = new RotatingMonitorSessionPersistenceFactory(
                         fixture.DataRoot,
                         "shutdown-handle-agent",
                         new MonitoringArchiveCoordinator(
                             fixture.DataRoot,
                             fixture.DataRoot,
                             () => fixture.SevenZipPath)))
        {
            await factory.InitializeAsync();
            var session = CreateSession();
            await using (var persistence = factory.Create(session.SessionId))
            {
                await persistence.StartAsync(session, CancellationToken.None);
                Assert.True(persistence.TryWrite(CreateSample(session, 1)));
                await persistence.CompleteAsync(
                    MonitoringSessionState.Stopped,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
            }
        }

        await using var exclusive = new FileStream(
            databasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        Assert.True(exclusive.Length > 0);
    }

    private static MonitoringSession CreateSession()
    {
        var systemId = SystemId.New();
        var sessionId = SessionId.New();
        var request = new MonitorRequest(
            sessionId,
            systemId,
            [new MonitorTarget(new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "rotation-disk"), "Disk 0")],
            [MonitorMetricKind.ActiveTimePercent, MonitorMetricKind.ReadBytesPerSecond],
            TimeSpan.FromSeconds(1),
            ContinueWhenUiCloses: true);
        return new MonitoringSession(
            sessionId,
            request,
            MonitoringSessionState.Running,
            DateTimeOffset.UtcNow,
            null);
    }

    private static MonitoringSession CreateTwoTargetSession()
    {
        var systemId = SystemId.New();
        var sessionId = SessionId.New();
        var request = new MonitorRequest(
            sessionId,
            systemId,
            [
                new MonitorTarget(
                    new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "continuous-a"),
                    "Disk A"),
                new MonitorTarget(
                    new StorageObjectId(systemId, StorageObjectKind.PhysicalDisk, "continuous-b"),
                    "Disk B")
            ],
            [MonitorMetricKind.ActiveTimePercent, MonitorMetricKind.ReadBytesPerSecond],
            TimeSpan.FromSeconds(1),
            ContinueWhenUiCloses: true);
        return new MonitoringSession(
            sessionId,
            request,
            MonitoringSessionState.Running,
            DateTimeOffset.UtcNow,
            null);
    }

    private static MonitorSample CreateSample(MonitoringSession session, int index) =>
        new(
            session.SessionId,
            session.Request.Targets[0].ObjectId,
            session.CreatedAtUtc.AddMilliseconds(index),
            [
                new MonitorMetricValue(MonitorMetricKind.ActiveTimePercent, index),
                new MonitorMetricValue(MonitorMetricKind.ReadBytesPerSecond, index * 1024d)
            ]);

    private static async Task<long> CountSamplesAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM monitor_samples;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadDroppedSamplesAsync(string databasePath, SessionId sessionId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT dropped_samples FROM monitor_sessions WHERE session_id = $session;";
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("N"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, int> ToSampleMultiSet(IEnumerable<MonitorSample> samples)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var active = sample.Values.SingleOrDefault(value =>
                value.Kind == MonitorMetricKind.ActiveTimePercent);
            var read = sample.Values.SingleOrDefault(value =>
                value.Kind == MonitorMetricKind.ReadBytesPerSecond);
            Add(
                result,
                SampleIdentity(
                    sample.SessionId.Value.ToString("N"),
                    MonitorSampleBatchWriter.PersistedDeviceId(sample),
                    sample.SampledAtUtc.ToUnixTimeMilliseconds(),
                    active?.Value,
                    read?.Value));
        }

        return result;
    }

    private static async Task<Dictionary<string, int>> ReadSampleMultiSetAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, device_id, timestamp_utc_ms, activity_pct, read_bytes_per_sec
            FROM monitor_samples;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Add(
                result,
                SampleIdentity(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }

        return result;
    }

    private static string SampleIdentity(
        string sessionId,
        string deviceId,
        long timestampUtcMs,
        double? activity,
        double? read) => string.Join(
        "|",
        sessionId,
        deviceId,
        timestampUtcMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        activity?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "<null>",
        read?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "<null>");

    private static void Add(Dictionary<string, int> multiSet, string identity)
    {
        multiSet.TryGetValue(identity, out var count);
        multiSet[identity] = checked(count + 1);
    }

    private static void Merge(Dictionary<string, int> target, IReadOnlyDictionary<string, int> source)
    {
        foreach (var (identity, count) in source)
        {
            target.TryGetValue(identity, out var existing);
            target[identity] = checked(existing + count);
        }
    }

    private static void AssertMultiSetEqual(
        IReadOnlyDictionary<string, int> expected,
        IReadOnlyDictionary<string, int> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (identity, count) in expected)
        {
            Assert.True(actual.TryGetValue(identity, out var actualCount),
                $"Expected monitoring row was not persisted: {identity}");
            Assert.Equal(count, actualCount);
        }
    }

    private static MonitoringDatabaseFootprint GetDatabaseFootprint(string databasePath)
    {
        var primary = File.Exists(databasePath)
            ? new FileInfo(databasePath).Length
            : 0;
        var walPath = databasePath + "-wal";
        var wal = File.Exists(walPath)
            ? new FileInfo(walPath).Length
            : 0;
        return new MonitoringDatabaseFootprint(primary, wal);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException("The expected monitoring rotation did not occur.");
    }

    private readonly record struct MonitoringDatabaseFootprint(long PrimaryBytes, long WalBytes)
    {
        public long TotalBytes => checked(PrimaryBytes + WalBytes);
    }

    private sealed class RotationFixture : IAsyncDisposable
    {
        private RotationFixture(string dataRoot, string sevenZipPath)
        {
            DataRoot = dataRoot;
            SevenZipPath = sevenZipPath;
        }

        public string DataRoot { get; }

        public string SevenZipPath { get; }

        public static Task<RotationFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"WinPool rotation {Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var repository = FindRepositoryRoot();
            var sevenZip = Path.Combine(
                repository,
                "assets",
                "ThirdParty",
                "7zip",
                "26.03",
                "x64",
                "7za.exe");
            Assert.True(File.Exists(sevenZip));
            return Task.FromResult(new RotationFixture(root, sevenZip));
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "WinPool.slnx")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate the WinPool repository root.");
        }
    }
}
