namespace Jellyfin.Plugin.HueSync.Hue;

public interface IHueSyncClient
{
    Task<SyncExecution?> GetExecutionAsync(CancellationToken cancellationToken);

    Task SetModeAsync(string mode, CancellationToken cancellationToken);
}
