using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.HueSync.Playback;

internal sealed class PlaybackStartConsumer : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly PauseLightingService _service;

    public PlaybackStartConsumer(PauseLightingService service)
    {
        _service = service;
    }

    public Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Item?.IsThemeMedia == true)
        {
            return Task.CompletedTask;
        }

        _service.Note(PlaybackSnapshots.FromProgress(eventArgs));
        return Task.CompletedTask;
    }
}

internal sealed class PlaybackProgressConsumer : IEventConsumer<PlaybackProgressEventArgs>
{
    private readonly PauseLightingService _service;

    public PlaybackProgressConsumer(PauseLightingService service)
    {
        _service = service;
    }

    public Task OnEvent(PlaybackProgressEventArgs eventArgs)
    {
        if (eventArgs.Item?.IsThemeMedia == true)
        {
            return Task.CompletedTask;
        }

        _service.Note(PlaybackSnapshots.FromProgress(eventArgs));
        return Task.CompletedTask;
    }
}

internal sealed class PlaybackStopConsumer : IEventConsumer<PlaybackStopEventArgs>
{
    private readonly PauseLightingService _service;

    public PlaybackStopConsumer(PauseLightingService service)
    {
        _service = service;
    }

    public Task OnEvent(PlaybackStopEventArgs eventArgs)
    {
        _service.Forget(PlaybackSnapshots.KeyFrom(eventArgs.DeviceId, eventArgs.DeviceName, eventArgs.ClientName));
        return Task.CompletedTask;
    }
}
