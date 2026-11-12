using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using static SaveTether.Core.Planning.StepKind;

namespace SaveTether.Tests.Planning;

public class ClassificationTests
{
    [Fact]
    public void Row_1_a_link_to_the_target_is_already_applied()
    {
        Assert.Equal(Classification.AlreadyApplied, Planner.Classify(Snap.LinkedTo(Snap.Target)));
    }

    [Fact]
    public void A_link_to_a_missing_target_is_broken()
    {
        Assert.Equal(Classification.BrokenLink, Planner.Classify(Snap.LinkedTo(Snap.Target, EntryKind.Missing)));
    }

    [Fact]
    public void Row_2_a_link_elsewhere_is_foreign()
    {
        Assert.Equal(Classification.ForeignLink, Planner.Classify(Snap.LinkedTo(Snap.P("old-sync", "PPSSPP"))));
        Assert.Equal(Classification.ForeignLink, Planner.Classify(Snap.LinkedTo(Snap.P("old-sync", "PPSSPP"), EntryKind.Missing)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Row_3_seeded_but_empty_here_has_not_arrived(bool targetFolderExists)
    {
        var snapshot = Snap.WithLocalSaves() with
        {
            ActiveMarkers = [Snap.Marker("desktop", "GAME1/save.bin")],
            TargetKind = targetFolderExists ? EntryKind.Directory : EntryKind.Missing,
            TargetTree = targetFolderExists ? Snap.FoldersOnly("GAME1") : TreeFacts.Empty,
        };

        Assert.Equal(Classification.NotArrived, Planner.Classify(snapshot));
    }

    [Fact]
    public void Row_4_no_local_saves_just_links_whatever_the_target_holds()
    {
        var shared = Snap.Tree(("GAME1/save.bin", 100, null));

        Assert.Equal(Classification.LinkOnly, Planner.Classify(Snap.Base()));
        Assert.Equal(Classification.LinkOnly, Planner.Classify(Snap.Base() with { SourceKind = EntryKind.Missing }));
        Assert.Equal(Classification.LinkOnly, Planner.Classify(Snap.Base() with { TargetKind = EntryKind.Directory, TargetTree = shared }));

        // A fresh install's empty folder layout is not "saves".
        Assert.Equal(Classification.LinkOnly, Planner.Classify(Snap.Base() with { SourceTree = Snap.FoldersOnly("GAME1", "GAME1/sub") }));
    }

    [Fact]
    public void Row_5_local_saves_and_an_empty_target_is_the_first_device()
    {
        Assert.Equal(Classification.FirstDevice, Planner.Classify(Snap.WithLocalSaves()));
        Assert.Equal(
            Classification.FirstDevice,
            Planner.Classify(Snap.WithLocalSaves(Snap.FoldersOnly("GAME1"))));
    }

    [Fact]
    public void Row_6_saves_on_both_sides_is_the_guard()
    {
        Assert.Equal(Classification.Guard, Planner.Classify(Snap.Guarded()));
    }

    [Fact]
    public void Earlier_rows_win_where_rows_overlap()
    {
        var marker = Snap.Marker("desktop", "GAME1/save.bin");

        // Rows 1 and 2 beat row 3: an existing link is judged as a link even with a marker and an empty target.
        Assert.Equal(Classification.AlreadyApplied, Planner.Classify(Snap.LinkedTo(Snap.Target) with { ActiveMarkers = [marker] }));
        Assert.Equal(Classification.ForeignLink, Planner.Classify(Snap.LinkedTo(Snap.P("elsewhere")) with { ActiveMarkers = [marker] }));

        // Row 3 beats row 4: an empty local folder must not link to a shared copy that has not arrived.
        Assert.Equal(Classification.NotArrived, Planner.Classify(Snap.Base() with { ActiveMarkers = [marker] }));

        // Row 3 beats row 5: local saves must not be offered as the first seed when someone else seeded.
        Assert.Equal(Classification.NotArrived, Planner.Classify(Snap.WithLocalSaves() with { ActiveMarkers = [marker] }));

        // Row 3 only applies while the target is empty: once data is there, rows 4 and 6 decide.
        var arrived = Snap.Tree(("GAME1/save.bin", 100, null));
        Assert.Equal(Classification.LinkOnly, Planner.Classify(Snap.Base() with { ActiveMarkers = [marker], TargetKind = EntryKind.Directory, TargetTree = arrived }));
        Assert.Equal(Classification.Guard, Planner.Classify(Snap.Guarded() with { ActiveMarkers = [marker] }));
    }

    [Fact]
    public void Assume_synced_lets_row_3_fall_through()
    {
        var marker = Snap.Marker("desktop", "GAME1/save.bin");

        Assert.Equal(Classification.LinkOnly, Planner.Classify(Snap.Base() with { ActiveMarkers = [marker] }, assumeSynced: true));
        Assert.Equal(Classification.FirstDevice, Planner.Classify(Snap.WithLocalSaves() with { ActiveMarkers = [marker] }, assumeSynced: true));
    }
}

public class ChoiceTests
{
    private static readonly StepKind[] Switch = [RenameAside, CreateLink, VerifyLink, WriteState, DeleteAside];

    private static readonly ApplyRequest ReplaceConfirmed = new(SetupChoice.ReplaceShared, ConfirmReplace: true);

    [Fact]
    public void Already_applied_only_verifies_and_records()
    {
        var outcome = Planner.Plan(Snap.LinkedTo(Snap.Target), new ApplyRequest(SetupChoice.Copy));

        Assert.Equal([VerifyLink, WriteState], outcome.Kinds());
        Assert.Null(outcome.Plan!.Choice);
    }

    [Fact]
    public void Links_that_cannot_be_used_are_refused()
    {
        AssertRefused(Planner.Plan(Snap.LinkedTo(Snap.P("old")), new ApplyRequest()), RefusalKind.ForeignLink);
        AssertRefused(Planner.Plan(Snap.LinkedTo(Snap.Target, EntryKind.Missing), new ApplyRequest()), RefusalKind.BrokenLink);
    }

    [Fact]
    public void Not_arrived_is_refused_and_points_at_assume_synced()
    {
        var snapshot = Snap.WithLocalSaves() with { ActiveMarkers = [Snap.Marker("desktop", "GAME1/save.bin")] };

        var refusal = AssertRefused(Planner.Plan(snapshot, new ApplyRequest(SetupChoice.Copy)), RefusalKind.NotArrived);

        Assert.True(refusal.AssumeSyncedWouldHelp);
        Assert.Contains("desktop", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SetupChoice.Copy)]
    [InlineData(SetupChoice.NoCopy)]
    [InlineData(SetupChoice.AddMissing)]
    public void Link_only_links_whatever_choice_is_given(SetupChoice? choice)
    {
        var existing = Planner.Plan(Snap.Base(), new ApplyRequest(choice));
        var missing = Planner.Plan(Snap.Base() with { SourceKind = EntryKind.Missing }, new ApplyRequest(choice));

        Assert.Equal([EnsureTarget, .. Switch], existing.Kinds());
        Assert.Equal([EnsureTarget, CreateLink, VerifyLink, WriteState], missing.Kinds());
        Assert.Equal(SetupChoice.NoCopy, existing.Plan!.Choice);
        Assert.Equal(choice is not null, existing.Plan.Notes.Any(n => n.Contains("nothing to copy", StringComparison.Ordinal)));
    }

    [Fact]
    public void Link_only_refuses_to_replace_shared_saves_with_nothing()
    {
        var shared = Snap.Tree(("GAME1/save.bin", 100, null));
        var snapshot = Snap.Base() with { TargetKind = EntryKind.Directory, TargetTree = shared };

        AssertRefused(Planner.Plan(snapshot, ReplaceConfirmed), RefusalKind.ChoiceNotAllowed);
    }

    [Fact]
    public void First_device_must_choose_between_copy_and_no_copy()
    {
        var refusal = AssertRefused(Planner.Plan(Snap.WithLocalSaves(), new ApplyRequest()), RefusalKind.ChoiceNeeded);

        Assert.Equal([SetupChoice.Copy, SetupChoice.NoCopy], refusal.AllowedChoices);
        Assert.Equal(["GAME1", "GAME2"], refusal.Comparison!.OnlyLocalEntries);
    }

    [Fact]
    public void First_device_copy_seeds_the_shared_folder()
    {
        var outcome = Planner.Plan(Snap.WithLocalSaves(), new ApplyRequest(SetupChoice.Copy));

        Assert.Equal(
            [BackUpSource, EnsureTarget, CopyIntoTarget, RenameAside, CreateLink, VerifyLink, WriteSeedMarker, WriteState, DeleteAside],
            outcome.Kinds());
        Assert.Null(outcome.Plan!.Steps.Single(s => s.Kind == CopyIntoTarget).Entries);
        Assert.True(outcome.Plan.NeedsFirstSeedConfirmation);
    }

    [Fact]
    public void First_device_no_copy_links_to_an_empty_folder_and_says_so()
    {
        var outcome = Planner.Plan(Snap.WithLocalSaves(), new ApplyRequest(SetupChoice.NoCopy));

        Assert.Equal([BackUpSource, EnsureTarget, .. Switch], outcome.Kinds());
        Assert.Contains(outcome.Plan!.Notes, n => n.Contains("will see no saves", StringComparison.Ordinal));
        Assert.False(outcome.Plan.NeedsFirstSeedConfirmation);
    }

    [Theory]
    [InlineData(SetupChoice.AddMissing, false)]
    [InlineData(SetupChoice.ReplaceShared, true)]
    public void First_device_refuses_choices_meant_for_a_populated_target(SetupChoice choice, bool confirm)
    {
        var refusal = AssertRefused(
            Planner.Plan(Snap.WithLocalSaves(), new ApplyRequest(choice, ConfirmReplace: confirm)), RefusalKind.ChoiceNotAllowed);

        Assert.Equal([SetupChoice.Copy, SetupChoice.NoCopy], refusal.AllowedChoices);
    }

    [Fact]
    public void Copy_after_assume_synced_supersedes_the_other_devices_marker()
    {
        var snapshot = Snap.WithLocalSaves() with { ActiveMarkers = [Snap.Marker("desktop", "GAME1/save.bin")] };

        var outcome = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.Copy, AssumeSynced: true));

        Assert.Contains(SupersedeMarkers, outcome.Kinds());
        Assert.False(outcome.Plan!.NeedsFirstSeedConfirmation);
    }

    [Fact]
    public void Guard_stops_and_shows_the_comparison()
    {
        var refusal = AssertRefused(Planner.Plan(Snap.Guarded(), new ApplyRequest()), RefusalKind.ChoiceNeeded);

        Assert.Equal([SetupChoice.NoCopy, SetupChoice.AddMissing, SetupChoice.ReplaceShared], refusal.AllowedChoices);
        Assert.Contains("already holds saves", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(
            [("GAME1", EntryPresence.Different), ("GAME2", EntryPresence.OnlyLocal), ("GAME3", EntryPresence.OnlyShared)],
            refusal.Comparison!.Entries.Select(e => (e.Name, e.Presence)));
    }

    [Fact]
    public void Guard_refuses_copy()
    {
        var refusal = AssertRefused(Planner.Plan(Snap.Guarded(), new ApplyRequest(SetupChoice.Copy)), RefusalKind.ChoiceNotAllowed);

        Assert.Contains("--copy is refused", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SetupChoice.Copy, refusal.AllowedChoices);
    }

    [Fact]
    public void Guard_no_copy_backs_up_and_links_without_writing_to_the_target()
    {
        var outcome = Planner.Plan(Snap.Guarded(), new ApplyRequest(SetupChoice.NoCopy));

        Assert.Equal([BackUpSource, .. Switch], outcome.Kinds());
    }

    [Fact]
    public void Guard_add_missing_copies_only_entries_the_target_lacks()
    {
        var outcome = Planner.Plan(Snap.Guarded(), new ApplyRequest(SetupChoice.AddMissing));

        Assert.Equal([BackUpSource, CopyIntoTarget, .. Switch], outcome.Kinds());
        Assert.Equal(["GAME2"], outcome.Plan!.Steps.Single(s => s.Kind == CopyIntoTarget).Entries);
        Assert.Contains(outcome.Plan.Notes, n => n.Contains("GAME1", StringComparison.Ordinal) && n.Contains("shared version is kept", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_missing_never_names_an_entry_the_target_has_even_by_another_case()
    {
        var snapshot = Snap.Base() with
        {
            SourceTree = Snap.Tree(("Game1/save.bin", 1, null), ("GAME2/save.bin", 1, null), ("loose.sav", 1, null)),
            TargetKind = EntryKind.Directory,
            TargetTree = Snap.Tree(("GAME1/save.bin", 1, null), ("LOOSE.SAV", 1, null)),
        };

        var outcome = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.AddMissing));

        var entries = outcome.Plan!.Steps.Single(s => s.Kind == CopyIntoTarget).Entries!;
        Assert.Equal(["GAME2"], entries);
        Assert.DoesNotContain(entries, e => snapshot.TargetTree.TopLevelNames.Contains(e, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Add_missing_with_nothing_missing_copies_nothing()
    {
        var same = Snap.Tree(("GAME1/save.bin", 100, "aaa"));
        var snapshot = Snap.Base() with { SourceTree = same, TargetKind = EntryKind.Directory, TargetTree = same };

        var outcome = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.AddMissing));

        Assert.Equal([BackUpSource, .. Switch], outcome.Kinds());
        Assert.Contains(outcome.Plan!.Notes, n => n.Contains("Nothing will be copied", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_replace_shared_needs_its_second_confirmation()
    {
        var refusal = AssertRefused(
            Planner.Plan(Snap.Guarded(), new ApplyRequest(SetupChoice.ReplaceShared)), RefusalKind.ConfirmationNeeded);

        Assert.Contains("--confirm-replace", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_replace_shared_backs_up_both_sides_before_clearing()
    {
        var outcome = Planner.Plan(Snap.Guarded(), ReplaceConfirmed);

        Assert.Equal(
            [
                BackUpSource, BackUpTarget, ClearTarget, CopyIntoTarget, RenameAside, CreateLink, VerifyLink,
                WriteSeedMarker, SupersedeMarkers, WriteState, DeleteAside,
            ],
            outcome.Kinds());
        Assert.Contains(outcome.Plan!.Notes, n => n.Contains("every device", StringComparison.Ordinal));
    }

    [Fact]
    public void A_possibly_partial_shared_copy_blocks_only_the_choices_that_write_into_it()
    {
        var snapshot = Snap.Guarded() with
        {
            ActiveMarkers = [Snap.Marker("desktop", "GAME1/save.bin", "GAME9/save.bin")],
            MissingSeedFiles = ["GAME9/save.bin"],
        };

        // Linking is safe: the sync tool keeps filling the folder.
        var noCopy = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.NoCopy));
        Assert.True(noCopy.IsPlanned);
        Assert.Contains(noCopy.Plan!.Notes, n => n.Contains("may still be arriving", StringComparison.Ordinal));

        foreach (var request in new[] { new ApplyRequest(SetupChoice.AddMissing), ReplaceConfirmed })
        {
            var refusal = AssertRefused(Planner.Plan(snapshot, request), RefusalKind.NotArrived);
            Assert.True(refusal.AssumeSyncedWouldHelp);
            Assert.True(Planner.Plan(snapshot, request with { AssumeSynced = true }).IsPlanned);
        }

        // The confirmation is asked for before the sync question.
        AssertRefused(Planner.Plan(snapshot, new ApplyRequest(SetupChoice.ReplaceShared)), RefusalKind.ConfirmationNeeded);
    }

    [Fact]
    public void Two_active_markers_are_called_out_in_the_plan()
    {
        var snapshot = Snap.Guarded() with { ActiveMarkers = [Snap.Marker("deck"), Snap.Marker("desktop")] };

        var outcome = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.NoCopy));

        Assert.Contains(outcome.Plan!.Notes, n => n.Contains("More than one device", StringComparison.Ordinal) && n.Contains("desktop", StringComparison.Ordinal));
    }

    [Fact]
    public void Across_every_situation_and_choice_the_plans_keep_their_promises()
    {
        var situations = new[]
        {
            Snap.Base(), Snap.Base() with { SourceKind = EntryKind.Missing }, Snap.WithLocalSaves(), Snap.Guarded(),
            Snap.LinkedTo(Snap.Target), Snap.LinkedTo(Snap.P("old")),
            Snap.WithLocalSaves() with { ActiveMarkers = [Snap.Marker("desktop", "x")] },
        };
        var requests = new[]
        {
            new ApplyRequest(), new ApplyRequest(SetupChoice.Copy), new ApplyRequest(SetupChoice.NoCopy),
            new ApplyRequest(SetupChoice.AddMissing), new ApplyRequest(SetupChoice.ReplaceShared), ReplaceConfirmed,
            new ApplyRequest(SetupChoice.Copy, AssumeSynced: true),
        };

        foreach (var snapshot in situations)
        {
            foreach (var request in requests)
            {
                var outcome = Planner.Plan(snapshot, request);
                Assert.True(outcome.Plan is null != outcome.Refusal is null, "exactly one of plan and refusal");
                if (outcome.Plan is not { } plan)
                {
                    continue;
                }

                var kinds = plan.Steps.Select(s => s.Kind).ToList();

                // Deleting shared data happens only for a confirmed replace, and only after backing it up.
                if (kinds.Contains(ClearTarget))
                {
                    Assert.Equal(SetupChoice.ReplaceShared, plan.Choice);
                    Assert.True(request.ConfirmReplace);
                    Assert.True(kinds.IndexOf(BackUpTarget) < kinds.IndexOf(ClearTarget));
                }

                // Nothing is written into the target, and S is never moved, before S is backed up.
                var firstChange = kinds.FindIndex(k => k is CopyIntoTarget or ClearTarget or RenameAside);
                if (snapshot.SourceTree.HasData && firstChange >= 0)
                {
                    Assert.True(kinds.IndexOf(BackUpSource) is >= 0 and var backup && backup < firstChange);
                }

                // S is renamed aside, not deleted, until the link has been verified.
                if (kinds.Contains(RenameAside))
                {
                    Assert.Equal(snapshot.Source + ".savetether-old", plan.Steps.Single(s => s.Kind == RenameAside).OtherPath);
                    Assert.True(kinds.IndexOf(RenameAside) < kinds.IndexOf(CreateLink));
                    Assert.True(kinds.IndexOf(VerifyLink) < kinds.IndexOf(DeleteAside));
                    Assert.Equal(DeleteAside, kinds[^1]);
                }

                Assert.True(kinds.IndexOf(VerifyLink) < kinds.IndexOf(WriteState));
            }
        }
    }

    private static Refusal AssertRefused(PlanOutcome outcome, RefusalKind expected)
    {
        Assert.Null(outcome.Plan);
        Assert.Equal(expected, outcome.Refusal!.Kind);
        return outcome.Refusal;
    }
}
