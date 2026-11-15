# Standalone debrid plugins for Jellyfin

Four separately installable Jellyfin plugins connect directly to **TorBox**,
**Real-Debrid**, **AllDebrid** or **Premiumize**. Each plugin reads its own
provider account and streams through Jellyfin. Jellyfin's configured metadata
providers identify the media and supply descriptions, posters and backdrops;
the standalone plugins do not use a LeLibrary account, setup token, backend or
metadata service.

The standalone packages require **Jellyfin 12.4 or newer**. They have been
exercised in DEV with mocked provider APIs; live provider accounts have not
been tested. Synchronization currently adds and updates entries but does not
remove old ones, and migration from the older LeLibrary-connected plugin has not
been implemented.

## Install and configure

1. Add the repository under **Dashboard → Plugins → Repositories**:
   `https://lelibrary.github.io/LeLibrary-Jellyfin/manifest.json`.
2. Install the standalone plugin for your provider from the catalog and restart
   Jellyfin. Each plugin works without the others.
3. Open that plugin's settings, enter your provider API key and save. In
   Docker, set **Server URL** to an address accessible to your Jellyfin server
   and players. For TorBox, enable web downloads or Usenet if your plan supports
   them and you want those sources included.
4. Test the key and start synchronization. Jellyfin's library settings and
   metadata providers control identification and artwork.

The separate legacy **LeLibrary** plugin remains in the repository for existing
users. It still requires a LeLibrary setup and is not a standalone plugin. The
standalone plugins create their own libraries and do not automatically migrate
or delete existing libraries, watched data or provider files.

Jellyfin stores plugin settings as XML on disk. Protect the server's
configuration directory, which contains the provider API key.

## Issues and feedback

If you encounter a problem, please [open an issue](https://github.com/LeLibrary/LeLibrary-Jellyfin/issues) with your Jellyfin version, provider and relevant logs so we can investigate. Remove API keys and other credentials before sharing logs.

## Development

Build and test with the .NET 10 SDK:

```sh
dotnet test -c Release
scripts/standalone-repo.sh
```

The script creates a local development repository. See
[STANDALONE-DEV.md](STANDALONE-DEV.md) for test evidence and release limitations.
The project is licensed under [GPL-3.0-only](LICENSE).
