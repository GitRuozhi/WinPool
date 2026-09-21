using WinPool.Agent;

namespace WinPool.Agent.Tests;

public sealed class AgentStartupTaskRunnerTests
{
    [Fact]
    public async Task CompleteRunsAsyncStartupWorkOutsideAnUnpumpedUiContext()
    {
        using var context = new HeldSynchronizationContext();
        var completion = new TaskCompletionSource<Exception?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(
            () =>
            {
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    Program.AgentStartupTaskRunner.Complete(
                        async () =>
                        {
                            await Task.Yield();
                        });
                    completion.TrySetResult(null);
                }
                catch (Exception exception)
                {
                    completion.TrySetResult(exception);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                }
            })
        {
            IsBackground = true
        };

        worker.Start();
        try
        {
            Assert.Null(await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            context.Release();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(0, context.PostCount);
    }

    private sealed class HeldSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly ManualResetEventSlim released = new(false);
        private int postCount;

        public int PostCount => Volatile.Read(ref postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref postCount);
            _ = Task.Run(
                () =>
                {
                    released.Wait();
                    callback(state);
                });
        }

        public void Release() => released.Set();

        public void Dispose() => released.Dispose();
    }
}
