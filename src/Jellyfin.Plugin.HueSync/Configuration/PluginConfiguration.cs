using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HueSync.Configuration;

/// <summary>
/// Settings for pause lighting. Secrets stay in the Jellyfin plugin configuration file.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether pause and play change the Hue Sync device.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the Hue Sync device address, as a hostname or IP, with an optional port.
    /// </summary>
    public string SyncHost { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Hue Sync API bearer token from pairing.
    /// </summary>
    public string SyncAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Hue Sync registration id that belongs to <see cref="SyncAccessToken"/>.
    /// </summary>
    public string SyncRegistrationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Hue Bridge address.
    /// </summary>
    public string BridgeHost { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Hue Bridge application key from pairing.
    /// </summary>
    public string BridgeApplicationKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an entertainment area id. Empty uses the area selected on the sync device.
    /// </summary>
    public string EntertainmentAreaId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the pause brightness, from 1 to 100 percent.
    /// </summary>
    public int PauseBrightness { get; set; } = 15;

    /// <summary>
    /// Gets or sets a value indicating whether pause lighting uses a warm white instead of the last sync color.
    /// </summary>
    public bool UseWarmWhite { get; set; } = true;

    /// <summary>
    /// Gets or sets the warm-white color temperature in mirek (153 is cool, 500 is warm).
    /// </summary>
    public int WarmWhiteMirek { get; set; } = 370;

    /// <summary>
    /// Gets or sets Jellyfin device or client names that control the lights. Empty watches every session.
    /// </summary>
    public string DeviceNames { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets Jellyfin usernames that control the lights. Empty watches every user.
    /// </summary>
    public string UserNames { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how long to wait, in milliseconds, before treating a pause as intentional.
    /// </summary>
    public int PauseDelayMs { get; set; } = 700;

    /// <summary>
    /// Gets the pause brightness clamped to the range Hue accepts.
    /// </summary>
    public int ClampedBrightness => Math.Clamp(PauseBrightness, 1, 100);

    /// <summary>
    /// Gets the warm-white mirek clamped to the Hue color-temperature range.
    /// </summary>
    public int ClampedMirek => Math.Clamp(WarmWhiteMirek, 153, 500);

    /// <summary>
    /// Gets the pause delay clamped to a range that still feels immediate.
    /// </summary>
    public int ClampedPauseDelayMs => Math.Clamp(PauseDelayMs, 0, 10000);
}
