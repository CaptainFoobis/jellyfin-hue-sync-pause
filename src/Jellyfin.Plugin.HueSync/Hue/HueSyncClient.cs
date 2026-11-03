using System.Text.Json;
using Jellyfin.Plugin.HueSync.Configuration;

namespace Jellyfin.Plugin.HueSync.Hue;

public sealed class HueSyncClient : IHueSyncClient, IDisposable
{
    private readonly Func<PluginConfiguration> _configuration;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HueSyncClient(Func<PluginConfiguration> configuration)
        : this(configuration, LocalHttp.Create(), ownsClient: true)
    {
    }

    internal HueSyncClient(Func<PluginConfiguration> configuration, HttpClient httpClient, bool ownsClient = false)
    {
        _configuration = configuration;
        _http = httpClient;
        _ownsClient = ownsClient;
    }

    public async Task<SyncExecution?> GetExecutionAsync(CancellationToken cancellationToken)
    {
        var (host, token) = Credentials();
        if (host is null || token is null)
        {
            return null;
        }

        using var response = await SendAsync(HttpMethod.Get, host, "/api/v1/execution", token, null, cancellationToken).ConfigureAwait(false);
        using var document = await HueJson.ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return SyncExecution.Parse(document.RootElement.GetRawText());
    }

    public async Task SetModeAsync(string mode, CancellationToken cancellationToken)
    {
        var (host, token) = Credentials();
        if (host is null || token is null)
        {
            throw new HueApiException("Pair the Hue Sync device on the plugin page first.");
        }

        using var response = await SendAsync(
            HttpMethod.Put,
            host,
            "/api/v1/execution",
            token,
            HueJson.JsonBody(new Dictionary<string, string> { ["mode"] = mode }),
            cancellationToken).ConfigureAwait(false);
        using var _ = await HueJson.ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string AccessToken, string RegistrationId, string? DeviceName)> RegisterAsync(
        string host,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            host,
            "/api/v1/registrations",
            token: null,
            HueJson.JsonBody(new Dictionary<string, string>
            {
                ["appName"] = "Jellyfin Hue",
                ["instanceName"] = "Jellyfin",
                ["appSecret"] = "MDAwMTExMDAwMTExMDAwMTExMDAwMTExMDAwMTExMDA="
            }),
            cancellationToken).ConfigureAwait(false);
        using var document = await HueJson.ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        ThrowIfRegistrationError(root);
        var accessToken = RequiredString(root, "accessToken");
        var registrationId = RequiredString(root, "registrationId");
        string? deviceName = null;
        try
        {
            using var deviceResponse = await SendAsync(HttpMethod.Get, host, "/api/v1/device", accessToken, null, cancellationToken).ConfigureAwait(false);
            using var device = await HueJson.ReadDocumentAsync(deviceResponse, cancellationToken).ConfigureAwait(false);
            if (device.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                deviceName = name.GetString();
            }
        }
        catch (HueApiException)
        {
            // Pairing already succeeded. The name is only for the confirmation message.
        }

        return (accessToken, registrationId, deviceName);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private (string? Host, string? Token) Credentials()
    {
        var config = _configuration();
        if (string.IsNullOrWhiteSpace(config.SyncHost) || string.IsNullOrWhiteSpace(config.SyncAccessToken))
        {
            return (null, null);
        }

        return (config.SyncHost.Trim(), config.SyncAccessToken.Trim());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string host,
        string path,
        string? token,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var request = LocalHttp.Request(method, "https://" + HostAddress.ForUrl(host) + path, content);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        }

        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            throw new HueApiException("Could not reach the Hue Sync device at " + host + ".", unreachable: true);
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        throw new HueApiException("Hue Sync pairing did not return " + name + ".");
    }

    /// <summary>
    /// The sync box answers the registration call with HTTP 200 and <c>code</c> 16 until its button is held.
    /// </summary>
    private static void ThrowIfRegistrationError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || HasText(root, "accessToken"))
        {
            return;
        }

        if (!root.TryGetProperty("code", out var codeElement) || !codeElement.TryGetInt32(out var code))
        {
            return;
        }

        var message = "Invalid State";
        if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString() ?? message;
        }

        throw new HueApiException(message, code);
    }

    private static bool HasText(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString());
    }
}
