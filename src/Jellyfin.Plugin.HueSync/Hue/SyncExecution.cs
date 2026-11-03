using System.Text.Json;

namespace Jellyfin.Plugin.HueSync.Hue;

/// <summary>
/// The part of a Hue Sync device's execution state this plugin acts on.
/// </summary>
public sealed record SyncExecution(bool SyncActive, string Mode, string? LastSyncMode, string? HueTarget)
{
    public static SyncExecution Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("execution", out var nested))
        {
            root = nested;
        }

        var mode = ReadString(root, "mode") ?? string.Empty;
        var syncActive = root.TryGetProperty("syncActive", out var active) && active.ValueKind == JsonValueKind.True;
        return new SyncExecution(
            syncActive,
            mode,
            ReadString(root, "lastSyncMode"),
            ReadString(root, "hueTarget"));
    }

    public static bool IsSyncMode(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return false;
        }

        return !mode.Equals("passthrough", StringComparison.OrdinalIgnoreCase)
            && !mode.Equals("powersave", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
