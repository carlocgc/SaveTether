using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Tests.Execution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Planning;

/// <summary>
/// Entry depth (design 5.13): the comparison and "add missing" work in units that sit a set
/// number of folder levels down, because not every app keeps one game per top-level entry.
/// The trees here are shaped like Cemu's save folder, where that was found.
/// </summary>
public class EntryDepthTests
{
    private static readonly TreeFacts Cemu = Snap.Tree(
        ("00050000/101c9500/user/80000001/game.sav", 10, "a"),
        ("00050000/10143500/user/80000001/game.sav", 20, "b"),
        ("00050010/1004a000/data.bin", 5, "c"),
        ("system/act/account.dat", 1, "d"),
        ("PlayStats.dat", 2, "e"));

    [Fact]
    public void Entries_are_the_paths_that_many_levels_down()
    {
        Assert.Equal(["00050000", "00050010", "PlayStats.dat", "system"], Cemu.EntryNames(1));
        Assert.Equal(Cemu.TopLevelNames, Cemu.EntryNames(1));
        Assert.Equal(
            ["00050000/10143500", "00050000/101c9500", "00050010/1004a000", "PlayStats.dat", "system/act"],
            Cemu.EntryNames(2));
        Assert.Equal(
            ["00050000/10143500/user", "00050000/101c9500/user", "00050010/1004a000/data.bin", "PlayStats.dat", "system/act/account.dat"],
            Cemu.EntryNames(3));
    }

    [Fact]
    public void A_file_or_an_empty_folder_above_the_depth_is_its_own_entry_and_a_folder_with_content_is_not()
    {
        var tree = new TreeFacts(
        [
            new FileFact("loose.sav", EntryKind.File, 1, Snap.Older),
            new FileFact("empty", EntryKind.Directory, 0, default),
            new FileFact("full", EntryKind.Directory, 0, default),
            new FileFact("full/game", EntryKind.Directory, 0, default),
            new FileFact("full/game/save.bin", EntryKind.File, 1, Snap.Older),
            new FileFact("link", EntryKind.Symlink, 0, default),
        ]);

        Assert.Equal(["empty", "full/game", "link", "loose.sav"], tree.EntryNames(2));
        Assert.Equal(["empty", "full/game/save.bin", "link", "loose.sav"], tree.EntryNames(3));
    }

    [Theory]
    [InlineData("a/b/c", 1, "a")]
    [InlineData("a/b/c", 2, "a/b")]
    [InlineData("a/b/c", 3, "a/b/c")]
    [InlineData("a/b/c", 5, "a/b/c")]
    [InlineData("loose.sav", 2, "loose.sav")]
    public void The_entry_of_a_path_is_its_first_parts(string path, int depth, string entry)
    {
        Assert.Equal(entry, TreeFacts.EntryOf(path, depth));
    }

    [Fact]
    public void Being_under_an_entry_means_whole_path_parts_not_a_shared_prefix()
    {
        Assert.True(TreeFacts.IsUnder("GAME1", "GAME1"));
        Assert.True(TreeFacts.IsUnder("GAME1/save.bin", "GAME1"));
        Assert.False(TreeFacts.IsUnder("GAME10/save.bin", "GAME1"));
        Assert.False(TreeFacts.IsUnder("GAME", "GAME1"));
        Assert.False(TreeFacts.IsUnder("game1/save.bin", "GAME1"));
        Assert.True(TreeFacts.IsUnder("game1/save.bin", "GAME1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void At_depth_1_every_game_looks_present_on_both_sides_and_at_depth_2_they_are_told_apart()
    {
        var local = Snap.Tree(
            ("00050000/101c9500/game.sav", 10, "same"),
            ("00050000/aaaa0000/game.sav", 10, null),
            ("00050000/cccc0000/game.sav", 10, null));
        var shared = Snap.Tree(
            ("00050000/101c9500/game.sav", 10, "same"),
            ("00050000/bbbb0000/game.sav", 10, null),
            ("00050000/cccc0000/game.sav", 11, null));

        // What the Cemu trial ran into: one entry, present on both sides, nothing to add.
        var shallow = Comparison.Build(local, shared);
        Assert.Equal([("00050000", EntryPresence.Different)], shallow.Entries.Select(e => (e.Name, e.Presence)));
        Assert.Empty(shallow.OnlyLocalEntries);

        var deep = Comparison.Build(local, shared, entryDepth: 2);
        Assert.Equal(
            [
                ("00050000/101c9500", EntryPresence.Identical), ("00050000/aaaa0000", EntryPresence.OnlyLocal),
                ("00050000/bbbb0000", EntryPresence.OnlyShared), ("00050000/cccc0000", EntryPresence.Different),
            ],
            deep.Entries.Select(e => (e.Name, e.Presence)));
        Assert.Equal(["00050000/aaaa0000"], deep.OnlyLocalEntries);
        Assert.Equal(
            [new FileDifference("00050000/cccc0000/game.sav", FileDifferenceKind.Different, 10, 11)],
            deep.Entries.Single(e => e.Name == "00050000/cccc0000").Files);
    }

    [Fact]
    public void Depth_3_compares_one_level_further_down()
    {
        var local = Snap.Tree(("a/b/one/x.sav", 1, null), ("a/b/two/x.sav", 1, "h"));
        var shared = Snap.Tree(("a/b/two/x.sav", 1, "h"), ("a/b/three/x.sav", 1, null));

        var comparison = Comparison.Build(local, shared, entryDepth: 3);

        Assert.Equal(
            [("a/b/one", EntryPresence.OnlyLocal), ("a/b/three", EntryPresence.OnlyShared), ("a/b/two", EntryPresence.Identical)],
            comparison.Entries.Select(e => (e.Name, e.Presence)));
    }

    [Fact]
    public void Presence_ignores_letter_case_at_every_level()
    {
        var local = Snap.Tree(("Saves/Game1/x.sav", 1, null), ("Saves/GAME2/x.sav", 1, null));
        var shared = Snap.Tree(("saves/GAME1/x.sav", 1, null));

        var comparison = Comparison.Build(local, shared, entryDepth: 2);

        // On Windows "Saves/Game1" and "saves/GAME1" are one folder, so it is not something to add.
        Assert.Equal(["Saves/GAME2"], comparison.OnlyLocalEntries);
    }

    [Fact]
    public void A_folder_that_is_empty_on_one_side_and_holds_entries_on_the_other_is_not_an_entry_itself()
    {
        var local = Snap.FoldersOnly("00050000");
        var shared = Snap.Tree(("00050000/101c9500/game.sav", 10, null));

        var comparison = Comparison.Build(local, shared, entryDepth: 2);

        Assert.Equal([("00050000/101c9500", EntryPresence.OnlyShared)], comparison.Entries.Select(e => (e.Name, e.Presence)));
        Assert.Empty(comparison.OnlyLocalEntries);
    }

    [Fact]
    public void A_file_where_the_other_side_has_a_folder_is_never_something_to_add()
    {
        var local = Snap.Tree(("00050000/101c9500/game.sav", 10, null));
        var shared = Snap.Tree(("00050000", 3, null));

        var comparison = Comparison.Build(local, shared, entryDepth: 2);

        Assert.Empty(comparison.OnlyLocalEntries);
    }

    [Fact]
    public void Add_missing_at_depth_2_plans_to_copy_one_game_and_only_that()
    {
        var snapshot = Snap.Base() with
        {
            EntryDepth = 2,
            SourceTree = Snap.Tree(("00050000/101c9500/game.sav", 100, null), ("00050000/aaaa0000/game.sav", 30, null)),
            TargetKind = EntryKind.Directory,
            TargetTree = Snap.Tree(("00050000/101c9500/game.sav", 100, null), ("00050000/bbbb0000/game.sav", 50, null)),
        };

        var plan = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.AddMissing)).Plan!;

        var copy = plan.Steps.Single(s => s.Kind == StepKind.CopyIntoTarget);
        Assert.Equal(["00050000/aaaa0000"], copy.Entries);
        Assert.Contains(plan.Notes, n => n.Contains("00050000/aaaa0000", StringComparison.Ordinal));
        Assert.Contains(plan.Notes, n => n.Contains("the shared version is kept", StringComparison.Ordinal) && n.Contains("00050000/101c9500", StringComparison.Ordinal));

        // Free space is judged on what would really be copied: one game, 30 bytes.
        Assert.True(Planner.Plan(snapshot with { FreeBytesAtTarget = 30 }, new ApplyRequest(SetupChoice.AddMissing)).IsPlanned);
        Assert.Equal(
            [PrecheckKind.NotEnoughSpace],
            Planner.Plan(snapshot with { FreeBytesAtTarget = 29 }, new ApplyRequest(SetupChoice.AddMissing)).Refusal!.Failures.Select(f => f.Kind));

        // The same trees at depth 1 have nothing to add.
        var shallow = Planner.Plan(snapshot with { EntryDepth = 1 }, new ApplyRequest(SetupChoice.AddMissing)).Plan!;
        Assert.DoesNotContain(shallow.Steps, s => s.Kind == StepKind.CopyIntoTarget);
    }

    [Fact]
    public void A_mapping_at_depth_1_is_written_as_format_1_and_a_deeper_one_as_format_2()
    {
        using var sandbox = new Sandbox();
        var store = new SharedStore(sandbox.Dir("sync"));
        store.CreateRoot();
        string File1() => File.ReadAllText(Path.Combine(store.MetaRoot, "mappings", "cemu.save.json"));
        MappingFile Mapping(int? depth) => new()
        {
            Schema = MappingFile.CurrentSchema, Id = "cemu.save", Target = "cemu/save", Mechanism = Mechanism.Link, EntryDepth = depth,
        };

        // An older SaveTether on another device can still read this one.
        store.WriteMapping(Mapping(null));
        Assert.Contains("\"schema\": 1", File1(), StringComparison.Ordinal);
        Assert.DoesNotContain("entry_depth", File1(), StringComparison.Ordinal);
        Assert.Equal(1, store.ReadMapping("cemu.save").Depth);

        store.WriteMapping(Mapping(1));
        Assert.DoesNotContain("entry_depth", File1(), StringComparison.Ordinal);

        // The depth of an existing mapping may be changed; its target may not.
        store.WriteMapping(Mapping(2));
        Assert.Contains("\"schema\": 2", File1(), StringComparison.Ordinal);
        Assert.Contains("\"entry_depth\": 2", File1(), StringComparison.Ordinal);
        Assert.Equal(2, store.ReadMapping("cemu.save").Depth);
        Assert.Equal(2, store.ListMappings().Single().Depth);
        var error = Assert.Throws<StoreException>(() => store.WriteMapping(Mapping(2) with { Target = "cemu/other" }));
        Assert.Equal(StoreProblem.Conflict, error.Problem);

        store.WriteMapping(Mapping(1));
        Assert.Contains("\"schema\": 1", File1(), StringComparison.Ordinal);

        Assert.Equal(StoreProblem.Invalid, Assert.Throws<StoreException>(() => store.WriteMapping(Mapping(0))).Problem);
        Assert.Equal(StoreProblem.Invalid, Assert.Throws<StoreException>(() => store.WriteMapping(Mapping(9))).Problem);
    }

    [Fact]
    public void Copying_named_entries_creates_their_parent_folders_and_touches_nothing_else()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("new game", "from", "00050000", "aaaa0000", "user", "game.sav");
        sandbox.FileAt("not asked for", "from", "00050000", "101c9500", "game.sav");
        sandbox.FileAt("loose", "from", "PlayStats.dat");
        sandbox.FileAt("theirs", "to", "00050000", "101c9500", "game.sav");

        var result = SafeFs.CopyInto(sandbox.At("from"), sandbox.At("to"), ["00050000/aaaa0000", "PlayStats.dat", "00050010/absent"]);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(
            [
                "00050000", "00050000/101c9500", "00050000/101c9500/game.sav", "00050000/aaaa0000", "00050000/aaaa0000/user",
                "00050000/aaaa0000/user/game.sav", "00050010", "PlayStats.dat",
            ],
            SafeFs.EnumerateTree(sandbox.At("to")).Select(e => e.RelativePath));
        Assert.Equal("theirs", File.ReadAllText(sandbox.At("to", "00050000", "101c9500", "game.sav")));
        sandbox.AssertCanaryIntact();
    }

    [Theory]
    [InlineData("00050000/101c9500")]
    [InlineData("00050000/101C9500")]
    [InlineData("../outside")]
    [InlineData("00050000//x")]
    [InlineData("00050000/.")]
    [InlineData("blocked/game")]
    public void A_named_entry_that_exists_or_is_not_a_path_inside_the_folder_is_refused(string entry)
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("mine", "from", "00050000", "101c9500", "game.sav");
        sandbox.FileAt("mine", "from", "00050000", "101C9500", "game.sav");
        sandbox.FileAt("mine", "from", "blocked", "game", "x.sav");
        sandbox.FileAt("outside", "outside", "x.sav");
        sandbox.FileAt("theirs", "to", "00050000", "101c9500", "game.sav");
        sandbox.FileAt("a file where a folder would go", "to", "blocked");

        Assert.Throws<SafeFsException>(() => SafeFs.CopyInto(sandbox.At("from"), sandbox.At("to"), [entry]));

        Assert.Equal("theirs", File.ReadAllText(sandbox.At("to", "00050000", "101c9500", "game.sav")));
        Assert.Equal(
            ["00050000", "00050000/101c9500", "00050000/101c9500/game.sav", "blocked"],
            SafeFs.EnumerateTree(sandbox.At("to")).Select(e => e.RelativePath));
    }

    [Fact]
    public void A_parent_folder_that_differs_only_by_letter_case_is_refused_where_case_matters()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("mine", "from", "Saves", "GAME2", "x.sav");
        sandbox.FileAt("theirs", "to", "saves", "GAME1", "x.sav");

        if (OperatingSystem.IsWindows())
        {
            // One folder here whatever the case, so the entry simply lands in it.
            SafeFs.CopyInto(sandbox.At("from"), sandbox.At("to"), ["Saves/GAME2"]);
            Assert.Equal(["saves"], Directory.GetFileSystemEntries(sandbox.At("to")).Select(Path.GetFileName));
            Assert.Equal("mine", File.ReadAllText(sandbox.At("to", "saves", "GAME2", "x.sav")));
        }
        else
        {
            var error = Assert.Throws<SafeFsException>(() => SafeFs.CopyInto(sandbox.At("from"), sandbox.At("to"), ["Saves/GAME2"]));
            Assert.Contains("differ only by letter case", error.Message, StringComparison.Ordinal);
            Assert.Equal(["saves"], Directory.GetFileSystemEntries(sandbox.At("to")).Select(Path.GetFileName));
        }
    }

    [Fact]
    public void Add_missing_at_depth_2_adds_a_game_and_leaves_the_shared_ones_untouched()
    {
        using var world = new ExecWorld(entryDepth: 2);
        world.LocalSave("00050000/101c9500/game.sav", "mine");
        world.LocalSave("00050000/aaaa0000/user/game.sav", "only here");
        world.LocalSave("system/act/account.dat", "local account");
        world.SharedSave("00050000/101c9500/game.sav", "theirs");
        world.SharedSave("00050000/bbbb0000/game.sav", "only there");
        world.SharedSave("system/act/account.dat", "shared account");
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(world.Target, "00050000", "101c9500", "game.sav"));

        var result = world.Apply(new ApplyRequest(SetupChoice.AddMissing));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["00050000/101c9500/game.sav"] = "theirs",
                ["00050000/aaaa0000/user/game.sav"] = "only here",
                ["00050000/bbbb0000/game.sav"] = "only there",
                ["system/act/account.dat"] = "shared account",
            },
            ExecWorld.Files(world.Target));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(world.Target, "00050000", "101c9500", "game.sav")));
        Assert.True(ExecWorld.IsLink(world.Source));
        Assert.Equal("mine", ExecWorld.Files(world.Backups.DataPathOf(Assert.Single(result.BackupIds)))["00050000/101c9500/game.sav"]);
        world.Sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_entry_that_appeared_in_the_shared_folder_after_planning_stops_the_copy_before_it_starts()
    {
        using var world = new ExecWorld(entryDepth: 2);
        world.LocalSave("00050000/aaaa0000/game.sav", "mine");
        world.LocalSave("00050000/cccc0000/game.sav", "also mine");
        world.SharedSave("00050000/bbbb0000/game.sav", "theirs");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.AddMissing)));

        // The sync tool delivers that game, under a name differing only by case, before the plan runs.
        world.SharedSave("00050000/CCCC0000/game.sav", "arrived meanwhile");
        var result = world.Executor().Run(plan);

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        Assert.Contains("00050000/cccc0000", result.Error, StringComparison.Ordinal);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["00050000/CCCC0000/game.sav"] = "arrived meanwhile", ["00050000/bbbb0000/game.sav"] = "theirs",
            },
            ExecWorld.Files(world.Target));
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(world.Source));
    }
}
