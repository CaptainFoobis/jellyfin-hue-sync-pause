using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.HueSync.Hue;

internal static class HueJson
{
    /// <summary>
    /// Hue devices reject a chunked body. This content carries Content-Length and a plain JSON type.
    /// </summary>
    public static ByteArrayContent JsonBody<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    public static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = response.IsSuccessStatusCode ? "{}" : string.Empty;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ParseError(response.StatusCode, text);
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new HueApiException("Hue returned a response that was not JSON.");
        }
    }

    public static HueApiException ParseError(System.Net.HttpStatusCode status, string text)
    {
        int? code = null;
        int? hueType = null;
        var message = string.IsNullOrWhiteSpace(text) ? "Hue returned " + (int)status + "." : text.Trim();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                root = root[0];
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                root = error;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsedCode))
                {
                    code = parsedCode;
                }

                if (root.TryGetProperty("type", out var typeElement) && typeElement.TryGetInt32(out var parsedType))
                {
                    hueType = parsedType;
                }

                if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
                {
                    message = messageElement.GetString() ?? message;
                }
                else if (root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                {
                    message = description.GetString() ?? message;
                }
            }
        }
        catch (JsonException)
        {
            // Keep the raw body as the message.
        }

        return new HueApiException(message, code, hueType);
    }
}
