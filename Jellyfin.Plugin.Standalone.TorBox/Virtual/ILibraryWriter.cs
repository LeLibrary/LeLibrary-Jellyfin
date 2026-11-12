using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Standalone.TorBox.Virtual;

/// <summary>
/// The handful of database operations the sync diff performs.
///
/// The diff used to live behind <c>ILibraryManager</c>, a hundred-method
/// interface that cannot be faked, so the diff could not be tested at all.
/// These five members are everything the diff touches.
/// </summary>
public interface ILibraryWriter
{
    /// <summary>Every movie, episode, series and season in the server.</summary>
    /// <returns>The rows. The diff filters them.</returns>
    IReadOnlyList<BaseItem> LoadItems();

    /// <summary>The id a row with this key would get.</summary>
    /// <param name="key">The stable key of the item.</param>
    /// <param name="type">The item's type.</param>
    /// <returns>The id.</returns>
    Guid NewItemId(string key, Type type);

    /// <summary>Writes a batch of new items under one parent.</summary>
    /// <param name="items">The items.</param>
    /// <param name="parent">Their parent.</param>
    void CreateItems(IReadOnlyList<BaseItem> items, BaseItem parent);

    /// <summary>Saves one item.</summary>
    /// <param name="item">The item.</param>
    void UpdateItem(BaseItem item);

    /// <summary>Removes one item and its children.</summary>
    /// <param name="item">The item.</param>
    void DeleteItem(BaseItem item);
}
