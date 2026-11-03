using Jellyfin.Plugin.HueSync.Configuration;
using Jellyfin.Plugin.HueSync.Hue;
using Jellyfin.Plugin.HueSync.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.HueSync;

/// <summary>
/// Registers the Hue clients and the playback listeners.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Func<PluginConfiguration>>(_ => () =>
            Plugin.Instance?.Configuration ?? new PluginConfiguration());
        serviceCollection.AddSingleton<ISuspensionStore, FileSuspensionStore>();
        serviceCollection.AddSingleton<HueSyncClient>();
        serviceCollection.AddSingleton<IHueSyncClient>(services => services.GetRequiredService<HueSyncClient>());
        serviceCollection.AddSingleton<HueBridgeClient>();
        serviceCollection.AddSingleton<IHueBridgeClient>(services => services.GetRequiredService<HueBridgeClient>());
        serviceCollection.AddSingleton<PauseLightingService>();
        serviceCollection.AddHostedService(services => services.GetRequiredService<PauseLightingService>());
        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackStartConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackProgressEventArgs>, PlaybackProgressConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackStopConsumer>();
    }
}
