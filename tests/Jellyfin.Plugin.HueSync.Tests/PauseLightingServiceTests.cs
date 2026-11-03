using Jellyfin.Plugin.HueSync.Configuration;
using Jellyfin.Plugin.HueSync.Hue;
using Jellyfin.Plugin.HueSync.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.HueSync.Tests;

public class PauseLightingServiceTests
{
    [Fact]
    public async Task Pause_while_sync_is_on_sets_passthrough_and_writes_the_lights_twice()
    {
        var sync = new FakeSync { Execution = new SyncExecution(true, "game", "video", "area-9") };
        var bridge = new FakeBridge();
        var store = new MemoryStore();
        var service = Create(sync, bridge, store, Ready());

        service.Note(Paused("Living Room"));

        Assert.Equal(["passthrough"], sync.Modes);
        Assert.Equal(2, bridge.Calls.Count);
        Assert.All(bridge.Calls, call =>
        {
            Assert.Equal("area-9", call.AreaId);
            Assert.Equal(15, call.Brightness);
            Assert.True(call.WarmWhite);
            Assert.Equal(370, call.Mirek);
        });
        Assert.True(store.State.Suspended);
        Assert.Equal("game", store.State.Mode);
        Assert.Contains("15%", service.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pause_while_sync_is_off_leaves_the_lights_alone()
    {
        var sync = new FakeSync { Execution = new SyncExecution(false, "passthrough", "video", "area-9") };
        var bridge = new FakeBridge();
        var service = Create(sync, bridge, new MemoryStore(), Ready());

        var status = await service.SuspendNowAsync(CancellationToken.None);

        Assert.Empty(sync.Modes);
        Assert.Empty(bridge.Calls);
        Assert.Contains("not running", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resume_restores_the_saved_mode()
    {
        var sync = new FakeSync { Execution = new SyncExecution(false, "passthrough", "video", "area-9") };
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "music", HueTarget = "area-9" } };
        var service = Create(sync, new FakeBridge(), store, Ready());

        var status = await service.ResumeNowAsync(CancellationToken.None);

        Assert.Equal(["music"], sync.Modes);
        Assert.False(store.State.Suspended);
        Assert.Contains("music", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Powersave_does_not_turn_sync_back_on()
    {
        var sync = new FakeSync { Execution = new SyncExecution(false, "powersave", "video", null) };
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "video" } };
        var service = Create(sync, new FakeBridge(), store, Ready());

        var status = await service.ResumeNowAsync(CancellationToken.None);

        Assert.Empty(sync.Modes);
        Assert.False(store.State.Suspended);
        Assert.Contains("turned off", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resume_leaves_an_active_sync_mode_in_place()
    {
        var sync = new FakeSync { Execution = new SyncExecution(true, "video", null, "area-9") };
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "game" } };
        var service = Create(sync, new FakeBridge(), store, Ready());

        var status = await service.ResumeNowAsync(CancellationToken.None);

        Assert.Empty(sync.Modes);
        Assert.False(store.State.Suspended);
        Assert.Contains("already running", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_playing_session_blocks_pause()
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = new List<PlaybackSnapshot>
        {
            new("playing", false, "Living Room", "Jellyfin Web", ["ada"], now),
            new("paused", true, "Living Room", "Jellyfin Web", ["ada"], now)
        };
        var sync = new FakeSync { Execution = new SyncExecution(true, "video", null, "area-9") };
        var bridge = new FakeBridge();
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "video" } };
        var service = Create(sync, bridge, store, Ready(), () => sessions);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(sync.Modes);
        Assert.Empty(bridge.Calls);
        Assert.False(store.State.Suspended);
    }

    [Fact]
    public async Task Play_cancels_a_pending_pause()
    {
        var sync = new FakeSync { Execution = new SyncExecution(true, "video", null, "area-9") };
        var bridge = new FakeBridge();
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "video", HueTarget = "area-9" } };
        var started = 0;
        var service = Create(
            sync,
            bridge,
            store,
            Ready(configuration => configuration.PauseDelayMs = 700),
            delay: (time, token) =>
            {
                if (time >= TimeSpan.FromMilliseconds(800))
                {
                    return Task.CompletedTask;
                }

                Interlocked.Increment(ref started);
                return Task.Delay(Timeout.Infinite, token);
            });

        service.Note(Paused("Living Room"));
        Assert.Equal(1, Volatile.Read(ref started));

        service.Note(Paused("Living Room") with { IsPaused = false });
        await WaitUntil(() => !store.State.Suspended);

        Assert.Empty(sync.Modes);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public void A_failed_resume_is_not_repeated_on_the_next_progress_report()
    {
        var sync = new ThrowingSync();
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "video" } };
        var service = Create(sync, new FakeBridge(), store, Ready());

        var playing = new PlaybackSnapshot("room", false, "Living Room", "Jellyfin Web", ["ada"], DateTimeOffset.UtcNow);
        service.Note(playing);
        service.Note(playing);

        Assert.Equal(1, sync.Calls);
        Assert.Contains("did not respond", service.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Removing_the_sync_connection_clears_a_saved_suspension()
    {
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "video", HueTarget = "area-9" } };
        var service = Create(new FakeSync(), new FakeBridge(), store, Ready());

        var status = await service.ForgetSyncConnectionAsync();

        Assert.False(store.State.Suspended);
        Assert.Equal(string.Empty, store.State.Mode);
        Assert.Equal(string.Empty, store.State.HueTarget);
        Assert.Contains("removed", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hue app", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Removing_the_sync_connection_waits_until_an_in_progress_pause_finishes()
    {
        var sync = new BlockingSync();
        var store = new MemoryStore();
        var service = Create(sync, new FakeBridge(), store, Ready());

        service.Note(Paused("Living Room"));
        await sync.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        var forgetting = service.ForgetSyncConnectionAsync();
        var completedEarly = await Task.WhenAny(forgetting, Task.Delay(150));
        Assert.NotSame(forgetting, completedEarly);

        sync.Release.TrySetResult();
        var status = await forgetting.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(store.State.Suspended);
        Assert.Contains("removed", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hue app", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unwatched_device_does_not_change_sync()
    {
        var sync = new FakeSync { Execution = new SyncExecution(true, "video", null, "area-9") };
        var bridge = new FakeBridge();
        var service = Create(sync, bridge, new MemoryStore(), Ready(configuration => configuration.DeviceNames = "Living Room"));

        service.Note(Paused("Phone"));

        Assert.Empty(sync.Modes);
        Assert.Empty(bridge.Calls);
    }

    [Fact]
    public async Task Turning_the_plugin_off_restores_a_suspended_sync_mode()
    {
        var sync = new FakeSync { Execution = new SyncExecution(false, "passthrough", "video", "area-9") };
        var store = new MemoryStore { State = new SuspensionState { Suspended = true, Mode = "video" } };
        var service = Create(
            sync,
            new FakeBridge(),
            store,
            Ready(configuration => configuration.Enabled = false));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(["video"], sync.Modes);
        Assert.False(store.State.Suspended);
    }

    private static PluginConfiguration Ready(Action<PluginConfiguration>? change = null)
    {
        var configuration = new PluginConfiguration
        {
            SyncHost = "192.0.2.10",
            SyncAccessToken = "token",
            BridgeHost = "192.0.2.2",
            BridgeApplicationKey = "key"
        };
        change?.Invoke(configuration);
        return configuration;
    }

    private sealed class ThrowingSync : IHueSyncClient
    {
        public int Calls { get; private set; }

        public Task<SyncExecution?> GetExecutionAsync(CancellationToken cancellationToken)
        {
            Calls++;
            throw new HueApiException("The Hue Sync device did not respond.");
        }

        public Task SetModeAsync(string mode, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Blocks inside the sync read until released, and ignores cancellation, so removal has to wait its turn.
    /// </summary>
    private sealed class BlockingSync : IHueSyncClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public TaskCompletionSource Release => _release;

        public async Task<SyncExecution?> GetExecutionAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return new SyncExecution(true, "video", null, "area-9");
        }

        public Task SetModeAsync(string mode, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private static PauseLightingService Create(
        IHueSyncClient sync,
        FakeBridge bridge,
        MemoryStore store,
        PluginConfiguration configuration,
        Func<IReadOnlyList<PlaybackSnapshot>>? sessions = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        return new PauseLightingService(
            sync,
            bridge,
            store,
            () => configuration,
            sessions ?? (() => []),
            delay ?? ((_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
            NullLogger<PauseLightingService>.Instance);
    }

    private static PlaybackSnapshot Paused(string device)
    {
        return new PlaybackSnapshot(device, true, device, "Jellyfin Web", ["ada"], DateTimeOffset.UtcNow);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The playback action did not finish.");
            }

            await Task.Delay(10);
        }
    }
}
