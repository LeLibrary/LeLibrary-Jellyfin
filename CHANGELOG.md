# Changelog

## Standalone 0.0.1

- Added separate plugins for TorBox, Real-Debrid, AllDebrid and
  Premiumize, each with its own provider settings and direct API client. These
  plugins do not use the LeLibrary backend or setup token.
- Added local library building, Jellyfin-native metadata and artwork refresh,
  streaming proxy infrastructure and a shared repository manifest.
- Gave each provider its own settings page, protected unrelated existing
  libraries from being reused, and retried failed native metadata refreshes.
- Let Jellyfin populate episode titles during the initial metadata refresh;
  later refreshes continue to preserve existing metadata.
- Improved Premiumize error handling for API responses that report errors
  despite an HTTP 200 status.
- Corrected the AllDebrid authentication probe and TorBox handling for
  unfinished downloads and expired delivery links. TorBox web downloads and
  Usenet can be enabled independently when the account supports them.
- Tested all four against mocked provider APIs in Jellyfin DEV, including movie
  identification, artwork and ranged playback. Further live-provider,
  large-library, migration, recovery and playback testing remains outstanding.

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
