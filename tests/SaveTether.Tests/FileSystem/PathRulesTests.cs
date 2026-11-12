using SaveTether.Core.FileSystem;

namespace SaveTether.Tests.FileSystem;

public class PathRulesTests
{
    [Fact]
    public void IsSameOrInside_is_true_for_the_root_and_anything_below_it()
    {
        using var sandbox = new Sandbox();
        var root = sandbox.At("root");

        Assert.True(PathRules.IsSameOrInside(root, root));
        Assert.True(PathRules.IsSameOrInside(root + Path.DirectorySeparatorChar, root));
        Assert.True(PathRules.IsSameOrInside(Path.Combine(root, "a", "b"), root));
        Assert.True(PathRules.IsSameOrInside(Path.Combine(root, "a", "..", "b"), root));
    }

    [Fact]
    public void IsSameOrInside_is_false_for_siblings_parents_and_name_prefixes()
    {
        using var sandbox = new Sandbox();
        var root = sandbox.At("root");

        Assert.False(PathRules.IsSameOrInside(sandbox.Work, root));
        Assert.False(PathRules.IsSameOrInside(sandbox.At("other"), root));
        Assert.False(PathRules.IsSameOrInside(root + "-backup", root));
        Assert.False(PathRules.IsSameOrInside(Path.Combine(root, "..", "other"), root));
    }

    [Fact]
    public void Case_matters_only_where_the_platform_says_so()
    {
        using var sandbox = new Sandbox();
        var lower = sandbox.At("root", "savedata");
        var upper = sandbox.At("ROOT", "SaveData");

        Assert.Equal(OperatingSystem.IsWindows(), PathRules.AreSame(lower, upper));
        Assert.Equal(OperatingSystem.IsWindows(), PathRules.IsSameOrInside(upper, sandbox.At("root")));
    }

    [Fact]
    public void ResolveParents_follows_links_in_the_folders_but_not_the_final_entry()
    {
        using var sandbox = new Sandbox();
        var real = sandbox.Dir("real");
        sandbox.Dir("real", "inner");
        var alias = sandbox.At("alias");
        Sandbox.Link(alias, real);
        var innerLink = Path.Combine(real, "inner-link");
        Sandbox.Link(innerLink, Path.Combine(real, "inner"));

        // A link in the middle of the path is resolved.
        Assert.True(PathRules.AreSame(Path.Combine(real, "inner"), PathRules.ResolveParents(Path.Combine(alias, "inner"))));

        // The final entry is left alone even when it is a link...
        Assert.True(PathRules.AreSame(innerLink, PathRules.ResolveParents(Path.Combine(alias, "inner-link"))));
        Assert.True(PathRules.AreSame(alias, PathRules.ResolveParents(alias)));

        // ...unless everything is to be resolved.
        Assert.True(PathRules.AreSame(Path.Combine(real, "inner"), PathRules.ResolveAll(Path.Combine(alias, "inner-link"))));
        Assert.True(PathRules.AreSame(real, PathRules.ResolveAll(alias)));

        // Parts that do not exist are kept as written.
        Assert.True(PathRules.AreSame(Path.Combine(real, "nope", "x"), PathRules.ResolveAll(Path.Combine(alias, "nope", "x"))));
    }

    [Fact]
    public void Resolve_gives_up_on_a_link_cycle()
    {
        using var sandbox = new Sandbox();
        var a = sandbox.Dir("a");
        var b = sandbox.Dir("b");
        var fs = new SafeFs();

        // a/to-b -> b and b/to-a -> a, then walk round and round.
        fs.CreateDirectoryLink(Path.Combine(a, "to-b"), b);
        fs.CreateDirectoryLink(Path.Combine(b, "to-a"), a);
        var looping = a;
        for (var i = 0; i < 30; i++)
        {
            looping = Path.Combine(looping, "to-b", "to-a");
        }

        Assert.Throws<SafeFsException>(() => PathRules.ResolveAll(Path.Combine(looping, "x")));
    }

    [Fact]
    public void FindCaseOnlyCollisions_groups_names_that_differ_only_by_case()
    {
        var collisions = PathRules.FindCaseOnlyCollisions(["savedata", "SaveData", "other", "SAVEDATA", "other"]);

        var group = Assert.Single(collisions);
        Assert.Equal(["SAVEDATA", "SaveData", "savedata"], group);
        Assert.Empty(PathRules.FindCaseOnlyCollisions(["a", "b", "a"]));
    }
}
