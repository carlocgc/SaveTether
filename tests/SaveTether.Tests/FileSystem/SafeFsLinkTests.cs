using SaveTether.Core.FileSystem;

namespace SaveTether.Tests.FileSystem;

public class SafeFsLinkTests
{
    private static readonly EntryKind DefaultLinkKind =
        OperatingSystem.IsWindows() ? EntryKind.Junction : EntryKind.Symlink;

    [Fact]
    public void Probe_reports_each_kind_without_following()
    {
        using var sandbox = new Sandbox();
        var tree = LinkyTree.Build(sandbox);

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(sandbox.At("nothing-here")));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(sandbox.At("no-parent", "nothing-here")));
        Assert.Equal(EntryKind.File, SafeFs.Probe(Path.Combine(tree, "file.txt")));
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(tree));
        Assert.Equal(DefaultLinkKind, SafeFs.Probe(Path.Combine(tree, "link-to-canary")));
        Assert.Equal(DefaultLinkKind, SafeFs.Probe(Path.Combine(tree, "broken")));
        Assert.Equal(DefaultLinkKind, SafeFs.Probe(Path.Combine(tree, "loop")));

        // A trailing separator must not make the probe look through the link.
        Assert.Equal(DefaultLinkKind, SafeFs.Probe(Path.Combine(tree, "link-to-canary") + Path.DirectorySeparatorChar));

        foreach (var name in new[] { "sym-dir", "sym-file" })
        {
            var path = Path.Combine(tree, name);
            if (SafeFs.Probe(path) != EntryKind.Missing)
            {
                Assert.Equal(EntryKind.Symlink, SafeFs.Probe(path));
            }
        }

        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void CreateDirectoryLink_makes_the_platform_default_link_and_reads_back_exactly()
    {
        using var sandbox = new Sandbox();
        var target = sandbox.Dir("target");
        sandbox.FileAt("save", "target", "slot1.sav");
        var link = sandbox.At("link");

        new SafeFs().CreateDirectoryLink(link, target);

        Assert.Equal(DefaultLinkKind, SafeFs.Probe(link));
        Assert.Equal(PathRules.Normalize(target), SafeFs.ReadLinkTarget(link));

        // Independent checks: .NET's own reader agrees, and the OS resolves the link.
        Assert.True(PathRules.AreSame(target, new DirectoryInfo(link).LinkTarget!));
        Assert.Equal("save", File.ReadAllText(Path.Combine(link, "slot1.sav")));
    }

    [Fact]
    public void Junction_is_a_mount_point_not_a_symlink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var target = sandbox.Dir("target");
        var link = sandbox.At("link");

        new SafeFs().CreateDirectoryLink(link, target);

        var info = WindowsReparse.Read(link);
        Assert.Equal(WindowsReparse.TagMountPoint, info.Tag);
        Assert.Equal(PathRules.Normalize(target), info.Target);
        Assert.False(info.IsRelative);
    }

    [Fact]
    public void Windows_own_tool_sees_the_junction_as_a_mount_point_to_the_target()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var target = sandbox.Dir("target");
        var link = sandbox.At("link");
        new SafeFs().CreateDirectoryLink(link, target);

        using var fsutil = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "fsutil.exe",
            ArgumentList = { "reparsepoint", "query", link },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = fsutil.StandardOutput.ReadToEnd();
        fsutil.WaitForExit();

        Assert.Equal(0, fsutil.ExitCode);
        Assert.Contains("0xa0000003", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PathRules.Normalize(target), output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateDirectoryLink_refuses_bad_requests_and_leaves_nothing_behind()
    {
        using var sandbox = new Sandbox();
        var fs = new SafeFs();
        var target = sandbox.Dir("target");
        var occupied = sandbox.Dir("occupied");
        var file = sandbox.FileAt("x", "a-file.txt");

        Assert.Throws<SafeFsException>(() => fs.CreateDirectoryLink(occupied, target));
        Assert.Throws<SafeFsException>(() => fs.CreateDirectoryLink(sandbox.At("l1"), sandbox.At("missing-target")));
        Assert.Throws<SafeFsException>(() => fs.CreateDirectoryLink(sandbox.At("l2"), file));
        Assert.Throws<SafeFsException>(() => fs.CreateDirectoryLink(sandbox.At("no-parent", "l3"), target));
        Assert.Throws<SafeFsException>(() => fs.CreateDirectoryLink("relative-link", target));
        Assert.Throws<SafeFsException>(() => fs.CreateDirectoryLink(sandbox.At("l4"), "relative-target"));

        foreach (var name in new[] { "l1", "l2", "l4" })
        {
            Assert.Equal(EntryKind.Missing, SafeFs.Probe(sandbox.At(name)));
        }

        Assert.Equal(EntryKind.Directory, SafeFs.Probe(occupied));
    }

    [Fact]
    public void RemoveLink_removes_only_the_link()
    {
        using var sandbox = new Sandbox();
        var tree = LinkyTree.Build(sandbox);
        var fs = new SafeFs();

        foreach (var name in LinkyTree.DefaultLinks)
        {
            fs.RemoveLink(Path.Combine(tree, name));
            Assert.Equal(EntryKind.Missing, SafeFs.Probe(Path.Combine(tree, name)));
        }

        foreach (var name in new[] { "sym-dir", "sym-file" })
        {
            var path = Path.Combine(tree, name);
            if (SafeFs.Probe(path) == EntryKind.Symlink)
            {
                fs.RemoveLink(path);
                Assert.Equal(EntryKind.Missing, SafeFs.Probe(path));
            }
        }

        // The tree the cycle pointed at still has its own content.
        Assert.Equal("plain", File.ReadAllText(Path.Combine(tree, "file.txt")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void RemoveLink_refuses_anything_that_is_not_a_link()
    {
        using var sandbox = new Sandbox();
        var fs = new SafeFs();
        var dir = sandbox.Dir("real-dir");
        var file = sandbox.FileAt("x", "real-dir", "file.txt");

        Assert.Throws<SafeFsException>(() => fs.RemoveLink(dir));
        Assert.Throws<SafeFsException>(() => fs.RemoveLink(file));
        Assert.Throws<SafeFsException>(() => fs.RemoveLink(sandbox.At("missing")));

        Assert.Equal("x", File.ReadAllText(file));
    }

    [Fact]
    public void ReadLinkTarget_refuses_a_real_directory()
    {
        using var sandbox = new Sandbox();
        Assert.Throws<SafeFsException>(() => SafeFs.ReadLinkTarget(sandbox.Dir("real-dir")));
    }

    [Fact]
    public void Links_work_below_a_path_longer_than_260_characters()
    {
        using var sandbox = new Sandbox();
        var deep = sandbox.Work;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, "segment-0123456789");
        }

        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "file.txt"), "deep");
        var link = Path.Combine(deep, "link");
        var fs = new SafeFs();

        fs.CreateDirectoryLink(link, sandbox.Canary);
        Assert.Equal(DefaultLinkKind, SafeFs.Probe(link));
        Assert.Equal(PathRules.Normalize(sandbox.Canary), SafeFs.ReadLinkTarget(link));

        fs.DeleteTree(sandbox.At("segment-0123456789"));

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(sandbox.At("segment-0123456789")));
        sandbox.AssertCanaryIntact();
    }
}
