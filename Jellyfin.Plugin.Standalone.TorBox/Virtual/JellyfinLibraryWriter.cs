using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Standalone.TorBox.Virtual;

/// <summary>
/// The library writer over Jellyfin's own database.
/// </summary>
public sealed class JellyfinLibraryWriter : ILibraryWriter
{
    private readonly ILibraryManager _library;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLibraryWriter"/> class.</summary>
    /// <param name="library">Jellyfin's library manager.</param>
    public JellyfinLibraryWriter(ILibraryManager library)
    {
        _library = library;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> LoadItems()
        => _library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Episode,
                Jellyfin.Data.Enums.BaseItemKind.Series,
                Jellyfin.Data.Enums.BaseItemKind.Season,
            },
        });

    /// <inheritdoc />
    public Guid NewItemId(string key, Type type) => _library.GetNewItemId(key, type);

    /// <inheritdoc />
    public void CreateItems(IReadOnlyList<BaseItem> items, BaseItem parent)
        => _library.CreateItems(items, parent, CancellationToken.None);

    /// <inheritdoc />
    public void UpdateItem(BaseItem item)
        => item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public void DeleteItem(BaseItem item)
        => _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false }, true);
}
