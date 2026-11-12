using System.Text.Json;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;

namespace SaveTether.Tests.Planning;

public class PrecheckTests
{
    private static readonly ApplyRequest Copy = new(SetupChoice.Copy);

    [Fact]
    public void A_healthy_snapshot_has_no_failures()
    {
        Assert.True(Planner.Plan(Snap.WithLocalSaves(), Copy).IsPlanned);
    }

    [Fact]
    public void An_uninitialized_sync_root_is_the_only_thing_reported()
    {
        var snapshot = Snap.WithLocalSaves() with
        {
            SyncRootInitialized = false, JournalOpen = true, SourceParentExists = false, RunningProcesses = ["PPSSPPSDL"],
        };

        Assert.Equal([PrecheckKind.SyncRootNotInitialized], Failures(snapshot));
    }

    [Fact]
    public void An_unfinished_operation_blocks_everything()
    {
        Assert.Equal([PrecheckKind.UnfinishedOperation], Failures(Snap.WithLocalSaves() with { JournalOpen = true }));
        Assert.Equal([PrecheckKind.UnfinishedOperation], Failures(Snap.LinkedTo(Snap.Target) with { JournalOpen = true }));
    }

    [Fact]
    public void The_target_must_be_a_folder_inside_the_data_root()
    {
        foreach (var bad in new[]
                 {
                     Snap.P("sync", "PPSSPP", "SAVEDATA"), // the old script-era layout
                     Snap.P("elsewhere", "SAVEDATA"), // a link in the path led out of the sync root
                     Snap.DataRoot,
                     Path.Combine(Snap.DataRoot, ".savetether", "mappings"),
                 })
        {
            Assert.Equal([PrecheckKind.TargetOutsideDataRoot], Failures(Snap.WithLocalSaves() with { ResolvedTarget = bad }));
        }
    }

    [Fact]
    public void The_source_must_be_outside_the_sync_root()
    {
        var inside = Snap.WithLocalSaves() with { ResolvedSource = Snap.P("sync", "Retroarch", "saves") };
        Assert.Equal([PrecheckKind.SourceInsideSyncRoot], Failures(inside));

        // The path looked fine as written; only resolving a link in it showed where it lands.
        Assert.Equal(Snap.Source, inside.Source);
    }

    [Fact]
    public void The_source_must_not_contain_the_target()
    {
        var snapshot = Snap.WithLocalSaves() with { ResolvedSource = Snap.P("sync", "savetether", "ppsspp") };
        Assert.Contains(PrecheckKind.SourceInsideSyncRoot, Failures(snapshot));

        var containing = Snap.WithLocalSaves() with { ResolvedSource = Path.GetPathRoot(Snap.SyncRoot)! };
        Assert.Equal([PrecheckKind.PathsOverlap], Failures(containing));
    }

    [Fact]
    public void A_missing_parent_folder_is_refused_so_a_typo_cannot_create_a_link()
    {
        var snapshot = Snap.Base() with { SourceKind = EntryKind.Missing, SourceParentExists = false };

        Assert.Equal([PrecheckKind.SourceParentMissing], Failures(snapshot, new ApplyRequest()));
    }

    [Theory]
    [InlineData(EntryKind.File)]
    [InlineData(EntryKind.OtherReparsePoint)]
    public void A_source_that_is_not_a_folder_is_refused(EntryKind kind)
    {
        Assert.Equal([PrecheckKind.SourceUnusable], Failures(Snap.Base() with { SourceKind = kind }, new ApplyRequest()));
    }

    [Theory]
    [InlineData(EntryKind.File)]
    [InlineData(EntryKind.Symlink)]
    [InlineData(EntryKind.Junction)]
    [InlineData(EntryKind.OtherReparsePoint)]
    public void A_target_that_is_not_a_plain_folder_is_refused(EntryKind kind)
    {
        Assert.Equal([PrecheckKind.TargetUnusable], Failures(Snap.WithLocalSaves() with { TargetKind = kind }));
    }

    [Fact]
    public void A_running_app_is_refused_naming_what_was_looked_for()
    {
        var snapshot = Snap.WithLocalSaves() with
        {
            ProcessNamesChecked = ["PPSSPPSDL", "PPSSPPQt"], RunningProcesses = ["PPSSPPSDL"],
        };

        var refusal = Planner.Plan(snapshot, Copy).Refusal!;
        Assert.Equal([PrecheckKind.AppRunning], refusal.Failures.Select(f => f.Kind));
        Assert.Contains("PPSSPPQt", refusal.Message, StringComparison.Ordinal);

        // The owner can override a false positive.
        Assert.True(Planner.Plan(snapshot, Copy with { AssumeClosed = true }).IsPlanned);

        // An existing link is only checked and recorded, so a running app does not matter.
        Assert.True(Planner.Plan(Snap.LinkedTo(Snap.Target) with { RunningProcesses = ["PPSSPPSDL"] }, new ApplyRequest()).IsPlanned);
    }

    [Fact]
    public void An_unusable_backup_location_is_refused_with_its_reason()
    {
        var snapshot = Snap.WithLocalSaves() with { BackupRootProblem = "The backup location is inside the sync root." };

        var refusal = Planner.Plan(snapshot, Copy).Refusal!;
        Assert.Equal([PrecheckKind.BackupLocationUnusable], refusal.Failures.Select(f => f.Kind));
        Assert.Equal("The backup location is inside the sync root.", refusal.Message);
    }

    [Fact]
    public void A_network_target_is_refused()
    {
        Assert.Equal([PrecheckKind.TargetNotLocal], Failures(Snap.WithLocalSaves() with { TargetOnLocalVolume = false }));
    }

    [Fact]
    public void Every_failure_is_reported_together()
    {
        var snapshot = Snap.WithLocalSaves() with
        {
            JournalOpen = true, SourceParentExists = false, RunningProcesses = ["x"], TargetOnLocalVolume = false,
        };

        var refusal = Planner.Plan(snapshot, Copy).Refusal!;
        Assert.Equal(
            [PrecheckKind.UnfinishedOperation, PrecheckKind.SourceParentMissing, PrecheckKind.TargetNotLocal, PrecheckKind.AppRunning],
            refusal.Failures.Select(f => f.Kind));
        Assert.Equal(RefusalKind.Precondition, refusal.Kind);
        Assert.Contains("4 checks failed", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Free_space_is_checked_against_what_the_plan_will_write()
    {
        // Local saves are 300 bytes; the guard's shared copy is 400.
        var roomy = Snap.WithLocalSaves() with { FreeBytesAtBackupRoot = 300, FreeBytesAtTarget = 300 };
        Assert.True(Planner.Plan(roomy, Copy).IsPlanned);

        Assert.Equal([PrecheckKind.NotEnoughSpace], Failures(roomy with { FreeBytesAtBackupRoot = 299 }));
        Assert.Equal([PrecheckKind.NotEnoughSpace], Failures(roomy with { FreeBytesAtTarget = 299 }));

        // No-copy writes nothing to the target, so only the backup needs room.
        Assert.True(Planner.Plan(roomy with { FreeBytesAtTarget = 0 }, new ApplyRequest(SetupChoice.NoCopy)).IsPlanned);

        // Add-missing copies only GAME2 (200 bytes).
        var guarded = Snap.Guarded() with { FreeBytesAtBackupRoot = 300, FreeBytesAtTarget = 200 };
        Assert.True(Planner.Plan(guarded, new ApplyRequest(SetupChoice.AddMissing)).IsPlanned);
        Assert.Equal([PrecheckKind.NotEnoughSpace], Failures(guarded with { FreeBytesAtTarget = 199 }, new ApplyRequest(SetupChoice.AddMissing)));

        // Replace-shared backs up both sides: 300 + 400.
        var replace = new ApplyRequest(SetupChoice.ReplaceShared, ConfirmReplace: true);
        Assert.True(Planner.Plan(Snap.Guarded() with { FreeBytesAtBackupRoot = 700 }, replace).IsPlanned);
        Assert.Equal([PrecheckKind.NotEnoughSpace], Failures(Snap.Guarded() with { FreeBytesAtBackupRoot = 699 }, replace));

        // Unknown free space does not block.
        Assert.True(Planner.Plan(Snap.WithLocalSaves() with { FreeBytesAtBackupRoot = null, FreeBytesAtTarget = null }, Copy).IsPlanned);
    }

    private static PrecheckKind[] Failures(Snapshot snapshot, ApplyRequest? request = null)
    {
        var outcome = Planner.Plan(snapshot, request ?? Copy);
        Assert.Null(outcome.Plan);
        Assert.Equal(RefusalKind.Precondition, outcome.Refusal!.Kind);
        return outcome.Refusal.Failures.Select(f => f.Kind).ToArray();
    }
}

public class ComparisonTests
{
    [Fact]
    public void Entries_are_grouped_by_where_they_exist_and_whether_they_agree()
    {
        var local = Snap.Tree(
            ("SAME/a.bin", 10, "h1"), ("SIZE/a.bin", 10, null), ("HASH/a.bin", 10, "h2"), ("MINE/a.bin", 5, null),
            ("MIXED/same.bin", 1, "h4"), ("MIXED/mine.bin", 2, null), ("loose.sav", 7, "h5"));
        var shared = Snap.Tree(
            ("SAME/a.bin", 10, "h1"), ("SIZE/a.bin", 11, null), ("HASH/a.bin", 10, "h3"), ("THEIRS/a.bin", 6, null),
            ("MIXED/same.bin", 1, "h4"), ("MIXED/theirs.bin", 3, null), ("loose.sav", 7, "h5"));

        var comparison = Comparison.Build(local, shared);

        Assert.Equal(
            [
                ("HASH", EntryPresence.Different), ("MINE", EntryPresence.OnlyLocal), ("MIXED", EntryPresence.Different),
                ("SAME", EntryPresence.Identical), ("SIZE", EntryPresence.Different), ("THEIRS", EntryPresence.OnlyShared),
                ("loose.sav", EntryPresence.Identical),
            ],
            comparison.Entries.Select(e => (e.Name, e.Presence)));
        Assert.Equal(["MINE"], comparison.OnlyLocalEntries);

        var mixed = comparison.Entries.Single(e => e.Name == "MIXED").Files;
        Assert.Equal(
            [
                new FileDifference("MIXED/mine.bin", FileDifferenceKind.OnlyLocal, 2, null),
                new FileDifference("MIXED/theirs.bin", FileDifferenceKind.OnlyShared, null, 3),
            ],
            mixed);
        Assert.Equal(
            [new FileDifference("SIZE/a.bin", FileDifferenceKind.Different, 10, 11)],
            comparison.Entries.Single(e => e.Name == "SIZE").Files);
        Assert.Empty(comparison.Entries.Single(e => e.Name == "SAME").Files);
    }

    [Fact]
    public void Files_without_a_hash_are_never_called_identical()
    {
        var unhashed = Snap.Tree(("GAME/a.bin", 10, null));

        var comparison = Comparison.Build(unhashed, unhashed);

        Assert.Equal(EntryPresence.Different, comparison.Entries.Single().Presence);
    }

    [Fact]
    public void Empty_sides_and_empty_folders_compare_cleanly()
    {
        var saves = Snap.Tree(("GAME/a.bin", 10, null));

        Assert.Empty(Comparison.Build(TreeFacts.Empty, TreeFacts.Empty).Entries);
        Assert.Equal(EntryPresence.OnlyLocal, Comparison.Build(saves, TreeFacts.Empty).Entries.Single().Presence);
        Assert.Equal(EntryPresence.OnlyShared, Comparison.Build(TreeFacts.Empty, saves).Entries.Single().Presence);

        // A folder with nothing in it on both sides is not a difference.
        var folders = Snap.FoldersOnly("EMPTY");
        Assert.Equal(EntryPresence.Identical, Comparison.Build(folders, folders).Entries.Single().Presence);
    }

    [Fact]
    public void The_newest_file_time_is_reported_for_each_side()
    {
        var local = new TreeFacts(
        [
            new FileFact("a.sav", EntryKind.File, 1, Snap.Older),
            new FileFact("b.sav", EntryKind.File, 1, Snap.Newer),
            new FileFact("folder", EntryKind.Directory, 0, default),
        ]);
        var shared = Snap.Tree(("a.sav", 1, null));

        var comparison = Comparison.Build(local, shared);

        Assert.Equal(Snap.Newer, comparison.LocalNewestUtc);
        Assert.Equal(Snap.Older, comparison.SharedNewestUtc);
        Assert.Null(Comparison.Build(TreeFacts.Empty, shared).LocalNewestUtc);
    }
}

public class PlanRendererTests
{
    [Fact]
    public void A_plan_prints_as_numbered_steps_with_its_paths_and_notes()
    {
        var text = PlanRenderer.ToText(Planner.Plan(Snap.WithLocalSaves(), new ApplyRequest(SetupChoice.Copy)));

        Assert.Contains("Plan for ppsspp.savedata (shared location empty, --copy):", text, StringComparison.Ordinal);
        Assert.Contains($"save folder:   {Snap.Source}", text, StringComparison.Ordinal);
        Assert.Contains($"shared folder: {Snap.Target}", text, StringComparison.Ordinal);
        Assert.Contains($"1. Back up '{Snap.Source}' and verify the backup", text, StringComparison.Ordinal);
        Assert.Contains($"5. Link '{Snap.Source}' to '{Snap.Target}'", text, StringComparison.Ordinal);
        Assert.Contains("9. Delete", text, StringComparison.Ordinal);
        Assert.Contains("wait until that has synced", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_guard_refusal_prints_the_comparison_and_the_choices()
    {
        var text = PlanRenderer.ToText(Planner.Plan(Snap.Guarded(), new ApplyRequest()));

        Assert.Contains("Not applied: ppsspp.savedata. Nothing was changed.", text, StringComparison.Ordinal);
        Assert.Contains("Only on this device (1): GAME2", text, StringComparison.Ordinal);
        Assert.Contains("Only in the shared copy (1): GAME3", text, StringComparison.Ordinal);
        Assert.Contains("On both, but different (1): GAME1", text, StringComparison.Ordinal);
        Assert.Contains("This device, newest file: 2026-01-01 10:00 UTC", text, StringComparison.Ordinal);
        Assert.Contains("Choices: --no-copy | --add-missing | --replace-shared --confirm-replace", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_failed_checks_are_all_listed()
    {
        var snapshot = Snap.WithLocalSaves() with { JournalOpen = true, SourceParentExists = false };

        var text = PlanRenderer.ToText(Planner.Plan(snapshot, new ApplyRequest(SetupChoice.Copy)));

        Assert.Contains("2 checks failed", text, StringComparison.Ordinal);
        Assert.Contains("  - An earlier operation did not finish", text, StringComparison.Ordinal);
        Assert.Contains("  - The folder that should contain", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_for_a_plan_uses_stable_lowercase_names()
    {
        var json = PlanRenderer.ToJson(Planner.Plan(Snap.Guarded(), new ApplyRequest(SetupChoice.AddMissing)));
        using var document = JsonDocument.Parse(json);
        var plan = document.RootElement.GetProperty("plan");

        Assert.False(document.RootElement.TryGetProperty("refusal", out _));
        Assert.Equal("ppsspp.savedata", plan.GetProperty("mapping_id").GetString());
        Assert.Equal("guard", plan.GetProperty("classification").GetString());
        Assert.Equal("add-missing", plan.GetProperty("choice").GetString());
        Assert.False(plan.GetProperty("needs_first_seed_confirmation").GetBoolean());

        var steps = plan.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("back-up-source", steps[0].GetProperty("kind").GetString());
        Assert.Equal("copy-into-target", steps[1].GetProperty("kind").GetString());
        Assert.Equal(["GAME2"], steps[1].GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(Snap.Target, steps[1].GetProperty("other_path").GetString());
    }

    [Fact]
    public void Json_for_a_refusal_carries_the_reason_choices_and_comparison()
    {
        var json = PlanRenderer.ToJson(Planner.Plan(Snap.Guarded(), new ApplyRequest()));
        using var document = JsonDocument.Parse(json);
        var refusal = document.RootElement.GetProperty("refusal");

        Assert.False(document.RootElement.TryGetProperty("plan", out _));
        Assert.Equal("choice-needed", refusal.GetProperty("kind").GetString());
        Assert.Equal("guard", refusal.GetProperty("classification").GetString());
        Assert.Equal(
            ["no-copy", "add-missing", "replace-shared"],
            refusal.GetProperty("allowed_choices").EnumerateArray().Select(e => e.GetString()));
        var entries = refusal.GetProperty("comparison").GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal("different", entries[0].GetProperty("presence").GetString());
        Assert.Equal("only-local", entries[1].GetProperty("presence").GetString());

        var failed = PlanRenderer.ToJson(Planner.Plan(Snap.WithLocalSaves() with { JournalOpen = true }, new ApplyRequest()));
        using var failedDocument = JsonDocument.Parse(failed);
        Assert.Equal(
            "unfinished-operation",
            failedDocument.RootElement.GetProperty("refusal").GetProperty("failures")[0].GetProperty("kind").GetString());
    }
}
