using SaveTether.Core.FileSystem;

namespace SaveTether.Tests.FileSystem;

public class SafeFsCopyIntoTests
{
    [Fact]
    public void Content_is_merged_into_an_existing_folder()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("one", "from", "GAME1", "save.bin");
        sandbox.FileAt("two", "from", "GAME2", "deep", "save.bin");
        sandbox.FileAt("top", "from", "top.txt");
        sandbox.Dir("to", "GAME1");
        sandbox.FileAt("kept", "to", "GAME3", "save.bin");

        var result = SafeFs.CopyInto(sandbox.At("from"), sandbox.At("to"));

        Assert.Equal(3, result.FileCount);
        Assert.Equal(
            ["GAME1", "GAME1/save.bin", "GAME2", "GAME2/deep", "GAME2/deep/save.bin", "GAME3", "GAME3/save.bin", "top.txt"],
            SafeFs.EnumerateTree(sandbox.At("to")).Select(e => e.RelativePath));
        Assert.Equal("kept", File.ReadAllText(sandbox.At("to", "GAME3", "save.bin")));
        Assert.Equal("one", File.ReadAllText(sandbox.At("from", "GAME1", "save.bin")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Only_the_named_top_level_entries_are_copied()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("one", "from", "GAME1", "save.bin");
        sandbox.FileAt("two", "from", "GAME2", "save.bin");
        sandbox.FileAt("nested", "from", "GAME2", "GAME1", "inner.bin");
        sandbox.Dir("to");

        SafeFs.CopyInto(sandbox.At("from"), sandbox.At("to"), ["GAME2"]);

        // The filter applies to the top level only: GAME2's own GAME1 folder comes along.
        Assert.Equal(
            ["GAME2", "GAME2/GAME1", "GAME2/GAME1/inner.bin", "GAME2/save.bin"],
            SafeFs.EnumerateTree(sandbox.At("to")).Select(e => e.RelativePath));
    }

    [Fact]
    public void An_existing_file_is_never_overwritten()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("new", "from", "GAME1", "save.bin");
        sandbox.FileAt("existing", "to", "GAME1", "save.bin");

        Assert.ThrowsAny<IOException>(() => SafeFs.CopyInto(sandbox.At("from"), sandbox.At("to")));

        Assert.Equal("existing", File.ReadAllText(sandbox.At("to", "GAME1", "save.bin")));
    }

    [Fact]
    public void Links_in_the_source_are_reported_and_never_followed()
    {
        using var sandbox = new Sandbox();
        var tree = LinkyTree.Build(sandbox);
        sandbox.Dir("to");

        var result = SafeFs.CopyInto(tree, sandbox.At("to"));

        Assert.Contains(result.Links, l => l.RelativePath == "link-to-canary");
        Assert.Contains(result.Links, l => l.RelativePath == "sub/a/b/c/deep-link");
        Assert.DoesNotContain(SafeFs.EnumerateTree(sandbox.At("to")), e => e.Kind is EntryKind.Symlink or EntryKind.Junction);
        Assert.Equal("deep", File.ReadAllText(sandbox.At("to", "sub", "a", "b", "c", "deep.txt")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_destination_must_exist_and_must_not_overlap_the_source()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("one", "from", "save.bin");
        sandbox.Dir("from", "inside");

        Assert.Throws<SafeFsException>(() => SafeFs.CopyInto(sandbox.At("from"), sandbox.At("missing")));
        Assert.Throws<SafeFsException>(() => SafeFs.CopyInto(sandbox.At("from"), sandbox.At("from", "inside")));
        Assert.Throws<SafeFsException>(() => SafeFs.CopyInto(sandbox.At("from", "save.bin"), sandbox.At("from", "inside")));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(sandbox.At("missing")));
    }

    [Fact]
    public void A_flushed_atomic_write_replaces_the_file_and_leaves_no_temporary_file()
    {
        using var sandbox = new Sandbox();
        var path = sandbox.FileAt("old", "local", "journal.json");

        SafeFs.WriteFileAtomically(path, "new content\n", flushToDisk: true);

        Assert.Equal("new content\n", File.ReadAllText(path));
        Assert.Equal([0x6E, 0x65, 0x77], File.ReadAllBytes(path).Take(3));
        Assert.Equal(["journal.json"], Directory.GetFileSystemEntries(sandbox.At("local")).Select(Path.GetFileName));
    }
}
