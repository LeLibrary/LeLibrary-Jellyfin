using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.Standalone.TorBox.Tests;

public class ConfigurationPageTests
{
    [Fact]
    public void InstalledProvidersHaveDistinctPagesWithTheirOwnPluginIds()
    {
        var plugins = new (Type Type, Guid Id)[]
        {
            (typeof(Jellyfin.Plugin.Standalone.TorBox.Plugin), Jellyfin.Plugin.Standalone.TorBox.Plugin.PluginId),
            (typeof(Jellyfin.Plugin.Standalone.RealDebrid.Plugin), Jellyfin.Plugin.Standalone.RealDebrid.Plugin.PluginId),
            (typeof(Jellyfin.Plugin.Standalone.AllDebrid.Plugin), Jellyfin.Plugin.Standalone.AllDebrid.Plugin.PluginId),
            (typeof(Jellyfin.Plugin.Standalone.Premiumize.Plugin), Jellyfin.Plugin.Standalone.Premiumize.Plugin.PluginId),
        };
        var pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "configPage" };
        var pageElements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, id) in plugins)
        {
            var plugin = (IHasWebPages)RuntimeHelpers.GetUninitializedObject(type);
            var page = Assert.Single(plugin.GetPages());
            Assert.True(pages.Add(page.Name), $"Duplicate configuration page: {page.Name}");
            using var stream = type.Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream);
            var html = reader.ReadToEnd();
            Assert.Contains(id.ToString(), html, StringComparison.OrdinalIgnoreCase);
            var rootId = Regex.Match(html, "<div id=\"([^\"]+)\" data-role=\"page\"").Groups[1].Value;
            Assert.NotEmpty(rootId);
            Assert.True(pageElements.Add(rootId), $"Duplicate configuration page element: {rootId}");
            Assert.Contains($"document.querySelector('#{rootId}')", html, StringComparison.Ordinal);
        }
    }
}
