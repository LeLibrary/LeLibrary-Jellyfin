using System;
using Jellyfin.Plugin.LeLibrary.Model;
using Jellyfin.Plugin.LeLibrary.Sync;
using Xunit;

namespace Jellyfin.Plugin.LeLibrary.Tests;

public class ReconcilerTests
{
    private static Desired Desired(string key, string path) =>
        new(key, path, new PlanItem { Key = key, Kind = "movie", Path = path });

    private static ExistingFile Existing(string key, string path) => new(key, path);

    [Fact]
    public void ANewKeyIsAWrite()
    {
        var plan = Reconciler.Reconcile(
            new[] { Desired("torbox:1:2", "A/A.strm") },
            Array.Empty<ExistingFile>());

        Assert.Single(plan.Writes);
        Assert.Empty(plan.Deletes);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void AMissingKeyIsADelete()
    {
        var plan = Reconciler.Reconcile(
            Array.Empty<Desired>(),
            new[] { Existing("torbox:1:2", "A/A.strm") });

        Assert.Single(plan.Deletes);
        Assert.Empty(plan.Writes);
    }

    [Fact]
    public void ASameKeySamePathIsANoOp()
    {
        var plan = Reconciler.Reconcile(
            new[] { Desired("torbox:1:2", "A/A.strm") },
            new[] { Existing("torbox:1:2", "A/A.strm") });

        Assert.Empty(plan.Writes);
        Assert.Empty(plan.Deletes);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void ASameKeyNewPathIsAMoveNotADeleteAndWrite()
    {
        var plan = Reconciler.Reconcile(
            new[] { Desired("torbox:1:2", "B/B.strm") },
            new[] { Existing("torbox:1:2", "A/A.strm") });

        Assert.Single(plan.Moves);
        Assert.Empty(plan.Writes);
        Assert.Empty(plan.Deletes);
        Assert.Equal("A/A.strm", plan.Moves[0].From.RelativePath);
        Assert.Equal("B/B.strm", plan.Moves[0].To.RelativePath);
    }

    [Fact]
    public void PathCaseChangesAreNotMoves()
    {
        var plan = Reconciler.Reconcile(
            new[] { Desired("torbox:1:2", "a/a.strm") },
            new[] { Existing("torbox:1:2", "A/A.strm") });

        Assert.Empty(plan.Moves);
        Assert.Empty(plan.Writes);
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void DuplicateDesiredPathsAreRejected()
    {
        // LeLibrary collapses copies by path already; a duplicate here means a
        // contract bug, and writing the same file twice is never right.
        Assert.Throws<InvalidOperationException>(() => Reconciler.Reconcile(
            new[] { Desired("torbox:1:2", "A/A.strm"), Desired("torbox:9:9", "A/A.strm") },
            Array.Empty<ExistingFile>()));
    }

    [Fact]
    public void EverythingAtOnce()
    {
        var plan = Reconciler.Reconcile(
            new[]
            {
                Desired("torbox:1:1", "keep/keep.strm"),
                Desired("torbox:2:2", "move/move.strm"),
                Desired("torbox:3:3", "new/new.strm"),
            },
            new[]
            {
                Existing("torbox:1:1", "keep/keep.strm"),
                Existing("torbox:2:2", "old/old.strm"),
                Existing("torbox:4:4", "gone/gone.strm"),
            });

        Assert.Single(plan.Writes);
        Assert.Single(plan.Moves);
        Assert.Single(plan.Deletes);
        Assert.Equal("torbox:3:3", plan.Writes[0].Key);
        Assert.Equal("torbox:4:4", plan.Deletes[0].Key);
    }
}
