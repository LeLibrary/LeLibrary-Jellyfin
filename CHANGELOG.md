# Changelog

## Unreleased: standalone provider plugins

- Added separate development plugins for TorBox, Real-Debrid, AllDebrid and
  Premiumize, each with its own provider settings and direct API client. These
  plugins do not use the LeLibrary backend or setup token.
- Added local library building, Jellyfin-native metadata and artwork refresh,
  streaming proxy infrastructure and a development repository manifest.
- Tested all four against mocked provider APIs in Jellyfin DEV, including movie
  identification, artwork and ranged playback. Further live-provider,
  large-library, migration, recovery and playback tests are required before
  these plugins can be released.

## 0.1.10

- Fix the released LeLibrary plugin failing to load with `NotSupported` on
  Jellyfin 12.0 because it referenced Jellyfin 12.4 assemblies. The plugin
  configuration page is available when the plugin loads successfully.
- Match the repository manifest's minimum Jellyfin version to the API version
  used to build the plugin.
- Keep the existing library intact if TorBox returns an invalid or incomplete
  listing or the listing exceeds the pagination limit.

This release is for the existing LeLibrary-connected plugin. The separate
standalone provider plugins are still under development.
