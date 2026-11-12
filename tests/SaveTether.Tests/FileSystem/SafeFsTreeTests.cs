using SaveTether.Core.FileSystem;

namespace SaveTether.Tests.FileSystem;

public class SafeFsTreeTests
{
    [Fact]
    public void DeleteTree_removes_a_tree_full_of_links_and_spares_the_canary()
    {
        using var sandbox = new Sandbox();
        var tree = LinkyTree.Build(sandbox);

        new SafeFs().DeleteTree(tree);

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(tree));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void DeleteTree_on_a_link_removes_only_the_link()
    {
        using var sandbox = new Sandbox();
        var link = sandbox.At("link");
        Sandbox.Link(link, sandbox.Canary);

        new SafeFs().DeleteTree(link);

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(link));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void DeleteTree_on_a_missing_path_does_nothing()
    {
        using var sandbox = new Sandbox();
        new SafeFs().DeleteTree(sandbox.At("never-existed"));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void DeleteTree_handles_non_ascii_names()
    {
        using var sandbox = new Sandbox();
        var tree = sandbox.Dir("セーブ データ");
        sandbox.FileAt("x", "セーブ データ", "ñandú — слот.sav");
        Sandbox.Link(Path.Combine(tree, "リンク"), sandbox.Canary);

        new SafeFs().DeleteTree(tree);

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(tree));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void DeleteTree_handles_names_differing_only_by_case()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Such names cannot coexist on a Windows filesystem.
        }

        using var sandbox = new Sandbox();
        var tree = sandbox.Dir("tree");
        sandbox.FileAt("lower", "tree", "savedata", "a.sav");
        sandbox.FileAt("upper", "tree", "SaveData", "a.sav");

        var names = SafeFs.EnumerateTree(tree).Where(e => e.Kind == EntryKind.Directory).Select(e => e.RelativePath);
        Assert.Single(PathRules.FindCaseOnlyCollisions(names));

        new SafeFs().DeleteTree(tree);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(tree));
    }

    [Fact]
    public void DeleteTree_refuses_the_protected_root_unless_explicitly_confirmed()
    {
        using var sandbox = new Sandbox();
        var syncRoot = sandbox.Dir("sync");
        var data = sandbox.Dir("sync", "savetether", "app", "saves");
        var save = sandbox.FileAt("save", "sync", "savetether", "app", "saves", "slot.sav");
        var fs = new SafeFs(syncRoot);

        Assert.Throws<SafeFsException>(() => fs.DeleteTree(data));
        Assert.Throws<SafeFsException>(() => fs.DeleteTree(syncRoot));
        Assert.Throws<SafeFsException>(() => fs.DeleteTree(save));
        Assert.Equal("save", File.ReadAllText(save));

        fs.DeleteTree(data, SharedDataDeletion.ExplicitlyConfirmed);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(data));
    }

    [Fact]
    public void DeleteTree_sees_the_protected_root_through_a_link_in_the_path()
    {
        using var sandbox = new Sandbox();
        var syncRoot = sandbox.Dir("sync");
        var save = sandbox.FileAt("save", "sync", "data", "slot.sav");
        var alias = sandbox.At("alias");
        Sandbox.Link(alias, syncRoot);
        var fs = new SafeFs(syncRoot);

        // The path does not look like it is under the sync root, but it lands there.
        Assert.Throws<SafeFsException>(() => fs.DeleteTree(Path.Combine(alias, "data")));
        Assert.Equal("save", File.ReadAllText(save));

        // The alias itself is a link outside the sync root: removing it leaves the data alone.
        fs.DeleteTree(alias);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(alias));
        Assert.Equal("save", File.ReadAllText(save));
    }

    [Fact]
    public void DeleteTree_refuses_a_folder_that_contains_the_protected_root()
    {
        using var sandbox = new Sandbox();
        var syncRoot = sandbox.Dir("outer", "sync");
        var fs = new SafeFs(syncRoot);

        Assert.Throws<SafeFsException>(() => fs.DeleteTree(sandbox.At("outer")));
        Assert.Throws<SafeFsException>(
            () => fs.DeleteTree(sandbox.At("outer"), SharedDataDeletion.ExplicitlyConfirmed));
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(syncRoot));
    }

    [Fact]
    public void Links_and_renames_are_refused_inside_the_protected_root()
    {
        using var sandbox = new Sandbox();
        var syncRoot = sandbox.Dir("sync");
        var inside = sandbox.Dir("sync", "data");
        var fs = new SafeFs(syncRoot);

        Assert.Throws<SafeFsException>(() => fs.CreateDirectoryLink(Path.Combine(syncRoot, "link"), sandbox.Canary));
        Assert.Throws<SafeFsException>(() => fs.RenameDirectory(inside, Path.Combine(syncRoot, "renamed")));

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(Path.Combine(syncRoot, "link")));
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(inside));
    }

    [Fact]
    public void CopyTree_copies_files_and_recreates_links_without_following_them()
    {
        using var sandbox = new Sandbox();
        var tree = LinkyTree.Build(sandbox);
        var copy = sandbox.At("copy");
        var stamp = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(tree, "file.txt"), stamp);

        var result = SafeFs.CopyTree(tree, copy);

        Assert.Equal("plain", File.ReadAllText(Path.Combine(copy, "file.txt")));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(copy, "file.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(copy, "sub", "a", "b", "c", "deep.txt")));
        Assert.Equal(3, result.FileCount); // file.txt, deep.txt, readonly.txt
        Assert.Equal("plain".Length + "deep".Length + "locked".Length, result.ByteCount);

        // Every link in the source is a link in the copy, pointing where the original pointed.
        var sourceLinks = SafeFs.EnumerateTree(tree).Where(e => e.Kind is EntryKind.Symlink or EntryKind.Junction).ToList();
        Assert.True(sourceLinks.Count >= 4);
        Assert.Equal(sourceLinks.Select(e => e.RelativePath), result.Links.Select(l => l.RelativePath));
        foreach (var link in sourceLinks)
        {
            var copied = Path.Combine(copy, link.RelativePath);
            Assert.Equal(link.Kind, SafeFs.Probe(copied));
            Assert.Equal(
                SafeFs.ReadLinkTarget(Path.Combine(tree, link.RelativePath)),
                SafeFs.ReadLinkTarget(copied));
        }

        // Nothing from the canary was copied in as real files.
        Assert.DoesNotContain(SafeFs.EnumerateTree(copy), e => e.RelativePath.EndsWith("keep.txt", StringComparison.Ordinal));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void CopyTree_can_record_links_without_copying_them()
    {
        using var sandbox = new Sandbox();
        var tree = LinkyTree.Build(sandbox);
        var copy = sandbox.At("copy");

        var result = SafeFs.CopyTree(tree, copy, LinkCopyPolicy.RecordOnly);

        Assert.Contains(result.Links, l => l.RelativePath == "link-to-canary");
        Assert.Contains(result.Links, l => l.RelativePath == "sub/a/b/c/deep-link");
        Assert.DoesNotContain(SafeFs.EnumerateTree(copy), e => e.Kind is EntryKind.Symlink or EntryKind.Junction);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(Path.Combine(copy, "link-to-canary")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void CopyTree_refuses_bad_requests()
    {
        using var sandbox = new Sandbox();
        var tree = sandbox.Dir("tree");
        var file = sandbox.FileAt("x", "tree", "file.txt");
        var existing = sandbox.Dir("existing");
        var link = sandbox.At("link");
        Sandbox.Link(link, sandbox.Canary);

        Assert.Throws<SafeFsException>(() => SafeFs.CopyTree(tree, existing));
        Assert.Throws<SafeFsException>(() => SafeFs.CopyTree(tree, Path.Combine(tree, "inside")));
        Assert.Throws<SafeFsException>(() => SafeFs.CopyTree(file, sandbox.At("from-file")));
        Assert.Throws<SafeFsException>(() => SafeFs.CopyTree(link, sandbox.At("from-link")));
        Assert.Throws<SafeFsException>(() => SafeFs.CopyTree(sandbox.At("missing"), sandbox.At("from-missing")));

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(Path.Combine(tree, "inside")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void RenameDirectory_renames_a_tree_with_links_in_place()
    {
        using var sandbox = new Sandbox();
        var tree = LinkyTree.Build(sandbox);
        var renamed = sandbox.At("tree.savetether-old");

        new SafeFs().RenameDirectory(tree, renamed);

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(tree));
        Assert.Equal("plain", File.ReadAllText(Path.Combine(renamed, "file.txt")));
        Assert.Equal(PathRules.Normalize(sandbox.Canary), SafeFs.ReadLinkTarget(Path.Combine(renamed, "link-to-canary")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void RenameDirectory_refuses_bad_requests()
    {
        using var sandbox = new Sandbox();
        var fs = new SafeFs();
        var tree = sandbox.Dir("tree");
        var other = sandbox.Dir("other");
        var file = sandbox.FileAt("x", "file.txt");
        var link = sandbox.At("link");
        Sandbox.Link(link, sandbox.Canary);

        Assert.Throws<SafeFsException>(() => fs.RenameDirectory(tree, Path.Combine(other, "moved")));
        Assert.Throws<SafeFsException>(() => fs.RenameDirectory(tree, other));
        Assert.Throws<SafeFsException>(() => fs.RenameDirectory(file, sandbox.At("file2.txt")));
        Assert.Throws<SafeFsException>(() => fs.RenameDirectory(link, sandbox.At("link2")));
        Assert.Throws<SafeFsException>(() => fs.RenameDirectory(sandbox.At("missing"), sandbox.At("missing2")));

        Assert.Equal(EntryKind.Directory, SafeFs.Probe(tree));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void EnumerateTree_lists_links_without_entering_them_in_a_stable_order()
    {
        using var sandbox = new Sandbox();
        var tree = sandbox.Dir("tree");
        sandbox.FileAt("12345", "tree", "b.txt");
        sandbox.FileAt("1", "tree", "a", "inner.txt");
        Sandbox.Link(Path.Combine(tree, "c-link"), sandbox.Canary);

        var entries = SafeFs.EnumerateTree(tree).ToList();

        Assert.Equal(["a", "a/inner.txt", "b.txt", "c-link"], entries.Select(e => e.RelativePath));
        Assert.Equal(5, entries.Single(e => e.RelativePath == "b.txt").Length);
        Assert.True(entries.Single(e => e.RelativePath == "c-link").Kind is EntryKind.Symlink or EntryKind.Junction);
        Assert.Throws<SafeFsException>(() => SafeFs.EnumerateTree(Path.Combine(tree, "c-link")).ToList());
    }
}
