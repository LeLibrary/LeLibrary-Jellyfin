# Standalone provider plugins: DEV status

The four projects under `Jellyfin.Plugin.Standalone.*` are independent
Jellyfin plugins. Each has its own provider client, settings, library, playback
route and metadata queue. They do not use a LeLibrary account, setup URL,
backend, metadata or artwork service. Jellyfin's configured metadata and image
providers perform identification and artwork retrieval.

The local `scripts/standalone-repo.sh` manifest targets Jellyfin 12.4 and is
for isolated testing. The standalone release workflow publishes all four
plugins while preserving the separately maintained legacy plugin.

## DEV validation

- All four plugins loaded together on Jellyfin 12.4 and returned distinct
  configuration pages. Each authenticated against its own mocked provider API,
  synchronized a movie, and left that movie unchanged on a repeat sync. The
  TorBox test included torrents, web downloads and Usenet with distinct IDs.
- Jellyfin installed all four release archives from a local repository manifest,
  then loaded the plugins and their separate settings pages after restart. A
  release-mode manifest retained the existing legacy plugin entry and older
  standalone version entries in a local preservation test.
- Jellyfin identified the movie as TMDb 603 and returned an overview, poster
  and backdrop for each provider. A TorBox mock TV episode received its native
  TMDb episode title, overview and image.
- Each mock movie, including the three TorBox source families, returned the requested 10-byte range through Jellyfin's video
  streaming endpoint. A simulated failure from each provider left existing item
  IDs unchanged. Items and metadata survived a server restart; a watched flag
  survived a TorBox resync.
- The metadata queue retains unfinished work when Jellyfin reports provider
  failures. It retries without rebuilding the library, and persists across a
  restart. A TorBox DEV test blocked TMDb, restored connectivity and confirmed
  the queue drained automatically without another Jellyfin restart.

These are mock-based integration checks, not proof of behavior with live
accounts, all release naming formats, complete season packs or every Jellyfin
client. The plugins currently perform additive synchronization; safe cleanup and
non-destructive migration from existing libraries are not complete. Live
provider testing, large-library measurements with current code, rate-limit
recovery and broader metadata retry resilience remain outstanding.

The documented account endpoints were also contacted with deliberately invalid
credentials, without a live account. Their error responses informed the
AllDebrid and Premiumize authentication handling; this does not validate a
successful live login, a real library or playback.

## Provider-specific behavior

Provider listing, caching and playback behavior differ. Each standalone
plugin communicates directly with its provider and uses Jellyfin's own
integration without requiring an external mount.
