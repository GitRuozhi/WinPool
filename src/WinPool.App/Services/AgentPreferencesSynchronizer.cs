using Microsoft.UI.Dispatching;
using WinPool.Application;
using WinPool.App.ViewModels;
using WinPool.Domain;
using WinPool.Infrastructure.Windows;
using IAgentConnection = WinPool.Application.IAgentConnection;

namespace WinPool.App.Services;

/// <summary>
/// Keeps the ViewModel's background preferences in sync with
/// agent-settings.json. The Agent's data-less change event is the primary
/// trigger, a reconnect reseed is the second, and the file watcher is the
/// low-frequency fallback when the Agent was unreachable. All three feeds
/// converge on one serialized reload pipeline.
/// </summary>
internal sealed class AgentPreferencesSynchronizer : IDisposable
{
    private readonly WorkspaceViewModel viewModel;
    private readonly IAgentConnection? agentConnection;
    private readonly DispatcherQueue dispatcherQueue;
    private readonly AgentPreferencesReloadCoordinator coordinator;
    private readonly FileSystemWatcher watcher;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task eventLoop;
    private bool disposed;

    public AgentPreferencesSynchronizer(
        WorkspaceViewModel viewModel,
        IAgentConnection? agentConnection,
        DispatcherQueue dispatcherQueue,
        LocalAgentPreferencesService? readerService = null)
    {
        this.viewModel = viewModel
            ?? throw new ArgumentNullException(nameof(viewModel));
        this.agentConnection = agentConnection;
        this.dispatcherQueue = dispatcherQueue
            ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        var service = readerService ?? new LocalAgentPreferencesService();
        AgentSettingsPath = service.AgentSettingsPath;
        coordinator = new AgentPreferencesReloadCoordinator(service, ApplyOnUiThread);

        Directory.CreateDirectory(Path.GetDirectoryName(AgentSettingsPath)!);
        watcher = new FileSystemWatcher(
            Path.GetDirectoryName(AgentSettingsPath)!,
            Path.GetFileName(AgentSettingsPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        watcher.Changed += (_, _) => coordinator.Trigger();
        watcher.Created += (_, _) => coordinator.Trigger();
        watcher.Renamed += (_, _) => coordinator.Trigger();

        eventLoop = agentConnection is null
            ? Task.CompletedTask
            : Task.Run(() => RunEventLoopAsync());
    }

    internal string AgentSettingsPath { get; }

    /// <summary>Performs the initial load. Safe to call once after construction.</summary>
    public void Start() => coordinator.Trigger();

    private void ApplyOnUiThread(AgentPreferences preferences) =>
        dispatcherQueue.TryEnqueue(() => viewModel.ApplyAgentPreferences(preferences));

    private async Task RunEventLoopAsync()
    {
        try
        {
            await foreach (var agentEvent in agentConnection!.WatchAsync(
                               cancellation.Token))
            {
                if (agentEvent is AgentPreferencesChangedEvent
                    or AgentStateReseedEvent)
                {
                    coordinator.Trigger();
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException
                or InvalidOperationException
                or ObjectDisposedException)
        {
            // The watcher and the next reconnect reseed remain as fallbacks.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cancellation.Cancel();
        watcher.Dispose();
    }
}
