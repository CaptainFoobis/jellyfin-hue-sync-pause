using Jellyfin.Plugin.HueSync.Configuration;

namespace Jellyfin.Plugin.HueSync.Playback;

internal static class SessionSelector
{
    public static WatchState Aggregate(IEnumerable<PlaybackSnapshot> sessions, PluginConfiguration configuration, DateTimeOffset now)
    {
        var playing = false;
        var paused = false;
        foreach (var session in sessions)
        {
            if (IsStale(session, now) || !IsWatched(configuration, session.DeviceName, session.ClientName, session.UserNames))
            {
                continue;
            }

            if (session.IsPaused)
            {
                paused = true;
            }
            else
            {
                playing = true;
            }
        }

        if (playing)
        {
            return WatchState.Playing;
        }

        return paused ? WatchState.Paused : WatchState.Idle;
    }

    public static bool IsStale(PlaybackSnapshot snapshot, DateTimeOffset now)
    {
        var age = now - snapshot.UpdatedAt;
        return snapshot.IsPaused ? age > TimeSpan.FromHours(12) : age > TimeSpan.FromMinutes(2);
    }

    public static bool IsWatched(PluginConfiguration configuration, string? deviceName, string? clientName, IReadOnlyList<string> userNames)
    {
        var devices = Split(configuration.DeviceNames);
        if (devices.Count > 0
            && !Contains(devices, deviceName)
            && !Contains(devices, clientName))
        {
            return false;
        }

        var users = Split(configuration.UserNames);
        if (users.Count == 0)
        {
            return true;
        }

        foreach (var userName in userNames)
        {
            if (Contains(users, userName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(HashSet<string> values, string? candidate)
    {
        return !string.IsNullOrWhiteSpace(candidate) && values.Contains(candidate.Trim());
    }

    private static HashSet<string> Split(string? text)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return values;
        }

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                values.Add(part);
            }
        }

        return values;
    }
}
