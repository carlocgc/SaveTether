using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Tests.FileSystem;
using SaveTether.Tests.Planning;

namespace SaveTether.Tests.Execution;

public class ExecutorTests
{
    private const string Id = ExecWorld.MappingId;

    [Fact]
    public void Copy_makes_this_devices_saves_the_shared_copy_and_links()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        world.LocalSave("GAME2/save.bin", "two");
        var before = ExecWorld.Files(world.Source);

        var result = world.Apply(new ApplyRequest(SetupChoice.Copy));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.True(ExecWorld.IsLink(world.Source));
        Assert.Equal(before, ExecWorld.Files(world.Target));
        Assert.Equal("one", File.ReadAllText(Path.Combine(world.Source, "GAME1", "save.bin")));

        var marker = Assert.Single(world.Shared.ReadSeedMarkers(Id));
        Assert.Equal(ExecWorld.Device, marker.DeviceName);
        Assert.True(marker.IsActive);
        Assert.Equal(["GAME1/save.bin", "GAME2/save.bin"], marker.Files.Select(f => f.Path));

        var backupId = Assert.Single(result.BackupIds);
        Assert.True(world.Backups.Verify(backupId).IsValid);
        Assert.Equal(before, ExecWorld.Files(world.Backups.DataPathOf(backupId)));

        var applied = world.Local.ReadState().Mappings[Id];
        Assert.Equal(SetupChoice.Copy, applied.SetupChoice);
        Assert.Equal(world.Source, applied.Source);
        Assert.Equal(world.Target, applied.Target);
        Assert.Equal(OperatingSystem.IsWindows() ? LinkType.Junction : LinkType.Symlink, applied.LinkType);
        Assert.Equal([backupId], applied.BackupIds);

        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Aside));
        Assert.False(world.Journal.IsOpen);
        world.Sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void No_copy_links_to_the_empty_shared_folder_and_keeps_the_saves_in_the_backup()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");

        var result = world.Apply(new ApplyRequest(SetupChoice.NoCopy));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.True(ExecWorld.IsLink(world.Source));
        Assert.Empty(ExecWorld.Files(world.Target));
        Assert.Empty(world.Shared.ReadSeedMarkers(Id));
        Assert.Equal("one", ExecWorld.Files(world.Backups.DataPathOf(Assert.Single(result.BackupIds)))["GAME1/save.bin"]);
    }

    [Fact]
    public void Add_missing_adds_only_what_the_shared_copy_lacks_and_touches_nothing_else()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "mine");
        world.LocalSave("GAME2/save.bin", "only-here");
        world.SharedSave("GAME1/save.bin", "theirs");
        world.SharedSave("GAME3/save.bin", "only-there");
        var sharedStamp = File.GetLastWriteTimeUtc(Path.Combine(world.Target, "GAME1", "save.bin"));

        var result = world.Apply(new ApplyRequest(SetupChoice.AddMissing));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["GAME1/save.bin"] = "theirs", ["GAME2/save.bin"] = "only-here", ["GAME3/save.bin"] = "only-there",
            },
            ExecWorld.Files(world.Target));
        Assert.Equal(sharedStamp, File.GetLastWriteTimeUtc(Path.Combine(world.Target, "GAME1", "save.bin")));
        Assert.Equal("mine", ExecWorld.Files(world.Backups.DataPathOf(Assert.Single(result.BackupIds)))["GAME1/save.bin"]);
        Assert.Empty(world.Shared.ReadSeedMarkers(Id));
    }

    [Fact]
    public void Replace_shared_backs_up_the_shared_copy_first_and_supersedes_other_markers()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "mine");
        world.SharedSave("GAME1/save.bin", "theirs");
        world.SharedSave("GAME3/save.bin", "only-there");
        world.Shared.WriteSeedMarker(Marker("desktop", "GAME1/save.bin", "GAME3/save.bin"));
        var sharedBefore = ExecWorld.Files(world.Target);

        var result = world.Apply(new ApplyRequest(SetupChoice.ReplaceShared, ConfirmReplace: true));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "mine" }, ExecWorld.Files(world.Target));
        Assert.True(ExecWorld.IsLink(world.Source));

        Assert.Equal(2, result.BackupIds.Count);
        var backups = world.Backups.List(Id);
        var ofShared = Assert.Single(backups, b => PathRules.AreSame(b.SourcePath, world.Target));
        Assert.Equal(sharedBefore, ExecWorld.Files(world.Backups.DataPathOf(ofShared.Id)));

        var markers = world.Shared.ReadSeedMarkers(Id).ToDictionary(m => m.DeviceName);
        Assert.Equal(2, markers.Count);
        Assert.Equal(ExecWorld.Device, markers["desktop"].SupersededBy);
        Assert.True(markers[ExecWorld.Device].IsActive);
    }

    [Fact]
    public void Applying_twice_changes_nothing_the_second_time()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        world.Apply(new ApplyRequest(SetupChoice.Copy));
        var statePath = Path.Combine(world.Local.Folder, "state.json");
        var stateBefore = File.ReadAllText(statePath);
        var backupsBefore = world.Backups.List().Count;

        var plan = ExecWorld.Planned(world.PlanApply());
        var result = world.Executor().Run(plan);

        Assert.Equal(Classification.AlreadyApplied, plan.Classification);
        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(stateBefore, File.ReadAllText(statePath));
        Assert.Equal(backupsBefore, world.Backups.List().Count);
        Assert.Equal("one", ExecWorld.Files(world.Target)["GAME1/save.bin"]);
    }

    [Fact]
    public void Apply_then_revert_leaves_the_save_folder_as_it_was_and_the_shared_copy_alone()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        world.LocalSave("GAME2/deep/slot.bin", "two");
        var before = ExecWorld.Files(world.Source);
        world.Apply(new ApplyRequest(SetupChoice.Copy));

        var result = world.Revert();

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(world.Source));
        Assert.Equal(before, ExecWorld.Files(world.Source));
        Assert.Equal(before, ExecWorld.Files(world.Target));
        Assert.Empty(world.Local.ReadState().Mappings);
        Assert.Equal(BackupReason.PreRevert, world.Backups.List(Id).Single(b => b.Id == Assert.Single(result.BackupIds)).Reason);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Staging));
        Assert.False(world.Journal.IsOpen);
        world.Sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Losing_the_state_file_and_applying_again_records_the_existing_link()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        world.Apply(new ApplyRequest(SetupChoice.Copy));
        new SafeFs().DeleteTree(Path.Combine(world.Local.Folder, "state.json"));
        Assert.Empty(world.Local.ReadState().Mappings);

        var result = world.Apply();

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        var applied = world.Local.ReadState().Mappings[Id];
        Assert.Equal(world.Target, applied.Target);
        Assert.Equal(OperatingSystem.IsWindows() ? LinkType.Junction : LinkType.Symlink, applied.LinkType);
    }

    [Fact]
    public void While_an_operation_is_unfinished_everything_but_recovery_refuses()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(1, FaultPoint.BeforeStep)).Run(plan));
        var backupId = Assert.Single(world.Backups.List()).Id;

        Assert.Equal([PrecheckKind.UnfinishedOperation], Kinds(world.PlanApply(new ApplyRequest(SetupChoice.Copy))));
        Assert.Equal([PrecheckKind.UnfinishedOperation, PrecheckKind.NotApplied], Kinds(Planner.PlanRevert(world.Read())));
        Assert.Equal([PrecheckKind.UnfinishedOperation], Kinds(world.PlanRestore(backupId)));

        // Even a plan made as if nothing were wrong is not run.
        var stale = ExecWorld.Planned(Planner.Plan(world.Read(journalOpen: false), new ApplyRequest(SetupChoice.Copy)));
        var refused = world.Executor().Run(stale);
        Assert.Equal(ExecutionStatus.Refused, refused.Status);
        Assert.Contains("recover", refused.Error, StringComparison.Ordinal);
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(world.Source));

        Assert.Equal(ExecutionStatus.RolledBack, world.Executor().Recover().Status);
        Assert.Equal(ExecutionStatus.Completed, world.Apply(new ApplyRequest(SetupChoice.Copy)).Status);
    }

    [Fact]
    public void A_step_that_fails_before_the_link_is_verified_is_rolled_back()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var before = ExecWorld.Files(world.Source);
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));

        var result = world.Executor(new FailAt(StepKind.CreateLink, FaultPoint.AfterStep)).Run(plan);

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        Assert.Contains("injected failure", result.Error, StringComparison.Ordinal);
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(world.Source));
        Assert.Equal(before, ExecWorld.Files(world.Source));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Aside));
        Assert.False(world.Journal.IsOpen);
        Assert.Empty(world.Local.ReadState().Mappings);

        // Invariant 2: what was already copied into the shared folder stays, and is reported.
        Assert.Equal(before, ExecWorld.Files(world.Target));
        Assert.Contains(result.Messages, m => m.Contains(world.Target, StringComparison.Ordinal));
        Assert.Single(result.BackupIds);
    }

    [Fact]
    public void A_step_that_fails_after_the_link_is_verified_is_finished_by_recovery()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));

        var result = world.Executor(new FailAt(StepKind.WriteState, FaultPoint.BeforeStep)).Run(plan);

        Assert.Equal(ExecutionStatus.RecoveryNeeded, result.Status);
        Assert.True(world.Journal.IsOpen);
        Assert.True(ExecWorld.IsLink(world.Source));

        var recovered = world.Executor().Recover();

        Assert.Equal(ExecutionStatus.Completed, recovered.Status);
        Assert.False(world.Journal.IsOpen);
        Assert.Equal(SetupChoice.Copy, world.Local.ReadState().Mappings[Id].SetupChoice);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Aside));
    }

    [Fact]
    public void A_leftover_aside_folder_stops_the_operation_before_anything_changes()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        Directory.CreateDirectory(world.Aside);
        File.WriteAllText(Path.Combine(world.Aside, "old.bin"), "from an earlier run");

        var result = world.Apply(new ApplyRequest(SetupChoice.Copy));

        Assert.Equal(ExecutionStatus.Refused, result.Status);
        Assert.Contains(world.Aside, result.Error, StringComparison.Ordinal);
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(world.Source));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Target));
        Assert.Empty(world.Backups.List());
        Assert.False(world.Journal.IsOpen);
        Assert.Equal("from an earlier run", File.ReadAllText(Path.Combine(world.Aside, "old.bin")));
    }

    [Fact]
    public void Recovery_clears_the_empty_folder_a_half_made_junction_leaves()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var before = ExecWorld.Files(world.Source);
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        var createLink = plan.Steps.ToList().FindIndex(s => s.Kind == StepKind.CreateLink);
        Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(createLink, FaultPoint.BeforeStep)).Run(plan));

        // What Windows leaves when the process dies between making the folder and making it a junction.
        Directory.CreateDirectory(world.Source);

        var result = world.Executor().Recover();

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        Assert.Equal(before, ExecWorld.Files(world.Source));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Aside));
    }

    [Fact]
    public void Recovery_does_not_overwrite_a_save_folder_that_reappeared()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        var createLink = plan.Steps.ToList().FindIndex(s => s.Kind == StepKind.CreateLink);
        Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(createLink, FaultPoint.BeforeStep)).Run(plan));

        // The app was started meanwhile and made a new save.
        world.LocalSave("GAME7/save.bin", "new");

        var result = world.Executor().Recover();

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        Assert.Equal("new", ExecWorld.Files(world.Source)["GAME7/save.bin"]);
        Assert.Equal("one", ExecWorld.Files(world.Aside)["GAME1/save.bin"]);
        Assert.Contains(result.Messages, m => m.Contains(world.Aside, StringComparison.Ordinal));
    }

    [Fact]
    public void Recovery_with_nothing_unfinished_does_nothing()
    {
        using var world = new ExecWorld();

        Assert.Equal(ExecutionStatus.NothingToRecover, world.Executor().Recover().Status);
    }

    [Fact]
    public void Recovery_works_without_the_backup_location()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(3, FaultPoint.AfterStep)).Run(plan));

        var result = new Executor(world.Shared, world.Local, backups: null, ExecWorld.Device).Recover();

        Assert.Equal(ExecutionStatus.RolledBack, result.Status);
        Assert.Equal("one", ExecWorld.Files(world.Source)["GAME1/save.bin"]);
    }

    [Fact]
    public void Links_inside_the_save_folder_are_recorded_reported_and_never_followed()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        Sandbox.Link(Path.Combine(world.Source, "link-to-canary"), world.Sandbox.Canary);

        var result = world.Apply(new ApplyRequest(SetupChoice.Copy));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(["GAME1", "GAME1/save.bin"], SafeFs.EnumerateTree(world.Target).Select(e => e.RelativePath));
        Assert.Contains(result.Messages, m => m.Contains("link-to-canary", StringComparison.Ordinal));
        Assert.Equal("link-to-canary", Assert.Single(world.Backups.LinksOf(Assert.Single(result.BackupIds))).Path);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Aside));
        world.Sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_backup_of_the_save_folder_is_restored_and_what_it_overwrites_is_backed_up()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "v1");
        var original = ExecWorld.Files(world.Source);
        var backupId = Assert.Single(world.Apply(new ApplyRequest(SetupChoice.NoCopy)).BackupIds);

        // While the folder is a link, restoring into it would write into the shared folder.
        Assert.Equal([PrecheckKind.RestoreDestinationUnusable], Kinds(world.PlanRestore(backupId)));

        Assert.Equal(ExecutionStatus.Completed, world.Revert().Status);
        Assert.Empty(ExecWorld.Files(world.Source));

        var first = world.Restore(backupId);
        Assert.Equal(ExecutionStatus.Completed, first.Status);
        Assert.Empty(first.BackupIds);
        Assert.Equal(original, ExecWorld.Files(world.Source));

        world.LocalSave("GAME1/save.bin", "v2");
        world.LocalSave("GAME5/save.bin", "v2-extra");
        var second = world.Restore(backupId);

        Assert.Equal(ExecutionStatus.Completed, second.Status);
        Assert.Equal(original, ExecWorld.Files(world.Source));
        var preRestore = world.Backups.List(Id).Single(b => b.Id == Assert.Single(second.BackupIds));
        Assert.Equal(BackupReason.PreRestore, preRestore.Reason);
        Assert.Equal("v2-extra", ExecWorld.Files(world.Backups.DataPathOf(preRestore.Id))["GAME5/save.bin"]);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Aside));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(world.Staging));
        Assert.False(world.Journal.IsOpen);
    }

    [Fact]
    public void A_backup_of_the_shared_folder_is_restored_only_with_confirmation()
    {
        using var world = new ExecWorld();
        world.SharedSave("GAME1/save.bin", "A");
        var original = ExecWorld.Files(world.Target);
        var backupId = world.Backups.Create(Id, world.Target, BackupReason.Snapshot).Id;
        world.SharedSave("GAME1/save.bin", "B");
        world.SharedSave("GAME2/save.bin", "B-extra");

        var unconfirmed = world.PlanRestore(backupId);
        Assert.Equal(RefusalKind.ConfirmationNeeded, unconfirmed.Refusal!.Kind);
        Assert.Equal("B", ExecWorld.Files(world.Target)["GAME1/save.bin"]);

        var result = world.Restore(backupId, new RestoreRequest(ConfirmReplace: true));

        Assert.Equal(ExecutionStatus.Completed, result.Status);
        Assert.Equal(original, ExecWorld.Files(world.Target));
        Assert.Equal("B-extra", ExecWorld.Files(world.Backups.DataPathOf(Assert.Single(result.BackupIds)))["GAME2/save.bin"]);
    }

    [Fact]
    public void A_backup_that_no_longer_matches_its_manifest_is_not_restored()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "v1");
        var backupId = world.Backups.Create(Id, world.Source, BackupReason.Baseline).Id;
        File.WriteAllText(Path.Combine(world.Backups.DataPathOf(backupId), "GAME1", "save.bin"), "xx");

        Assert.Equal([PrecheckKind.BackupUnusable], Kinds(world.PlanRestore(backupId)));
        Assert.Equal([PrecheckKind.BackupUnusable], Kinds(world.PlanRestore("ppsspp.savedata/20200101T000000Z")));
    }

    [Fact]
    public void A_link_to_another_filesystem_applies_and_reverts()
    {
        // tmpfs is a different filesystem from the temporary folder on Linux. Elsewhere there is nothing to test.
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm"))
        {
            return;
        }

        var tmpfs = Path.Combine("/dev/shm", "savetether-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpfs);
        try
        {
            using var world = new ExecWorld(Path.Combine(tmpfs, "sync"));
            world.LocalSave("GAME1/save.bin", "one");
            var before = ExecWorld.Files(world.Source);

            Assert.Equal(ExecutionStatus.Completed, world.Apply(new ApplyRequest(SetupChoice.Copy)).Status);
            Assert.True(ExecWorld.IsLink(world.Source));
            Assert.Equal(before, ExecWorld.Files(world.Target));

            Assert.Equal(ExecutionStatus.Completed, world.Revert().Status);
            Assert.Equal(before, ExecWorld.Files(world.Source));
            Assert.Equal(before, ExecWorld.Files(world.Target));
            world.Sandbox.AssertCanaryIntact();
        }
        finally
        {
            new SafeFs().DeleteTree(tmpfs);
        }
    }

    [Fact]
    public void The_journal_holds_the_plan_and_how_far_it_got()
    {
        using var world = new ExecWorld();
        world.LocalSave("GAME1/save.bin", "one");
        var plan = ExecWorld.Planned(world.PlanApply(new ApplyRequest(SetupChoice.Copy)));
        Assert.Throws<SimulatedCrashException>(() => world.Executor(new CrashAt(2, FaultPoint.BeforeStep)).Run(plan));

        var entry = world.Journal.TryRead()!;

        Assert.Equal(ExecWorld.Device, entry.DeviceName);
        Assert.Equal(plan.Steps, entry.Plan.Steps);
        Assert.Equal(SetupChoice.Copy, entry.Plan.Choice);
        Assert.Equal(
            [StepStatus.Done, StepStatus.Done, StepStatus.Started],
            entry.Steps.Take(3).Select(s => s.Status));
        Assert.All(entry.Steps.Skip(3), s => Assert.Equal(StepStatus.Pending, s.Status));
        Assert.Equal(world.Backups.List().Single().Id, Assert.Single(entry.BackupIds));
    }

    private static PrecheckKind[] Kinds(PlanOutcome outcome) => outcome.Refusal!.Failures.Select(f => f.Kind).ToArray();

    private static SeedMarker Marker(string device, params string[] files) => new()
    {
        Schema = SeedMarker.CurrentSchema,
        MappingId = Id,
        DeviceName = device,
        SeededUtc = DateTimeOffset.UnixEpoch,
        Files = files.Select(f => new SeedFile(f, 1)).ToList(),
    };
}

public class RevertPlannerTests
{
    [Fact]
    public void A_linked_folder_is_reverted_by_backing_up_copying_unlinking_and_renaming()
    {
        var snapshot = Snap.LinkedTo(Snap.Target) with { TargetTree = Snap.Tree(("GAME1/save.bin", 100, null)) };

        var plan = Planner.PlanRevert(snapshot).Plan!;

        Assert.Equal(PlanOperation.Revert, plan.Operation);
        Assert.Equal(
            [StepKind.BackUpTarget, StepKind.CopyToStaging, StepKind.RemoveLink, StepKind.RenameIntoPlace, StepKind.WriteState],
            plan.Steps.Select(s => s.Kind));
        Assert.Equal(Snap.Source + Planner.StagingSuffix, plan.Steps[1].OtherPath);
        Assert.Contains("Plan to revert", PlanRenderer.ToText(new PlanOutcome(plan, null)), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_shared_folder_needs_no_backup()
    {
        var plan = Planner.PlanRevert(Snap.LinkedTo(Snap.Target)).Plan!;

        Assert.DoesNotContain(plan.Steps, s => s.Kind == StepKind.BackUpTarget);
    }

    [Fact]
    public void Each_reason_not_to_revert_is_named()
    {
        static PrecheckKind[] Kinds(Snapshot snapshot, bool assumeClosed = false) =>
            Planner.PlanRevert(snapshot, assumeClosed).Refusal!.Failures.Select(f => f.Kind).ToArray();
        var linked = Snap.LinkedTo(Snap.Target) with { TargetTree = Snap.Tree(("GAME1/save.bin", 100, null)) };

        Assert.Equal([PrecheckKind.NotApplied], Kinds(Snap.Base()));
        Assert.Equal([PrecheckKind.NotApplied], Kinds(Snap.LinkedTo(Snap.P("elsewhere"))));
        Assert.Equal([PrecheckKind.TargetUnusable], Kinds(Snap.LinkedTo(Snap.Target, EntryKind.Missing)));
        Assert.Equal([PrecheckKind.SyncRootNotInitialized], Kinds(linked with { SyncRootInitialized = false, JournalOpen = true }));
        Assert.Equal([PrecheckKind.UnfinishedOperation], Kinds(linked with { JournalOpen = true }));
        Assert.Equal([PrecheckKind.BackupLocationUnusable], Kinds(linked with { BackupRootProblem = "no such folder" }));
        Assert.Equal([PrecheckKind.NotEnoughSpace], Kinds(linked with { FreeBytesAtSource = 99 }));
        Assert.Equal([PrecheckKind.NotEnoughSpace], Kinds(linked with { FreeBytesAtBackupRoot = 99 }));

        var running = linked with { RunningProcesses = ["PPSSPPSDL"], ProcessNamesChecked = ["PPSSPPSDL"] };
        Assert.Equal([PrecheckKind.AppRunning], Kinds(running));
        Assert.True(Planner.PlanRevert(running, assumeClosed: true).IsPlanned);
        Assert.Contains("Not reverted", PlanRenderer.ToText(Planner.PlanRevert(Snap.Base())), StringComparison.Ordinal);
    }
}
