using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Standalone.TorBox.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Standalone.TorBox;

/// <summary>
/// The Standalone plugin: turns a debrid account into Jellyfin media folders.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id. Must match the assembly Guid in AssemblyInfo.cs and never
    /// change after a release.
    /// </summary>
    public static readonly Guid PluginId = new("eedbc5ae-56c4-4145-9a2b-c12716963c95");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Standalone TorBox";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Streams your TorBox library through Jellyfin.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "configPage",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
            },
        };
    }
}
