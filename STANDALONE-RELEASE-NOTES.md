# LeLibrary provider plugins

**LeLibrary RD 0.0.4:** Corrects pagination when the reported torrent total has
already been received and adds more specific errors when a listing fails. The
other provider plugins remain on their existing versions.

Install one or more separate Jellyfin plugins for TorBox, Real-Debrid,
AllDebrid and Premiumize. Each connects directly to its provider with your API
key. Jellyfin handles media identification, metadata and artwork; no LeLibrary
account, setup link, backend or metadata service is required.

Jellyfin 12.4 or newer is required. TorBox torrents are included by default;
web downloads and Usenet can be enabled in the plugin's settings when supported
by the account.

The plugins have been tested with mocked provider accounts in Jellyfin, but have
not been validated with live accounts. Sync is additive: it does not delete
items removed at the provider. Migration from the separate older
LeLibrary-connected plugin is not automatic. Existing libraries and watched
data are not intentionally removed by the provider plugins.

If you run into an issue, please [open an issue](https://github.com/LeLibrary/LeLibrary-Jellyfin/issues) and include your Jellyfin version, provider and relevant logs so we can investigate. Remove API keys and other credentials from logs before sharing them.
