using System;
using System.Reflection;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace Jellyfin.Plugin.Standalone.TorBox.Tests;

public class NativeMetadataModeTests
{
    [Theory]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.TorBox.Virtual.NativeMetadataWorker))]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.RealDebrid.Virtual.NativeMetadataWorker))]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.AllDebrid.Virtual.NativeMetadataWorker))]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.Premiumize.Virtual.NativeMetadataWorker))]
    public void InitialRefreshUsesJellyfinsNormalIdentificationAndLaterRefreshesPreserveMetadata(Type workerType)
    {
        var select = workerType.GetMethod("SelectMetadataMode", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(select);
        Assert.Equal(MetadataRefreshMode.Default, select.Invoke(null, new object[] { DateTime.MinValue }));
        Assert.Equal(MetadataRefreshMode.FullRefresh, select.Invoke(null, new object[] { DateTime.UtcNow }));
    }
}
