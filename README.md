# LeLibrary for Jellyfin

Turn your debrid account into a normal Jellyfin library: movies, shows, seasons
and episodes, named and matched the way LeLibrary names them.

**Status: working, not yet released.** The loop is verified end to end on a
real library (36,000+ files, movies and episodes, Range playback through
Jellyfin). No release is tagged yet, so the repository URL above is empty until
one is.

## Install

1. **Add the plugin repository.** Jellyfin -> Dashboard -> Plugins ->
   Repositories -> add this URL:

   ```
   https://lelibrary.github.io/LeLibrary-Jellyfin/manifest.json
   ```

2. **Install the plugin.** Plugins -> Catalog -> **LeLibrary** -> Install, then
   restart Jellyfin. Jellyfin updates it from the same repository from then on.

3. **Copy your LeLibrary URL.** On your LeLibrary Setup page open the
   **Jellyfin** card: it has the repository URL and your personal setup link.
   Copy the setup link.

4. **Fill in the plugin settings.** Dashboard -> Plugins -> LeLibrary:

   | Field | Value |
   |---|---|
   | Debrid provider | TorBox (Real-Debrid, AllDebrid and Premiumize follow) |
   | Debrid API key | your own key |
   | LeLibrary URL | the link from the Setup card |
   | Library folder | `/config/lelibrary` (default) |
   | Server URL | the address players use, e.g. `http://192.168.1.20:8096` |
   | Sync every | 6 hours (default) |

   Press **Test key**, then **Run sync now**. The first sync writes one `.strm`
   and one `.nfo` per title or episode; on a large library that takes a minute
   or two.

5. **Add two libraries.** Jellyfin -> Dashboard -> Libraries -> Add:

   - **Movies** with the folder `<Library folder>/Movies`
   - **Shows** with the folder `<Library folder>/Shows`

   The plugin keeps the two apart on purpose: one mixed library cannot type its
   items, so movies and episodes need their own library, exactly like a normal
   Jellyfin setup.

> **No release yet?** If the catalog shows nothing, the repository has no
> tagged build. `scripts/local-repo.sh` builds the same zip and `manifest.json`
> locally; add that folder as a repository instead.

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

- **Jellyfin 12.** Not 10.11 and not 10.10.
- The indexed LeLibrary library (a Postgres-backed LeLibrary instance).
- A debrid account. TorBox is supported first; Real-Debrid, AllDebrid and
  Premiumize follow.

## Docker: set the server URL

Inside the official Jellyfin container, the address Jellyfin reports for itself
is the container's own interface, which a player cannot reach. Fill **Server
URL** in the plugin settings with the address players use, for example
`http://192.168.1.20:8096`. On a native install it can stay empty.

The library folder also has to be a path Jellyfin can see: the default
`/config/lelibrary` lives in the container's config volume, which is fine, but
it is not on a media share.

## Your key is on disk in plain text

Jellyfin stores plugin settings as plain XML. Anyone who can read the config
directory can read your debrid API key. That is true of every debrid plugin, and
this one does not pretend otherwise: protect the host or the disk (file
permissions, a dedicated machine, full-disk encryption).

The same applies to your LeLibrary URL: it contains your setup secret, it is
written into the `.nfo` files as art URLs, and anyone who can read it can read
your library.

## Building

Needs the .NET 10 SDK:

```bash
dotnet build -c Release
dotnet test
```

## Licence

Not chosen yet. Jellyfin itself is GPL-3.0, and a plugin that links against
`Jellyfin.Controller` is normally distributed under GPL-3.0 as well; the licence
will be added before the first release.
