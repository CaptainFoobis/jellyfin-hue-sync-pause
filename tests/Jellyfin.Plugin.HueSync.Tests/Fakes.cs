using Jellyfin.Plugin.HueSync.Hue;
using Jellyfin.Plugin.HueSync.Playback;

namespace Jellyfin.Plugin.HueSync.Tests;

internal sealed class FakeSync : IHueSyncClient
{
    public SyncExecution? Execution { get; set; }

    public List<string> Modes { get; } = [];

    public Task<SyncExecution?> GetExecutionAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(Execution);
    }

    public Task SetModeAsync(string mode, CancellationToken cancellationToken)
    {
        Modes.Add(mode);
        if (Execution is not null)
        {
            Execution = Execution with
            {
                Mode = mode,
                SyncActive = SyncExecution.IsSyncMode(mode)
            };
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeBridge : IHueBridgeClient
{
    public List<(string AreaId, int Brightness, bool WarmWhite, int Mirek)> Calls { get; } = [];

    public Task<int> ApplyPauseLevelAsync(
        string areaId,
        int brightnessPercent,
        bool warmWhite,
        int mirek,
        CancellationToken cancellationToken)
    {
        Calls.Add((areaId, brightnessPercent, warmWhite, mirek));
        return Task.FromResult(3);
    }
}

internal sealed class MemoryStore : ISuspensionStore
{
    public SuspensionState State { get; set; } = new();

    public SuspensionState Load()
    {
        return State;
    }

    public void Save(SuspensionState state)
    {
        State = state;
    }
}
