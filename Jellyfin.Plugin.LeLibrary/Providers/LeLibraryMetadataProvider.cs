using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeLibrary.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.LeLibrary.Providers;

/// <summary>
/// The metadata document the last sync fetched.
///
/// Jellyfin creates a metadata provider once per library and caches it, so a
/// provider cannot hold a document in a field: the sync puts the new one here
/// and the provider reads it on every call.
/// </summary>
public sealed class MetadataHolder
{
    /// <summary>Gets or sets the current document.</summary>
    public MetadataDocument Document { get; set; } = MetadataDocument.Empty;
}

/// <summary>
/// Applies the LeLibrary metadata to Jellyfin's items.
///
/// This is an <see cref="ICustomMetadataProvider{TItemType}"/>, which Jellyfin
/// runs after its own remote providers, so what LeLibrary knows is what wins.
/// It only runs on a full refresh: Jellyfin filters the provider list down to
/// the change monitors on a partial one, and a custom provider is not one.
/// </summary>
public sealed class LeLibraryMetadataProvider :
    ICustomMetadataProvider<Movie>,
    ICustomMetadataProvider<Series>,
    ICustomMetadataProvider<Episode>
{
    private readonly MetadataHolder _holder;

    /// <summary>Initializes a new instance of the <see cref="LeLibraryMetadataProvider"/> class.</summary>
    /// <param name="holder">The document the sync fetched.</param>
    public LeLibraryMetadataProvider(MetadataHolder holder)
    {
        _holder = holder;
    }

    /// <inheritdoc />
    public string Name => "LeLibrary";

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(Apply(item, MetadataLookup.For(item, _holder.Document)));

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Series item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(Apply(item, MetadataLookup.For(item, _holder.Document)));

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        var title = MetadataLookup.For(item, _holder.Document);
        var metadata = MetadataLookup.EpisodeFor(item, title);
        if (metadata is null)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            item.Name = metadata.Title!;
        }

        item.Overview = metadata.Overview ?? item.Overview;
        item.PremiereDate = Date(metadata.AirDate) ?? item.PremiereDate;
        if (metadata.Runtime is { } minutes)
        {
            item.RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks;
        }

        return Task.FromResult(ItemUpdateType.MetadataEdit);
    }

    private static ItemUpdateType Apply(BaseItem item, MetadataTitle? title)
    {
        if (title is null)
        {
            // The document knows nothing about this item: leave it exactly as
            // it is, and say so, so Jellyfin does not rewrite it for nothing.
            return ItemUpdateType.None;
        }

        item.Overview = title.Overview ?? item.Overview;
        if (title.Genres.Count > 0)
        {
            item.Genres = title.Genres.ToArray();
        }

        if (title.Rating is { } rating)
        {
            item.CommunityRating = (float)rating;
        }

        item.OfficialRating = title.Certification ?? item.OfficialRating;
        item.PremiereDate = Date(title.ReleaseDate) ?? item.PremiereDate;
        if (item.ProductionYear is null && title.Year is { } year)
        {
            item.ProductionYear = year;
        }

        // A film has a runtime; a series does not, and TMDB has none to give.
        if (item is not Series && title.Runtime is { } minutes)
        {
            item.RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks;
        }

        return ItemUpdateType.MetadataEdit;
    }

    private static DateTime? Date(string? value)
        => DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var date)
            ? date
            : null;
}
