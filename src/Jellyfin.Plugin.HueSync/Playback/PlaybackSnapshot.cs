namespace Jellyfin.Plugin.HueSync.Playback;

internal sealed record PlaybackSnapshot(
    string Key,
    bool IsPaused,
    string DeviceName,
    string ClientName,
    IReadOnlyList<string> UserNames,
    DateTimeOffset UpdatedAt);
