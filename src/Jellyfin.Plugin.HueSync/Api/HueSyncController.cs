using Jellyfin.Plugin.HueSync.Configuration;
using Jellyfin.Plugin.HueSync.Hue;
using Jellyfin.Plugin.HueSync.Playback;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.HueSync.Api;

/// <summary>
/// Admin actions for pairing Hue devices and previewing pause lighting.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("HueSync")]
public class HueSyncController : ControllerBase
{
    private readonly HueSyncClient _sync;
    private readonly HueBridgeClient _bridge;
    private readonly PauseLightingService _lighting;

    /// <summary>
    /// Initializes a new instance of the <see cref="HueSyncController"/> class.
    /// </summary>
    public HueSyncController(HueSyncClient sync, HueBridgeClient bridge, PauseLightingService lighting)
    {
        _sync = sync;
        _bridge = bridge;
        _lighting = lighting;
    }

    /// <summary>
    /// Gets the current sync state and entertainment areas. Tokens are not included.
    /// </summary>
    [HttpGet("Status")]
    public async Task<ActionResult<HueStatusResponse>> Status(CancellationToken cancellationToken)
    {
        var configuration = CurrentConfiguration();
        var response = new HueStatusResponse
        {
            Status = _lighting.Status,
            Suspended = _lighting.IsSuspended,
            SyncPaired = !string.IsNullOrWhiteSpace(configuration.SyncHost) && !string.IsNullOrWhiteSpace(configuration.SyncAccessToken),
            BridgePaired = !string.IsNullOrWhiteSpace(configuration.BridgeHost) && !string.IsNullOrWhiteSpace(configuration.BridgeApplicationKey)
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var syncRead = ReadSyncAsync(timeout.Token);
        var areaRead = ReadAreasAsync(response.BridgePaired, timeout.Token);
        var reads = Task.WhenAll(syncRead, areaRead);
        var finished = await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)).ConfigureAwait(false);
        if (finished != reads)
        {
            timeout.Cancel();
        }

        var notes = new List<string>();
        if (syncRead.IsCompletedSuccessfully)
        {
            syncRead.Result.Apply?.Invoke(response);
            AddNote(notes, syncRead.Result.Note);
        }
        else
        {
            notes.Add("The Hue Sync device did not respond.");
        }

        if (areaRead.IsCompletedSuccessfully)
        {
            areaRead.Result.Apply?.Invoke(response);
            AddNote(notes, areaRead.Result.Note);
        }
        else if (response.BridgePaired)
        {
            notes.Add("The Hue Bridge did not respond.");
        }

        response.Message = notes.Count == 0 ? null : string.Join(" ", notes);
        return Ok(response);
    }

    /// <summary>
    /// Gets the entertainment areas from the paired Hue Bridge.
    /// </summary>
    [HttpGet("Areas")]
    public async Task<ActionResult<IReadOnlyList<HueAreaResponse>>> Areas(CancellationToken cancellationToken)
    {
        var configuration = CurrentConfiguration();
        if (string.IsNullOrWhiteSpace(configuration.BridgeHost) || string.IsNullOrWhiteSpace(configuration.BridgeApplicationKey))
        {
            return BadRequest(new ErrorResponse { Error = "Pair the Hue Bridge on this page first." });
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var areas = await _bridge.GetEntertainmentAreasAsync(timeout.Token).ConfigureAwait(false);
            var listed = areas.Select(area => new HueAreaResponse { Id = area.Id, Name = area.Name }).ToList();
            return Ok(listed);
        }
        catch (HueApiException ex)
        {
            return BadRequest(new ErrorResponse { Error = ex.Message });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status408RequestTimeout, new ErrorResponse { Error = "The Hue Bridge did not respond." });
        }
    }

    /// <summary>
    /// Looks for Hue Sync devices and Hue Bridges on the local network.
    /// </summary>
    [HttpGet("Discover")]
    public async Task<ActionResult<IReadOnlyList<DiscoveredDeviceResponse>>> Discover(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await MdnsDiscovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            return Ok(devices.Select(device => new DiscoveredDeviceResponse
            {
                Kind = device.Kind,
                Host = device.Host,
                Name = device.Name
            }).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Jellyfin could not search the local network for Hue devices. Enter the IP address from the Hue app."
            });
        }
    }

    /// <summary>
    /// Pairs a Hue Sync device. Press the button on the device while this request runs.
    /// </summary>
    [HttpPost("PairSync")]
    public Task<ActionResult<PairResponse>> PairSync([FromBody] HostRequest request, CancellationToken cancellationToken)
    {
        return PollAsync(
            request.Host,
            TimeSpan.FromSeconds(90),
            async (host, token) =>
            {
                var paired = await _sync.RegisterAsync(host, token).ConfigureAwait(false);
                Save(configuration =>
                {
                    configuration.SyncHost = host;
                    configuration.SyncAccessToken = paired.AccessToken;
                    configuration.SyncRegistrationId = paired.RegistrationId;
                });
                return paired.DeviceName;
            },
            "The button press was not seen. Click Pair Hue Sync again and hold the button until the light flashes green, then release it. Stop sync on the device first.",
            cancellationToken);
    }

    /// <summary>
    /// Pairs a Hue Bridge. Press the link button while this request runs.
    /// </summary>
    [HttpPost("PairBridge")]
    public Task<ActionResult<PairResponse>> PairBridge([FromBody] HostRequest request, CancellationToken cancellationToken)
    {
        return PollAsync(
            request.Host,
            TimeSpan.FromSeconds(30),
            async (host, token) =>
            {
                var key = await _bridge.RegisterAsync(host, token).ConfigureAwait(false);
                Save(configuration =>
                {
                    configuration.BridgeHost = host;
                    configuration.BridgeApplicationKey = key;
                });
                return "Hue Bridge";
            },
            "Press the link button on the Hue Bridge and try again.",
            cancellationToken);
    }

    /// <summary>
    /// Removes the saved Hue Sync address and pairing.
    /// </summary>
    [HttpPost("ForgetSync")]
    public async Task<ActionResult<ActionResponse>> ForgetSync()
    {
        Save(configuration =>
        {
            configuration.SyncHost = string.Empty;
            configuration.SyncAccessToken = string.Empty;
            configuration.SyncRegistrationId = string.Empty;
        });
        var status = await _lighting.ForgetSyncConnectionAsync().ConfigureAwait(false);
        return Ok(new ActionResponse { Status = status });
    }

    /// <summary>
    /// Removes the saved Hue Bridge address and pairing.
    /// </summary>
    [HttpPost("ForgetBridge")]
    public async Task<ActionResult<ActionResponse>> ForgetBridge()
    {
        Save(configuration =>
        {
            configuration.BridgeHost = string.Empty;
            configuration.BridgeApplicationKey = string.Empty;
        });
        var status = await _lighting.ForgetBridgeConnectionAsync().ConfigureAwait(false);
        return Ok(new ActionResponse { Status = status });
    }

    /// <summary>
    /// Drops Hue Sync to passthrough and raises the entertainment lights.
    /// </summary>
    [HttpPost("Suspend")]
    public async Task<ActionResult<ActionResponse>> Suspend(CancellationToken cancellationToken)
    {
        var status = await _lighting.SuspendNowAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new ActionResponse { Status = status });
    }

    /// <summary>
    /// Restores the Hue Sync mode saved when pause lighting started.
    /// </summary>
    [HttpPost("Resume")]
    public async Task<ActionResult<ActionResponse>> Resume(CancellationToken cancellationToken)
    {
        var status = await _lighting.ResumeNowAsync(cancellationToken).ConfigureAwait(false);
        return Ok(new ActionResponse { Status = status });
    }

    private async Task<DeviceRead> ReadSyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            var execution = await _sync.GetExecutionAsync(cancellationToken).ConfigureAwait(false);
            if (execution is null)
            {
                return new DeviceRead(null, null);
            }

            return new DeviceRead(
                null,
                status =>
                {
                    status.SyncActive = execution.SyncActive;
                    status.SyncMode = execution.Mode;
                    status.HueTarget = execution.HueTarget;
                });
        }
        catch (HueApiException ex)
        {
            return new DeviceRead(ex.Message, null);
        }
        catch (OperationCanceledException)
        {
            return new DeviceRead("The Hue Sync device did not respond.", null);
        }
    }

    private async Task<DeviceRead> ReadAreasAsync(bool bridgePaired, CancellationToken cancellationToken)
    {
        if (!bridgePaired)
        {
            return new DeviceRead(null, null);
        }

        try
        {
            var areas = await _bridge.GetEntertainmentAreasAsync(cancellationToken).ConfigureAwait(false);
            var listed = areas.Select(area => new HueAreaResponse { Id = area.Id, Name = area.Name }).ToList();
            return new DeviceRead(null, status => status.Areas = listed);
        }
        catch (HueApiException ex)
        {
            return new DeviceRead(ex.Message, null);
        }
        catch (OperationCanceledException)
        {
            return new DeviceRead("The Hue Bridge did not respond.", null);
        }
    }

    private sealed record DeviceRead(string? Note, Action<HueStatusResponse>? Apply);

    private static void AddNote(List<string> notes, string? note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            notes.Add(note);
        }
    }

    private async Task<ActionResult<PairResponse>> PollAsync(
        string? rawHost,
        TimeSpan window,
        Func<string, CancellationToken, Task<string?>> attempt,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        if (!HostAddress.TryNormalize(rawHost, out var host, out var error))
        {
            return BadRequest(new ErrorResponse { Error = error });
        }

        var deadline = DateTimeOffset.UtcNow + window;
        var sawWaitingButton = false;
        string? lastUnreachable = null;
        while (true)
        {
            try
            {
                var name = await attempt(host, cancellationToken).ConfigureAwait(false);
                return Ok(new PairResponse
                {
                    Paired = true,
                    Host = host,
                    Name = name,
                    Message = "Paired " + (string.IsNullOrWhiteSpace(name) ? host : name) + "."
                });
            }
            catch (HueApiException ex) when (ex.IsButtonNotPressed || ex.IsUnreachable)
            {
                if (ex.IsButtonNotPressed)
                {
                    sawWaitingButton = true;
                }
                else
                {
                    lastUnreachable = ex.Message;
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    var message = sawWaitingButton || string.IsNullOrWhiteSpace(lastUnreachable)
                        ? timeoutMessage
                        : lastUnreachable;
                    return StatusCode(StatusCodes.Status408RequestTimeout, new ErrorResponse { Error = message });
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (HueApiException ex)
            {
                return BadRequest(new ErrorResponse { Error = ex.Message });
            }
        }
    }

    private static PluginConfiguration CurrentConfiguration()
    {
        return Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    private static void Save(Action<PluginConfiguration> update)
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("Hue Sync Pause is not loaded.");
        update(plugin.Configuration);
        plugin.UpdateConfiguration(plugin.Configuration);
    }
}

/// <summary>
/// A host entered on the configuration page.
/// </summary>
public sealed class HostRequest
{
    /// <summary>
    /// Gets or sets the IP address or hostname.
    /// </summary>
    public string? Host { get; set; }
}

/// <summary>
/// Sync and bridge state safe to show in the dashboard.
/// </summary>
public sealed class HueStatusResponse
{
    /// <summary>
    /// Gets or sets the latest pause-lighting status.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this plugin has switched Hue Sync to passthrough.
    /// </summary>
    public bool Suspended { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Hue Sync token is saved.
    /// </summary>
    public bool SyncPaired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a Hue Bridge application key is saved.
    /// </summary>
    public bool BridgePaired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the sync device is currently streaming.
    /// </summary>
    public bool? SyncActive { get; set; }

    /// <summary>
    /// Gets or sets the sync device mode.
    /// </summary>
    public string? SyncMode { get; set; }

    /// <summary>
    /// Gets or sets the entertainment target reported by the sync device.
    /// </summary>
    public string? HueTarget { get; set; }

    /// <summary>
    /// Gets or sets a connection error, when the devices could not be read.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the entertainment areas on the paired bridge.
    /// </summary>
    public List<HueAreaResponse> Areas { get; set; } = [];
}

/// <summary>
/// An entertainment area that can receive the pause brightness.
/// </summary>
public sealed class HueAreaResponse
{
    /// <summary>
    /// Gets or sets the area id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the area name.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A Hue device found on the network.
/// </summary>
public sealed class DiscoveredDeviceResponse
{
    /// <summary>
    /// Gets or sets the device kind, either sync or bridge.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address to enter in the configuration page.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name advertised by the device.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The result of a pairing attempt.
/// </summary>
public sealed class PairResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether pairing saved a credential.
    /// </summary>
    public bool Paired { get; set; }

    /// <summary>
    /// Gets or sets the paired address.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the device name, when the device reported one.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the confirmation message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// The status line returned by a preview action.
/// </summary>
public sealed class ActionResponse
{
    /// <summary>
    /// Gets or sets the status line.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// An error message for the configuration page.
/// </summary>
public sealed class ErrorResponse
{
    /// <summary>
    /// Gets or sets the error message.
    /// </summary>
    public string Error { get; set; } = string.Empty;
}
