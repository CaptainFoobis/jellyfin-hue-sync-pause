using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.HueSync.Playback;

public sealed class SuspensionState
{
    public bool Suspended { get; set; }

    public string Mode { get; set; } = string.Empty;

    public string HueTarget { get; set; } = string.Empty;
}

public interface ISuspensionStore
{
    SuspensionState Load();

    void Save(SuspensionState state);
}

internal sealed class FileSuspensionStore : ISuspensionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public FileSuspensionStore(IApplicationPaths applicationPaths)
    {
        _path = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.HueSync.suspension.json");
    }

    public SuspensionState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new SuspensionState();
            }

            return JsonSerializer.Deserialize<SuspensionState>(File.ReadAllText(_path), Options) ?? new SuspensionState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new SuspensionState();
        }
    }

    public void Save(SuspensionState state)
    {
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(_path, JsonSerializer.Serialize(state, Options));
    }
}
