using Jellyfin.Plugin.HueSync.Configuration;
using Jellyfin.Plugin.HueSync.Playback;

namespace Jellyfin.Plugin.HueSync.Tests;

public class SessionSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_sessions_are_idle()
    {
        Assert.Equal(WatchState.Idle, SessionSelector.Aggregate([], new PluginConfiguration(), Now));
    }

    [Fact]
    public void A_playing_session_wins_over_a_paused_session()
    {
        var sessions = new[]
        {
            Snapshot(paused: true, "room", "web", "ada", Now),
            Snapshot(paused: false, "room", "web", "ada", Now)
        };

        Assert.Equal(WatchState.Playing, SessionSelector.Aggregate(sessions, new PluginConfiguration(), Now));
    }

    [Fact]
    public void Paused_is_used_when_nothing_is_playing()
    {
        var sessions = new[] { Snapshot(paused: true, "room", "web", "ada", Now) };

        Assert.Equal(WatchState.Paused, SessionSelector.Aggregate(sessions, new PluginConfiguration(), Now));
    }

    [Fact]
    public void Device_filter_matches_the_device_name_or_the_client_name()
    {
        var configuration = new PluginConfiguration { DeviceNames = "Living Room, Shield" };

        Assert.True(SessionSelector.IsWatched(configuration, "Living Room", "Other", ["ada"]));
        Assert.True(SessionSelector.IsWatched(configuration, "Phone", "Shield", ["ada"]));
        Assert.False(SessionSelector.IsWatched(configuration, "Phone", "Web", ["ada"]));
    }

    [Fact]
    public void User_filter_applies_together_with_the_device_filter()
    {
        var configuration = new PluginConfiguration
        {
            DeviceNames = "Living Room",
            UserNames = "ada, bo"
        };

        Assert.True(SessionSelector.IsWatched(configuration, "Living Room", "Web", ["ada"]));
        Assert.False(SessionSelector.IsWatched(configuration, "Living Room", "Web", ["cam"]));
        Assert.False(SessionSelector.IsWatched(configuration, "Phone", "Web", ["ada"]));
        Assert.True(SessionSelector.IsWatched(new PluginConfiguration(), "Phone", "Web", []));
    }

    [Fact]
    public void Playing_sessions_expire_quickly_and_paused_sessions_stay()
    {
        Assert.True(SessionSelector.IsStale(Snapshot(false, "room", "web", "ada", Now.AddMinutes(-3)), Now));
        Assert.False(SessionSelector.IsStale(Snapshot(false, "room", "web", "ada", Now.AddMinutes(-1)), Now));
        Assert.False(SessionSelector.IsStale(Snapshot(true, "room", "web", "ada", Now.AddHours(-11)), Now));
        Assert.True(SessionSelector.IsStale(Snapshot(true, "room", "web", "ada", Now.AddHours(-13)), Now));

        var stalePlaying = Snapshot(false, "room", "web", "ada", Now.AddMinutes(-3));
        Assert.Equal(WatchState.Idle, SessionSelector.Aggregate([stalePlaying], new PluginConfiguration(), Now));
    }

    private static PlaybackSnapshot Snapshot(bool paused, string device, string client, string user, DateTimeOffset updated)
    {
        return new PlaybackSnapshot(device + "|" + client, paused, device, client, [user], updated);
    }
}
