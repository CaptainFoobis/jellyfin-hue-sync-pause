using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.HueSync.Playback;

internal static class PlaybackSnapshots
{
    public static PlaybackSnapshot FromProgress(PlaybackProgressEventArgs eventArgs)
    {
        return Create(
            eventArgs.DeviceId,
            eventArgs.DeviceName,
            eventArgs.ClientName,
            eventArgs.IsPaused,
            UserNames(eventArgs.Users));
    }

    public static PlaybackSnapshot? FromSession(SessionInfo session)
    {
        if (session.NowPlayingItem is null)
        {
            return null;
        }

        var users = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.UserName))
        {
            users.Add(session.UserName);
        }

        if (session.AdditionalUsers is not null)
        {
            foreach (var user in session.AdditionalUsers)
            {
                if (!string.IsNullOrWhiteSpace(user.UserName))
                {
                    users.Add(user.UserName);
                }
            }
        }

        return Create(session.DeviceId, session.DeviceName, session.Client, session.PlayState?.IsPaused ?? false, users);
    }

    public static string KeyFrom(string? deviceId, string? deviceName, string? clientName)
    {
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return deviceId;
        }

        return (deviceName ?? string.Empty) + "|" + (clientName ?? string.Empty);
    }

    private static PlaybackSnapshot Create(string? deviceId, string? deviceName, string? clientName, bool isPaused, IReadOnlyList<string> userNames)
    {
        return new PlaybackSnapshot(
            KeyFrom(deviceId, deviceName, clientName),
            isPaused,
            deviceName ?? string.Empty,
            clientName ?? string.Empty,
            userNames,
            DateTimeOffset.UtcNow);
    }

    private static List<string> UserNames(IEnumerable<User>? users)
    {
        var names = new List<string>();
        if (users is null)
        {
            return names;
        }

        foreach (var user in users)
        {
            if (!string.IsNullOrWhiteSpace(user?.Username))
            {
                names.Add(user.Username);
            }
        }

        return names;
    }
}
