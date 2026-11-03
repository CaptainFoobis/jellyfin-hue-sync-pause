using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.HueSync.Hue;

/// <summary>
/// HTTP client for Hue devices on the local network.
/// Hue Bridge and Hue Sync devices present certificates that are not in the public trust store.
/// This client is only used for the addresses configured on the plugin page.
/// </summary>
internal static class LocalHttp
{
    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = AcceptLocalDeviceCertificate
            },
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    public static HttpRequestMessage Request(HttpMethod method, string url, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = content,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.ExpectContinue = false;
        return request;
    }

    private static bool AcceptLocalDeviceCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        return true;
    }
}
