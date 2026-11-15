# Standalone provider plugins: DEV status

The four projects under `Jellyfin.Plugin.Standalone.*` are independent
Jellyfin plugins. Each has its own provider client, settings, library, playback
route and metadata queue. They do not use a LeLibrary account, setup URL,
backend, metadata or artwork service. Jellyfin's configured metadata and image
providers perform identification and artwork retrieval.

These projects are development builds, not a public release. The local
`scripts/standalone-repo.sh` manifest targets Jellyfin 12.4 and is only for
isolated testing. The public release workflow still packages the separately
maintained, LeLibrary-connected plugin.

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
recovery and broader metadata retry resilience remain release gates for a
stable release. The repository is prepared for a clearly labelled preview;
the preview must not be described as tested against live provider accounts.

The documented account endpoints were also contacted with deliberately invalid
credentials, without a live account. Their error responses informed the
AllDebrid and Premiumize authentication handling; this does not validate a
successful live login, a real library or playback.

## external project source review

The current external project source was reviewed as a reference, not as a dependency.
Provider listing, cache and playback behavior differ: for example,
`internal/torbox/listing.go` uses paged listings, while
`internal/alldebrid/files.go` resolves magnet details in batches. external project's DAV
and streaming code address filesystem exposure and playback; the standalone
plugins use Jellyfin's own integration and do not require external project. No external project change
has been established as necessary for this project.
