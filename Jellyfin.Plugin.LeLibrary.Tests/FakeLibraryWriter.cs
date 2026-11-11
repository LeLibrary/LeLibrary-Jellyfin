using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LeLibrary.Virtual;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LeLibrary.Tests;

/// <summary>
/// An in-memory stand-in for Jellyfin's database. The sync diff is tested
/// against this, not against a Jellyfin server.
/// </summary>
public sealed class FakeLibraryWriter : ILibraryWriter
{
    /// <summary>The rows that exist.</summary>
    public List<BaseItem> Items { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> LoadItems() => Items;

    /// <inheritdoc />
    public Guid NewItemId(string key, Type type)
    {
        // The same derivation Jellyfin uses, so an id in a test is the id the
        // plugin would get in production: the MD5 of the type name and the key.
        var bytes = Encoding.UTF8.GetBytes((type.FullName + key).ToLowerInvariant());
        return new Guid(MD5.HashData(bytes));
    }

    /// <inheritdoc />
    public void CreateItems(IReadOnlyList<BaseItem> items, BaseItem parent)
    {
        foreach (var item in items)
        {
            item.ParentId = parent.Id;
            Items.Add(item);
        }
    }

    /// <inheritdoc />
    public void UpdateItem(BaseItem item)
    {
        var index = Items.FindIndex(row => row.Id == item.Id);
        if (index >= 0)
        {
            Items[index] = item;
        }
    }

    /// <inheritdoc />
    public void DeleteItem(BaseItem item)
    {
        var doomed = new HashSet<Guid> { item.Id };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var row in Items.Where(row => row.ParentId != Guid.Empty && doomed.Contains(row.ParentId)))
            {
                if (doomed.Add(row.Id))
                {
                    grew = true;
                }
            }
        }

        Items.RemoveAll(row => doomed.Contains(row.Id));
    }
}
