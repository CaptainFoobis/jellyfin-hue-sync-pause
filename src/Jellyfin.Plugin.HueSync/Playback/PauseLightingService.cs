using Jellyfin.Plugin.HueSync.Configuration;
using Jellyfin.Plugin.HueSync.Hue;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HueSync.Playback;

/// <summary>
/// While a watched Jellyfin session is paused and Hue Sync is active, drops sync to passthrough
/// and raises the entertainment lights. Play and stop restore the sync mode.
/// </summary>
public sealed class PauseLightingService : IHostedService
{
    private readonly IHueSyncClient _sync;
    private readonly IHueBridgeClient _bridge;
    private readonly ISuspensionStore _store;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly Func<IReadOnlyList<PlaybackSnapshot>> _liveSessions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger<PauseLightingService> _logger;
    private readonly Dictionary<string, PlaybackSnapshot> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _operation;
    private int _generation;
    private WatchState? _appliedState;
    private string _status = "Waiting for playback.";

    public PauseLightingService(
        IHueSyncClient sync,
        IHueBridgeClient bridge,
        ISuspensionStore store,
        Func<PluginConfiguration> configuration,
        ISessionManager sessionManager,
        ILogger<PauseLightingService> logger)
        : this(
            sync,
            bridge,
            store,
            configuration,
            () => sessionManager.Sessions.Select(PlaybackSnapshots.FromSession).Where(snapshot => snapshot is not null).Select(snapshot => snapshot!).ToArray(),
            Task.Delay,
            logger)
    {
    }

    internal PauseLightingService(
        IHueSyncClient sync,
        IHueBridgeClient bridge,
        ISuspensionStore store,
        Func<PluginConfiguration> configuration,
        Func<IReadOnlyList<PlaybackSnapshot>> liveSessions,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger<PauseLightingService> logger)
    {
        _sync = sync;
        _bridge = bridge;
        _store = store;
        _configuration = configuration;
        _liveSessions = liveSessions;
        _delay = delay;
        _logger = logger;
    }

    public string Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    public bool IsSuspended => _store.Load().Suspended;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var snapshot in _liveSessions())
            {
                lock (_sessions)
                {
                    _sessions.TryAdd(snapshot.Key, snapshot);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read current Jellyfin sessions");
        }

        Schedule();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _operation?.Cancel();
        return Task.CompletedTask;
    }

    internal void Note(PlaybackSnapshot snapshot)
    {
        lock (_sessions)
        {
            _sessions[snapshot.Key] = snapshot;
        }

        Schedule();
    }

    internal void Forget(string key)
    {
        lock (_sessions)
        {
            _sessions.Remove(key);
        }

        Schedule();
    }

    public async Task<string> SuspendNowAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(async token =>
        {
            await SuspendAsync(token).ConfigureAwait(false);
            SetApplied(WatchState.Paused);
        }, cancellationToken).ConfigureAwait(false);
        return Status;
    }

    /// <summary>
    /// Forgets that this plugin suspended Hue Sync and drops the remembered playback state.
    /// A pause that is already talking to Hue is cancelled first, then the saved suspension is cleared.
    /// </summary>
    /// <returns>The status to show after the connection is removed.</returns>
    public async Task<string> ForgetSyncConnectionAsync()
    {
        var status = "Removed the Hue Sync connection.";
        await FinishCurrentWorkAsync(() =>
        {
            if (_store.Load().Suspended)
            {
                status = "Removed the Hue Sync connection. If sync is still off, turn it back on in the Hue app.";
            }

            _store.Save(new SuspensionState());
            SetStatus(status);
        }).ConfigureAwait(false);
        return status;
    }

    /// <summary>
    /// Drops the remembered playback state after the Hue Bridge pairing is removed.
    /// </summary>
    /// <returns>The status to show after the connection is removed.</returns>
    public async Task<string> ForgetBridgeConnectionAsync()
    {
        const string status = "Removed the Hue Bridge connection.";
        await FinishCurrentWorkAsync(() => SetStatus(status)).ConfigureAwait(false);
        return status;
    }

    public async Task<string> ResumeNowAsync(CancellationToken cancellationToken)
    {
        await RunExclusiveAsync(async token =>
        {
            await ResumeAsync(token).ConfigureAwait(false);
            SetApplied(WatchState.Playing);
        }, cancellationToken).ConfigureAwait(false);
        return Status;
    }

    private async Task FinishCurrentWorkAsync(Action complete)
    {
        Interlocked.Increment(ref _generation);
        var previous = Interlocked.Exchange(ref _operation, null);
        previous?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            previous?.Dispose();
            lock (_stateLock)
            {
                _appliedState = null;
            }

            complete();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Schedule()
    {
        var state = ReadState();
        if (state == ReadApplied())
        {
            return;
        }

        var generation = Interlocked.Increment(ref _generation);
        var operation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _operation, operation);
        previous?.Cancel();
        previous?.Dispose();
        _ = RunAsync(generation, operation);
    }

    private async Task RunAsync(int generation, CancellationTokenSource operation)
    {
        var acquired = false;
        var finished = false;
        try
        {
            await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
            acquired = true;
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            var state = ReadState();
            if (state == WatchState.Paused)
            {
                var delayMs = _configuration().ClampedPauseDelayMs;
                if (delayMs > 0)
                {
                    await _delay(TimeSpan.FromMilliseconds(delayMs), operation.Token).ConfigureAwait(false);
                }

                if (generation != Volatile.Read(ref _generation) || ReadState() != WatchState.Paused)
                {
                    return;
                }

                await SuspendAsync(operation.Token).ConfigureAwait(false);
            }
            else
            {
                await ResumeAsync(operation.Token).ConfigureAwait(false);
            }

            finished = true;
        }
        catch (OperationCanceledException)
        {
            // A newer pause or play replaced this one.
        }
        catch (Exception ex)
        {
            // Remember this play or pause state. The next progress report must not call Hue again
            // while the playback state is unchanged.
            finished = true;
            _logger.LogWarning(ex, "Hue Sync pause lighting failed");
            SetStatus(ex.Message);
        }
        finally
        {
            if (finished && generation == Volatile.Read(ref _generation))
            {
                SetApplied(ReadState());
            }

            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _operation, operation);
        previous?.Cancel();
        previous?.Dispose();
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            await action(operation.Token).ConfigureAwait(false);
            if (generation == Volatile.Read(ref _generation))
            {
                // The forced action already recorded the applied state.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SuspendAsync(CancellationToken cancellationToken)
    {
        if (!TryGetReady(out var configuration))
        {
            return;
        }

        var execution = await _sync.GetExecutionAsync(cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            SetStatus("Pair the Hue Sync device on this page first.");
            return;
        }

        var saved = _store.Load();
        if (!execution.SyncActive && !(saved.Suspended && !SyncExecution.IsSyncMode(execution.Mode)))
        {
            SetStatus("Hue Sync is not running, so the lights were left alone.");
            return;
        }

        var mode = SyncExecution.IsSyncMode(execution.Mode) ? execution.Mode : saved.Mode;
        if (execution.SyncActive)
        {
            if (!SyncExecution.IsSyncMode(mode))
            {
                mode = execution.LastSyncMode ?? "video";
            }

            await _sync.SetModeAsync("passthrough", cancellationToken).ConfigureAwait(false);
            saved = new SuspensionState
            {
                Suspended = true,
                Mode = mode,
                HueTarget = execution.HueTarget ?? saved.HueTarget
            };
            _store.Save(saved);
            _logger.LogInformation("Hue Sync was in {Mode}. Switched to passthrough while Jellyfin is paused", mode);
        }

        var areaId = string.IsNullOrWhiteSpace(configuration.EntertainmentAreaId) ? saved.HueTarget : configuration.EntertainmentAreaId.Trim();
        if (string.IsNullOrWhiteSpace(areaId))
        {
            areaId = execution.HueTarget ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(areaId))
        {
            SetStatus("Hue Sync is paused, but it did not report an entertainment area to dim.");
            return;
        }

        var count = await ApplyLightsTwiceAsync(configuration, areaId, cancellationToken).ConfigureAwait(false);
        SetStatus("Paused Hue Sync (" + mode + ") and set " + count + " light" + (count == 1 ? string.Empty : "s") + " to " + configuration.ClampedBrightness + "%.");
        _logger.LogInformation("Set {Count} Hue lights to {Brightness}% while playback is paused", count, configuration.ClampedBrightness);
    }

    private async Task<int> ApplyLightsTwiceAsync(PluginConfiguration configuration, string areaId, CancellationToken cancellationToken)
    {
        var count = await _bridge.ApplyPauseLevelAsync(
            areaId,
            configuration.ClampedBrightness,
            configuration.UseWarmWhite,
            configuration.ClampedMirek,
            cancellationToken).ConfigureAwait(false);

        // Stopping sync restores the scene from before sync started. Set the pause level again after that restore.
        await _delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
        return await _bridge.ApplyPauseLevelAsync(
            areaId,
            configuration.ClampedBrightness,
            configuration.UseWarmWhite,
            configuration.ClampedMirek,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        var saved = _store.Load();
        if (!saved.Suspended)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_configuration().SyncHost) || string.IsNullOrWhiteSpace(_configuration().SyncAccessToken))
        {
            SetStatus("Hue Sync is still paused. Pair the device to resume it.");
            return;
        }

        var execution = await _sync.GetExecutionAsync(cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            SetStatus("Could not reach Hue Sync to resume it.");
            return;
        }

        if (execution.SyncActive)
        {
            ClearSuspension();
            SetStatus("Hue Sync is already running.");
            return;
        }

        if (execution.Mode.Equals("powersave", StringComparison.OrdinalIgnoreCase))
        {
            ClearSuspension();
            SetStatus("The Hue Sync device was turned off, so sync was left off.");
            _logger.LogInformation("Left Hue Sync off because the device is in powersave");
            return;
        }

        if (!SyncExecution.IsSyncMode(saved.Mode))
        {
            ClearSuspension();
            SetStatus("There was no sync mode to restore.");
            return;
        }

        await _sync.SetModeAsync(saved.Mode, cancellationToken).ConfigureAwait(false);
        ClearSuspension();
        SetStatus("Resumed Hue Sync in " + saved.Mode + " mode.");
        _logger.LogInformation("Resumed Hue Sync in {Mode} mode", saved.Mode);
    }

    private bool TryGetReady(out PluginConfiguration configuration)
    {
        configuration = _configuration();
        if (!configuration.Enabled)
        {
            SetStatus("Pause lighting is turned off.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuration.SyncHost) || string.IsNullOrWhiteSpace(configuration.SyncAccessToken))
        {
            SetStatus("Pair the Hue Sync device on this page first.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(configuration.BridgeHost) || string.IsNullOrWhiteSpace(configuration.BridgeApplicationKey))
        {
            SetStatus("Pair the Hue Bridge on this page first.");
            return false;
        }

        return true;
    }

    private void ClearSuspension()
    {
        _store.Save(new SuspensionState());
    }

    private WatchState ReadState()
    {
        List<PlaybackSnapshot> sessions;
        lock (_sessions)
        {
            var now = DateTimeOffset.UtcNow;
            var stale = _sessions.Where(entry => SessionSelector.IsStale(entry.Value, now)).Select(entry => entry.Key).ToArray();
            foreach (var key in stale)
            {
                _sessions.Remove(key);
            }

            sessions = _sessions.Values.ToList();
        }

        var configuration = _configuration();
        if (!configuration.Enabled)
        {
            return WatchState.Idle;
        }

        return SessionSelector.Aggregate(sessions, configuration, DateTimeOffset.UtcNow);
    }

    private WatchState? ReadApplied()
    {
        lock (_stateLock)
        {
            return _appliedState;
        }
    }

    private void SetApplied(WatchState state)
    {
        lock (_stateLock)
        {
            _appliedState = state;
        }
    }

    private void SetStatus(string status)
    {
        lock (_stateLock)
        {
            _status = status;
        }
    }
}
