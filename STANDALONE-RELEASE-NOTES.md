# Standalone provider plugins — preview

Install one or more separate Jellyfin plugins for TorBox, Real-Debrid,
AllDebrid and Premiumize. Each connects directly to its provider with your API
key. Jellyfin handles media identification, metadata and artwork; no LeLibrary
account, setup link, backend or metadata service is required.

Jellyfin 12.4 or newer is required. TorBox torrents are included by default;
web downloads and Usenet can be enabled in the plugin's settings when supported
by the account.

This preview has been tested with mocked provider accounts in Jellyfin, but has
not been validated with live accounts. Sync is additive: it does not delete
items removed at the provider. Migration from the separate older
LeLibrary-connected plugin is not automatic. Existing libraries and watched
data are not intentionally removed by the standalone plugins.
