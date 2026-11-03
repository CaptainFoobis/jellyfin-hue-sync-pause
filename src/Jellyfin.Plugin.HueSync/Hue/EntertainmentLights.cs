using System.Text.Json;

namespace Jellyfin.Plugin.HueSync.Hue;

/// <summary>
/// Maps an entertainment configuration to the light services the bridge can dim.
/// </summary>
internal static class EntertainmentLights
{
    public static IReadOnlyList<string> Resolve(string areaJson, string entertainmentJson, string lightJson, string areaId)
    {
        using var areaDocument = JsonDocument.Parse(areaJson);
        using var entertainmentDocument = JsonDocument.Parse(entertainmentJson);
        using var lightDocument = JsonDocument.Parse(lightJson);
        return Resolve(areaDocument.RootElement, entertainmentDocument.RootElement, lightDocument.RootElement, areaId);
    }

    public static IReadOnlyList<string> Resolve(JsonElement areaRoot, JsonElement entertainmentRoot, JsonElement lightRoot, string areaId)
    {
        var area = FindById(DataArray(areaRoot), areaId);
        if (area is null)
        {
            return [];
        }

        var entertainmentOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entertainment in DataArray(entertainmentRoot))
        {
            var id = ReadId(entertainment);
            var owner = ReadOwner(entertainment);
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(owner))
            {
                entertainmentOwners[id] = owner;
            }
        }

        var lightsByDevice = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var light in DataArray(lightRoot))
        {
            var id = ReadId(light);
            var owner = ReadOwner(light);
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(owner) && !lightsByDevice.ContainsKey(owner))
            {
                lightsByDevice[owner] = id;
            }
        }

        var lights = new List<string>();
        if (!area.Value.TryGetProperty("channels", out var channels) || channels.ValueKind != JsonValueKind.Array)
        {
            return lights;
        }

        foreach (var channel in channels.EnumerateArray())
        {
            if (!channel.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var member in members.EnumerateArray())
            {
                if (!member.TryGetProperty("service", out var service))
                {
                    continue;
                }

                var rid = ReadString(service, "rid");
                var rtype = ReadString(service, "rtype");
                if (string.IsNullOrEmpty(rid))
                {
                    continue;
                }

                if (string.Equals(rtype, "light", StringComparison.OrdinalIgnoreCase))
                {
                    Add(lights, rid);
                    continue;
                }

                if (entertainmentOwners.TryGetValue(rid, out var deviceId)
                    && lightsByDevice.TryGetValue(deviceId, out var lightId))
                {
                    Add(lights, lightId);
                }
            }
        }

        return lights;
    }

    public static string? AreaName(JsonElement areaRoot, string areaId)
    {
        var area = FindById(DataArray(areaRoot), areaId);
        if (area is null)
        {
            return null;
        }

        if (area.Value.TryGetProperty("metadata", out var metadata))
        {
            return ReadString(metadata, "name");
        }

        return null;
    }

    public static string NormalizeAreaId(string hueTarget)
    {
        var text = hueTarget.Trim().Trim('/');
        var slash = text.LastIndexOf('/');
        if (slash >= 0 && slash < text.Length - 1)
        {
            text = text[(slash + 1)..];
        }

        return text;
    }

    public static bool IsGroupId(string areaId)
    {
        return !Guid.TryParse(areaId, out _);
    }

    private static void Add(List<string> lights, string id)
    {
        if (!lights.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            lights.Add(id);
        }
    }

    private static JsonElement? FindById(IEnumerable<JsonElement> items, string id)
    {
        var list = items as IReadOnlyList<JsonElement> ?? items.ToArray();
        foreach (var item in list)
        {
            if (string.Equals(ReadId(item), id, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        // A request for one configuration returns that configuration alone, sometimes without repeating the id.
        if (list.Count == 1)
        {
            return list[0];
        }

        return null;
    }

    private static IEnumerable<JsonElement> DataArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            return data.EnumerateArray().ToArray();
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToArray();
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            return [root];
        }

        return [];
    }

    private static string? ReadId(JsonElement element)
    {
        return ReadString(element, "id");
    }

    private static string? ReadOwner(JsonElement element)
    {
        if (!element.TryGetProperty("owner", out var owner))
        {
            return null;
        }

        return ReadString(owner, "rid");
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
