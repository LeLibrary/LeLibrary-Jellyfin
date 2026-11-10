using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LeLibrary.Configuration;

/// <summary>
/// Plugin settings. Jellyfin stores these as plain XML in the plugin
/// configuration folder: the debrid key is readable by anyone who can read that
/// directory. The settings page says so; protecting it is the host's job.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the debrid provider id: torbox, realdebrid, alldebrid or premiumize.
    /// </summary>
    public string Provider { get; set; } = "torbox";

    /// <summary>
    /// Gets or sets the user's own debrid API key. Never sent to LeLibrary.
    /// </summary>
    public string DebridApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the setup install URL the plan is read from.
    /// </summary>
    public string PlanUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the folder the .strm and .nfo files are written to.
    /// </summary>
    public string LibraryPath { get; set; } = "/config/lelibrary";

    /// <summary>
    /// Gets or sets the URL players should use for this server. Empty means the
    /// address Jellyfin reports for itself, which in Docker is the container's
    /// own interface and usually unreachable from a player.
    /// </summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the background sync interval in hours.
    /// </summary>
    public int SyncHours { get; set; } = 6;
}
