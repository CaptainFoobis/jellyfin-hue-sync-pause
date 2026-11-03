using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.HueSync.Hue;

/// <summary>
/// Normalizes a user-entered Hue device address and turns it into a URL host.
/// </summary>
internal static class HostAddress
{
    public static bool TryNormalize(string? input, out string host, out string error)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter an address.";
            return false;
        }

        var text = input.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                error = "That address is not valid.";
                return false;
            }

            var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            text = uri.Host + port;
        }

        if (text.Contains('/') || text.Contains(' ') || text.Contains('@') || text.Contains('\\'))
        {
            error = "Enter the IP address or hostname only.";
            return false;
        }

        if (text.Length > 253)
        {
            error = "That address is too long.";
            return false;
        }

        host = text;
        error = string.Empty;
        return true;
    }

    public static string ForUrl(string host)
    {
        if (host.StartsWith('['))
        {
            return host;
        }

        var bare = host;
        var port = string.Empty;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon)
        {
            bare = host[..colon];
            port = host[colon..];
        }

        if (IPAddress.TryParse(bare, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return "[" + bare + "]" + port;
        }

        return host;
    }
}
