# Hue Sync Pause

## Version

The plugin version has four parts. The fourth part stays 0.

A change under `src/Jellyfin.Plugin.HueSync` bumps the version before that work is finished:

- A fix increments the third part: `1.0.0.0` becomes `1.0.1.0`.
- A new feature increments the second part: `1.1.0.0`.
- A breaking change increments the first part: `2.0.0.0`.

Write that same version in each of these places:

- `src/Jellyfin.Plugin.HueSync/Jellyfin.Plugin.HueSync.csproj` (`Version`, `AssemblyVersion`, and `FileVersion`)
- `src/Jellyfin.Plugin.HueSync/meta.json` (`version` and `changelog`)
- `build.yaml` (`version` and `changelog`)
- `manifest.json`: add the new release as the first `versions` entry, with `version`, `changelog`, `targetAbi`, `sourceUrl` ending in `/download/vX.Y.Z.W/HueSyncPause.zip`, the MD5 `checksum` of that zip, and a `timestamp`
- `src/Jellyfin.Plugin.HueSync/Configuration/configPage.html`: `Settings page X.Y.Z auth`, using the first three parts

A README-only change keeps the current version.

`dotnet test` fails when those copies disagree, and when plugin source differs from the latest `v*` tag while the version is still that tag.

Publishing a release builds `HueSyncPause.zip` from the Release output. The zip root contains `Jellyfin.Plugin.HueSync.dll`, `Jellyfin.Plugin.HueSync.deps.json`, and `meta.json`. Tag `vX.Y.Z.W` and create a new GitHub release. Leave existing tags and release zips where they are.
