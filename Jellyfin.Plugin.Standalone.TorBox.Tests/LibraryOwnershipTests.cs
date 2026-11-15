using System;
using System.IO;
using System.Reflection;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Standalone.TorBox.Tests;

public class LibraryOwnershipTests
{
    [Theory]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.TorBox.Virtual.VirtualLibrary))]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.RealDebrid.Virtual.VirtualLibrary))]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.AllDebrid.Virtual.VirtualLibrary))]
    [InlineData(typeof(Jellyfin.Plugin.Standalone.Premiumize.Virtual.VirtualLibrary))]
    public void OnlyTheExpectedSingleFolderAndTypeCanBeReused(Type libraryType)
    {
        var path = Path.Combine(Path.GetTempPath(), "standalone-owned", "movies");
        var check = libraryType.GetMethod("IsOwnedLibrary", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(check);
        bool Owned(VirtualFolderInfo folder) => (bool)check.Invoke(null, new object[] { folder, path, CollectionTypeOptions.movies })!;

        Assert.True(Owned(new VirtualFolderInfo { CollectionType = CollectionTypeOptions.movies, Locations = new[] { path } }));
        Assert.False(Owned(new VirtualFolderInfo { CollectionType = CollectionTypeOptions.movies, Locations = new[] { path + "-other" } }));
        Assert.False(Owned(new VirtualFolderInfo { CollectionType = CollectionTypeOptions.movies, Locations = new[] { path, path + "-other" } }));
        Assert.False(Owned(new VirtualFolderInfo { CollectionType = CollectionTypeOptions.tvshows, Locations = new[] { path } }));
    }
}
