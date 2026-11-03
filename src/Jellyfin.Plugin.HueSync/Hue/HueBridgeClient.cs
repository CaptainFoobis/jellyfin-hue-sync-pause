using System.Text.Json;
using Jellyfin.Plugin.HueSync.Configuration;

namespace Jellyfin.Plugin.HueSync.Hue;

public sealed class HueBridgeClient : IHueBridgeClient, IDisposable
{
    private readonly Func<PluginConfiguration> _configuration;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HueBridgeClient(Func<PluginConfiguration> configuration)
        : this(configuration, LocalHttp.Create(), ownsClient: true)
    {
    }

    internal HueBridgeClient(Func<PluginConfiguration> configuration, HttpClient httpClient, bool ownsClient = false)
    {
        _configuration = configuration;
        _http = httpClient;
        _ownsClient = ownsClient;
    }

    public async Task<int> ApplyPauseLevelAsync(
        string areaId,
        int brightnessPercent,
        bool warmWhite,
        int mirek,
        CancellationToken cancellationToken)
    {
        var (host, key) = Credentials();
        if (host is null || key is null)
        {
            throw new HueApiException("Pair the Hue Bridge on the plugin page first.");
        }

        var normalized = EntertainmentLights.NormalizeAreaId(areaId);
        if (string.IsNullOrEmpty(normalized))
        {
            return 0;
        }

        if (EntertainmentLights.IsGroupId(normalized))
        {
            await SetGroupV1Async(host, key, normalized, brightnessPercent, warmWhite, mirek, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var lightIds = await ResolveLightIdsAsync(host, key, normalized, cancellationToken).ConfigureAwait(false);
        var updated = 0;
        foreach (var lightId in lightIds)
        {
            var changed = await SetLightAsync(host, key, lightId, brightnessPercent, warmWhite, mirek, cancellationToken).ConfigureAwait(false);
            if (changed)
            {
                updated++;
            }
        }

        return updated;
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> GetEntertainmentAreasAsync(CancellationToken cancellationToken)
    {
        var (host, key) = Credentials();
        if (host is null || key is null)
        {
            return [];
        }

        using var response = await SendAsync(HttpMethod.Get, host, "/clip/v2/resource/entertainment_configuration", key, null, cancellationToken).ConfigureAwait(false);
        using var document = await HueJson.ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        var areas = new List<(string Id, string Name)>();
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return areas;
        }

        foreach (var area in data.EnumerateArray())
        {
            var id = area.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            string? name = null;
            if (area.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("name", out var nameElement))
            {
                name = nameElement.GetString();
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                areas.Add((id, string.IsNullOrWhiteSpace(name) ? id : name));
            }
        }

        return areas;
    }

    public async Task<string?> GetBridgeNameAsync(CancellationToken cancellationToken)
    {
        var (host, key) = Credentials();
        if (host is null || key is null)
        {
            return null;
        }

        using var response = await SendAsync(HttpMethod.Get, host, "/clip/v2/resource/bridge", key, null, cancellationToken).ConfigureAwait(false);
        using var document = await HueJson.ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var bridge in data.EnumerateArray())
        {
            if (bridge.TryGetProperty("bridge_id", out _))
            {
                return "Hue Bridge";
            }
        }

        return data.GetArrayLength() > 0 ? "Hue Bridge" : null;
    }

    public async Task<string> RegisterAsync(string host, CancellationToken cancellationToken)
    {
        var body = HueJson.JsonBody(new Dictionary<string, object>
        {
            ["devicetype"] = "jellyfinhue#server",
            ["generateclientkey"] = true
        });

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(HttpMethod.Post, host, "/api", token: null, body, cancellationToken).ConfigureAwait(false);
        }
        catch (HueApiException)
        {
            body = HueJson.JsonBody(new Dictionary<string, object>
            {
                ["devicetype"] = "jellyfinhue#server",
                ["generateclientkey"] = true
            });
            response = await SendAsync(HttpMethod.Post, host, "/api", token: null, body, cancellationToken, useHttps: false).ConfigureAwait(false);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw HueJson.ParseError(response.StatusCode, text);
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "[]" : text);
            var root = document.RootElement;
            var entry = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("error", out var error))
            {
                var type = error.TryGetProperty("type", out var typeElement) && typeElement.TryGetInt32(out var parsed) ? parsed : (int?)null;
                var description = error.TryGetProperty("description", out var descriptionElement) ? descriptionElement.GetString() : "Hue Bridge refused pairing.";
                throw new HueApiException(description ?? "Hue Bridge refused pairing.", hueErrorType: type);
            }

            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("success", out var success)
                && success.TryGetProperty("username", out var username))
            {
                var key = username.GetString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }
            }

            throw new HueApiException("Hue Bridge pairing did not return an application key.");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task<IReadOnlyList<string>> ResolveLightIdsAsync(string host, string key, string areaId, CancellationToken cancellationToken)
    {
        var area = await GetTextAsync(host, key, "/clip/v2/resource/entertainment_configuration/" + Uri.EscapeDataString(areaId), cancellationToken).ConfigureAwait(false);
        var entertainment = await GetTextAsync(host, key, "/clip/v2/resource/entertainment", cancellationToken).ConfigureAwait(false);
        var lights = await GetTextAsync(host, key, "/clip/v2/resource/light", cancellationToken).ConfigureAwait(false);
        return EntertainmentLights.Resolve(area, entertainment, lights, areaId);
    }

    private async Task<string> GetTextAsync(string host, string key, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, host, path, key, null, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw HueJson.ParseError(response.StatusCode, text);
        }

        return string.IsNullOrWhiteSpace(text) ? "{}" : text;
    }

    private async Task<bool> SetLightAsync(
        string host,
        string key,
        string lightId,
        int brightnessPercent,
        bool warmWhite,
        int mirek,
        CancellationToken cancellationToken)
    {
        var updated = await PutLightAsync(host, key, lightId, brightnessPercent, warmWhite, mirek, cancellationToken).ConfigureAwait(false);
        if (updated || !warmWhite)
        {
            return updated;
        }

        return await PutLightAsync(host, key, lightId, brightnessPercent, warmWhite: false, mirek, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PutLightAsync(
        string host,
        string key,
        string lightId,
        int brightnessPercent,
        bool warmWhite,
        int mirek,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object>
        {
            ["on"] = new Dictionary<string, object> { ["on"] = true },
            ["dimming"] = new Dictionary<string, object> { ["brightness"] = (double)brightnessPercent },
            ["dynamics"] = new Dictionary<string, object> { ["duration"] = 800 }
        };
        if (warmWhite)
        {
            payload["color_temperature"] = new Dictionary<string, object> { ["mirek"] = mirek };
        }

        using var response = await SendAsync(
            HttpMethod.Put,
            host,
            "/clip/v2/resource/light/" + Uri.EscapeDataString(lightId),
            key,
            HueJson.JsonBody(payload),
            cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (warmWhite && text.Contains("color_temperature", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw HueJson.ParseError(response.StatusCode, text);
        }

        if (!TryReadErrors(text, out var errors) || errors.Count == 0)
        {
            return true;
        }

        if (warmWhite && errors.Exists(error => error.Contains("color_temperature", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        throw new HueApiException(errors[0]);
    }

    private static bool TryReadErrors(string text, out List<string> errors)
    {
        errors = [];
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            if (!document.RootElement.TryGetProperty("errors", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var error in array.EnumerateArray())
            {
                errors.Add(error.ValueKind == JsonValueKind.String ? error.GetString() ?? "Hue Bridge rejected the light change." : error.GetRawText());
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task SetGroupV1Async(
        string host,
        string key,
        string groupId,
        int brightnessPercent,
        bool warmWhite,
        int mirek,
        CancellationToken cancellationToken)
    {
        var bri = Math.Clamp((int)Math.Round(brightnessPercent / 100d * 254d), 1, 254);
        var payload = new Dictionary<string, object>
        {
            ["on"] = true,
            ["bri"] = bri,
            ["transitiontime"] = 8
        };
        if (warmWhite)
        {
            payload["ct"] = mirek;
        }

        var path = "/api/" + Uri.EscapeDataString(key) + "/groups/" + Uri.EscapeDataString(groupId) + "/action";
        using var response = await SendAsync(HttpMethod.Put, host, path, token: null, HueJson.JsonBody(payload), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw HueJson.ParseError(response.StatusCode, text);
        }
    }

    private (string? Host, string? Key) Credentials()
    {
        var config = _configuration();
        if (string.IsNullOrWhiteSpace(config.BridgeHost) || string.IsNullOrWhiteSpace(config.BridgeApplicationKey))
        {
            return (null, null);
        }

        return (config.BridgeHost.Trim(), config.BridgeApplicationKey.Trim());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string host,
        string path,
        string? token,
        HttpContent? content,
        CancellationToken cancellationToken,
        bool useHttps = true)
    {
        var scheme = useHttps ? "https://" : "http://";
        var request = LocalHttp.Request(method, scheme + HostAddress.ForUrl(host) + path, content);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.TryAddWithoutValidation("hue-application-key", token);
        }

        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            throw new HueApiException("Could not reach the Hue Bridge at " + host + ".");
        }
    }
}
