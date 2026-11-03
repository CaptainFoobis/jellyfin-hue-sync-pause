# Hue Sync Pause

A Jellyfin 12.4 plugin that raises the lights while you pause, then puts Hue Sync back the way it was when you press play.

Hue Sync keeps exclusive control of the entertainment lights while it is streaming. The plugin cannot set a brightness until it steps the sync device out of that stream. While sync is already on (video, game, or music), a pause does three things:

1. Switch the sync device to passthrough. Passthrough keeps the HDMI picture up. Powersave is not used, because it can blank the HDMI output.
2. Set the entertainment-area lights to the configured brightness (15% by default) and, optionally, a warm white (mirek 370, about 2700K).
3. Set that level a second time, about 800 milliseconds later, because Hue restores the scene from before sync and can overwrite the first command.

Play and full stop restore the sync mode that was active when the pause began. If you put the sync device into powersave while playback is paused, the plugin leaves it off. If sync is already running again, the plugin does not change the mode.

The plugin does nothing when sync is off. It never turns sync on by itself.

## Shared servers

An empty device list watches every Jellyfin session. On a shared server, set the living-room device name (or the client name) so a pause in another room does not change these lights. Device names and usernames are both required when both fields are filled in. A device entry matches the Jellyfin device name or the client name.

Short pauses inside the delay (700 milliseconds by default) are ignored. If any watched session is still playing, the lights stay with Hue Sync.

## Pairing

Pairing is done with the button on the hardware.

1. Stop sync on the Hue Sync device.
2. Enter its address and choose Pair Hue Sync. Hold the button on the box until the light flashes green, then release it. A red flash after the green one is normal. The request waits about 90 seconds.
3. Enter the Hue Bridge address, choose Pair Hue Bridge, and press the link button. The request waits about 30 seconds.

Find devices looks for `_huesync._tcp` and `_hue._tcp` on the local network and also asks `https://discovery.meethue.com/`. You can type an address instead.

The access token and the bridge application key are stored in the Jellyfin plugin configuration. They are not written to the log and they are not shown on the status panel. Changing the address and saving clears the saved credential for that device.

The plugin talks only to the addresses you enter. Those devices use certificates that are not publicly trusted, so the plugin accepts the certificate presented by each configured address.

## Install

Download [HueSyncPause.zip](https://github.com/CaptainFoobis/jellyfin-hue-sync-pause/releases/latest/download/HueSyncPause.zip). It contains:

- `Jellyfin.Plugin.HueSync.dll`
- `Jellyfin.Plugin.HueSync.deps.json`
- `meta.json`

Copy those three files into a new folder named `HueSyncPause`, then restart Jellyfin.

On Unraid Docker the usual folder is `/mnt/user/appdata/jellyfin/data/plugins/HueSyncPause/`. Some images use `plugins/HueSyncPause` instead. Keep a single copy. The container user is often `nobody:users`.

On Windows, use one of these folders:

- `%ProgramData%\Jellyfin\Server\plugins\HueSyncPause\`
- `%LOCALAPPDATA%\jellyfin\plugins\HueSyncPause\`

The dashboard page is under Plugins, named Hue Sync Pause.

To install it from the catalog, open Dashboard → Plugins → Repositories and add:

`https://raw.githubusercontent.com/CaptainFoobis/jellyfin-hue-sync-pause/main/manifest.json`

Jellyfin can still warn on the plugin details page when the official catalog cannot be reached. The settings page is separate.

This build targets Jellyfin 12.4 (`net10.0`, `targetAbi` `12.4.0.0`). A four-part ABI can hide the plugin on a different server version, including 12.5, until `meta.json` is updated for that version. The Jellyfin container must be able to reach the Hue addresses you enter. Typing those addresses is the reliable path; mDNS often fails from Docker.

## Build

The .NET 10 SDK is required only when compiling this plugin. Unraid and Windows installs use the zip in the Install section.

```powershell
dotnet test "Jellyfin.Plugin.HueSync.slnx"
```

Release build:

```powershell
dotnet build "src/Jellyfin.Plugin.HueSync/Jellyfin.Plugin.HueSync.csproj" -c Release
```

## Limits

The pause and resume sequence has been tested on my Hue Sync box. Unit tests and stand-in Hue clients cover the same sequence.

License: GPL-2.0-only.
