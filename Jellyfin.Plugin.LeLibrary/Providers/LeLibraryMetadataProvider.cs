using System;
using System.Globalization;
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
        => Task.FromResult(Apply(item, Find(item, "movie")));

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Series item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(Apply(item, Find(item, "tv")));

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(ApplyEpisode(item, Find(item, "tv")));

    private MetadataTitle? Find(BaseItem item, string kind)
        => _holder.Document.Find(
            kind,
            Number(item, "Tmdb"),
            Text(item, "Imdb"),
            Text(item, "Tvdb"));

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

    private static ItemUpdateType ApplyEpisode(Episode episode, MetadataTitle? title)
    {
        if (title is null || episode.ParentIndexNumber is not { } season || episode.IndexNumber is not { } number)
        {
            return ItemUpdateType.None;
        }

        var key = season.ToString(CultureInfo.InvariantCulture) + ":" + number.ToString(CultureInfo.InvariantCulture);
        if (!title.Episodes.TryGetValue(key, out var metadata))
        {
            return ItemUpdateType.None;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            episode.Name = metadata.Title!;
        }

        episode.Overview = metadata.Overview ?? episode.Overview;
        episode.PremiereDate = Date(metadata.AirDate) ?? episode.PremiereDate;
        if (metadata.Runtime is { } minutes)
        {
            episode.RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks;
        }

        return ItemUpdateType.MetadataEdit;
    }

    private static int? Number(BaseItem item, string provider)
        => item.ProviderIds is not null
            && item.ProviderIds.TryGetValue(provider, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : null;

    private static string? Text(BaseItem item, string provider)
        => item.ProviderIds is not null && item.ProviderIds.TryGetValue(provider, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;

    private static DateTime? Date(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}
