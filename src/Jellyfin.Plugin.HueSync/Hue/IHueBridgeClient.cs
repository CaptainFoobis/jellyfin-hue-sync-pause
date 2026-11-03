namespace Jellyfin.Plugin.HueSync.Hue;

public interface IHueBridgeClient
{
    /// <summary>
    /// Sets the entertainment area to the pause level. Returns how many lights were updated.
    /// </summary>
    Task<int> ApplyPauseLevelAsync(
        string areaId,
        int brightnessPercent,
        bool warmWhite,
        int mirek,
        CancellationToken cancellationToken);
}
