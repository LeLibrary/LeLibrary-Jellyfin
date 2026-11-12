using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Standalone.Premiumize.Configuration;

/// <summary>
/// Plugin settings. Jellyfin stores these as plain XML in the plugin
/// configuration folder: the debrid key is readable by anyone who can read that
/// directory. The settings page says so; protecting it is the host's job.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the capability protecting stream URLs.</summary>
    public string StreamSecret { get; set; } = System.Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    /// <summary>
    /// Gets or sets the debrid provider id: torbox, realdebrid, alldebrid or premiumize.
    /// </summary>
    public string Provider { get; set; } = "premiumize";

    /// <summary>
    /// Gets or sets the user's own debrid API key. Never sent to Standalone.
    /// </summary>
    public string DebridApiKey { get; set; } = string.Empty;

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
