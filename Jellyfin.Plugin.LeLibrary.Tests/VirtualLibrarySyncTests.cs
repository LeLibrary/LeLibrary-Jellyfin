using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

/// <summary>
/// The sync diff: what a sync creates, updates, keeps and removes. The
/// database is faked, so these tests need no Jellyfin server.
/// </summary>
public class VirtualLibrarySyncTests
{
    private static VirtualLibrary NewLibrary(ILibraryWriter writer)
        => new(
            null!,
            ProviderManager(),
            writer,
            // The diff never touches the file system; Jellyfin's directory
            // service only carries it.
            null!,
            NullLogger<VirtualLibrary>.Instance);

    private static Folder MoviesFolder() => new() { Id = Guid.NewGuid(), Name = "Movies" };

    private static Folder ShowsFolder() => new() { Id = Guid.NewGuid(), Name = "Shows" };

    private static VirtualLibraryPlan PlanWithOneMovie()
    {
        var movie = new Movie
        {
            Name = "The Matrix",
            ProductionYear = 1999,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [VirtualIdentity.ProviderId] = "torbox:1:2",
            },
        };

        return new VirtualLibraryPlan(
            new[] { new VirtualItem("The Matrix (1999)", null, "torbox:1:2", "The Matrix.mkv", movie, "torbox:1:2") },
            Array.Empty<VirtualItem>(),
            Array.Empty<VirtualItem>(),
            Array.Empty<VirtualItem>(),
            0);
    }

    [Fact]
    public async Task AFreshLibraryCreatesEveryItem()
    {
        var writer = new FakeLibraryWriter();
        var library = NewLibrary(writer);
        var result = await library.ApplyAsync(PlanWithOneMovie(), MoviesFolder(), ShowsFolder(), "http://host:8096", default);
        Assert.Equal(1, result.Added);
    }

    /// <summary>
    /// A provider manager that only answers the one call the sync makes.
    /// </summary>
    private static IProviderManager ProviderManager()
        => DispatchProxy.Create<IProviderManager, ProviderManagerProxy>();
}

/// <summary>
/// Answers <c>QueueRefresh</c> and nothing else: the sync must not depend on
/// Jellyfin's provider machinery to be tested.
/// </summary>
public class ProviderManagerProxy : DispatchProxy
{
    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == "QueueRefresh")
        {
            return null;
        }

        throw new NotSupportedException(targetMethod?.Name ?? "unknown");
    }
}
