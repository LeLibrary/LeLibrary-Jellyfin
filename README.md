# LeLibrary for Jellyfin

Turn your debrid account into a normal Jellyfin library: movies, shows, seasons
and episodes, named and matched the way LeLibrary names them.

**Status: in development.** There is no installable release yet. The repository
below will exist once the first version is tagged; nothing here is ready to
install.

## How it works

The plugin does not download or re-host anything, and LeLibrary never sees your
debrid key or a byte of your playback:

1. You paste your setup's LeLibrary URL and your **own** debrid API key into the
   plugin settings.
2. The plugin asks LeLibrary which titles belong in the library and what they
   are called, then lists your debrid account itself.
3. It writes one small `.strm` file per title or episode (plus an `.nfo` with
   the metadata and art URL).
4. When you press play, Jellyfin asks the plugin for a fresh debrid link, and the
   plugin streams it through to the player. Links expire; the plugin mints a new
   one each time.

Your files stay on your debrid account. Playback runs through your own Jellyfin
server, so its upload bandwidth is the ceiling.

## Requirements

- Jellyfin 10.11 (the `net9.0` build) or Jellyfin 12 (the `net10.0` build).
- The indexed LeLibrary library (a Postgres-backed LeLibrary instance).
- A debrid account. TorBox is supported first; Real-Debrid, AllDebrid and
  Premiumize follow.

## Your key is on disk in plain text

Jellyfin stores plugin settings as plain XML. Anyone who can read the config
directory can read your debrid API key. That is true of every debrid plugin, and
this one does not pretend otherwise: protect the host or the disk (file
permissions, a dedicated machine, full-disk encryption).

The same applies to your LeLibrary URL: it contains your setup secret, it is
written into the `.nfo` files as art URLs, and anyone who can read it can read
your library.

## Building

Two SDKs are needed because Jellyfin 10.11 runs on .NET 9 and Jellyfin 12 on
.NET 10:

```bash
dotnet build -c Release            # both targets
dotnet test                        # unit tests
```

## Licence

Not chosen yet. Jellyfin itself is GPL-3.0, and a plugin that links against
`Jellyfin.Controller` is normally distributed under GPL-3.0 as well; the licence
will be added before the first release.
