namespace Jellyfin.Plugin.HueSync.Hue;

/// <summary>
/// Error returned by a Hue Sync device or a Hue Bridge.
/// </summary>
internal sealed class HueApiException : Exception
{
    public HueApiException(string message, int? code = null, int? hueErrorType = null, bool unreachable = false)
        : base(message)
    {
        Code = code;
        HueErrorType = hueErrorType;
        IsUnreachable = unreachable;
    }

    public int? Code { get; }

    public int? HueErrorType { get; }

    /// <summary>
    /// Gets a value indicating whether the device could not be contacted.
    /// </summary>
    public bool IsUnreachable { get; }

    public bool IsButtonNotPressed => Code == 16 || HueErrorType == 101;
}
