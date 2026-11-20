using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 9.7 of the desktop app spec: the Backups page and restore, against a fixture device.</summary>
public class BackupsPageTests
{
    private const string Id = CliDevice.MappingId;
    private const string Role = "ppsspp savedata";

    /// <summary>A device whose apply replaced a shared copy: one backup of its own folder, one of the shared copy.</summary>
    private static CliDevice WithBothBackups(Sandbox sandbox)
    {
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        CliDevice.Write(pc.Target, "GAME1/save.bin", "theirs");
        pc.Ok("apply", Id, "--replace-shared", "--confirm-replace");
        return pc;
    }

    [Fact]
    public void Backups_are_grouped_by_mapping_newest_first_with_what_each_is_of_and_the_first_kept()
    {
        using var sandbox = new Sandbox();
        var pc = WithBothBackups(sandbox);

        var (groups, appName) = BackupsPage.Load(new LocalStore(pc.LocalFolder));

        var group = Assert.Single(groups);
        Assert.Equal(Id, group.MappingId);
        Assert.Equal("ppsspp savedata", group.Name);
        Assert.Equal(2, group.Rows.Count);
        Assert.Single(group.Rows, r => r.OfSharedCopy);
        Assert.Single(group.Rows, r => !r.OfSharedCopy);
        Assert.Single(group.Rows, r => r.Kept);
        Assert.True(group.Rows[^1].Kept);
        Assert.All(group.Rows, r => Assert.Equal("before linking", r.Reason));
        Assert.Equal("1 file, 4 bytes", group.Rows.Single(r => !r.OfSharedCopy).Size);
        Assert.Equal("1 file, 6 bytes", group.Rows.Single(r => r.OfSharedCopy).Size);
        Assert.All(group.Rows, r => Assert.True(Directory.Exists(r.DataFolder)));
        Assert.Equal("Shared copy", group.Rows.Single(r => r.OfSharedCopy).Of);

        Assert.Equal([("ppsspp", "ppsspp")], BackupsPage.Apps(groups, appName));
        Assert.Same(groups, BackupsPage.Filter(groups, null));
        Assert.Empty(BackupsPage.Filter(groups, "cemu"));
        foreach (var reason in Enum.GetValues<BackupReason>())
        {
            Assert.False(string.IsNullOrWhiteSpace(BackupsPage.Reason(reason)));
        }
    }

    [Fact]
    public void Restoring_this_devices_folder_while_linked_offers_revert_and_after_it_brings_the_files_back()
    {
        using var sandbox = new Sandbox();
        var pc = WithBothBackups(sandbox);
        var local = new LocalStore(pc.LocalFolder);
        var row = BackupsPage.Load(local).Groups[0].Rows.Single(r => !r.OfSharedCopy);

        var (session, _) = OperationSession.Begin(local, "restore", new Processes(), null);
        using (session)
        {
            var refusal = session!.PlanRestore(row.Info.Id, new RestoreRequest()).Refusal!;
            var view = ActionTexts.Describe(refusal, Role, "ppsspp", sandboxGrantPossible: false);
            Assert.Equal($"{Role} cannot be restored yet", view.Title);
            Assert.Equal([$"{Role} is linked, so restoring would write into the shared copy. Revert first."], view.Sentences);
            Assert.Equal([RefusalAction.Revert], view.Actions);
        }

        pc.Ok("revert", Id);
        File.WriteAllText(Path.Combine(pc.Source, "GAME1", "save.bin"), "changed");

        var (restore, _) = OperationSession.Begin(local, "restore", new Processes(), null);
        using (restore)
        {
            var plan = restore!.PlanRestore(row.Info.Id, new RestoreRequest()).Plan!;
            Assert.Equal($"Restore the backup into {pc.Source}?", ActionTexts.Confirmation(plan, Role).Title);
            var result = restore.Run(plan, null);
            Assert.Equal(ExecutionStatus.Completed, result.Status);
            Assert.Equal("The backup is restored.", ActionTexts.Result(result, Role).Title);
        }

        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "mine" }, pc.LocalFiles());

        // What was there is itself backed up first.
        Assert.Contains(BackupsPage.Load(local).Groups[0].Rows, r => r.Reason == "before restoring");
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Restoring_the_shared_copy_needs_its_own_confirmation()
    {
        using var sandbox = new Sandbox();
        var pc = WithBothBackups(sandbox);
        var local = new LocalStore(pc.LocalFolder);
        var row = BackupsPage.Load(local).Groups[0].Rows.Single(r => r.OfSharedCopy);

        var (session, _) = OperationSession.Begin(local, "restore", new Processes(), null);
        using (session)
        {
            Assert.Equal(RefusalKind.ConfirmationNeeded, session!.PlanRestore(row.Info.Id, new RestoreRequest()).Refusal!.Kind);
            var plan = session.PlanRestore(row.Info.Id, new RestoreRequest(ConfirmReplace: true)).Plan!;
            Assert.Equal(ExecutionStatus.Completed, session.Run(plan, null).Status);
        }

        Assert.Equal("theirs", pc.SharedFiles()["GAME1/save.bin"]);
        Assert.Equal($"Restore the shared saves for {Role} on every device?", BackupsPage.RestoreSharedTitle(Role));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Deleting_is_worded_more_strongly_for_the_first_backup_and_runs_through_the_session()
    {
        using var sandbox = new Sandbox();
        var pc = WithBothBackups(sandbox);
        var local = new LocalStore(pc.LocalFolder);
        var rows = BackupsPage.Load(local).Groups[0].Rows;
        var kept = rows.Single(r => r.Kept);
        var later = rows.First(r => !r.Kept);

        Assert.Contains("only copy of it from before SaveTether changed it", BackupsPage.DeleteText(kept), StringComparison.Ordinal);
        Assert.DoesNotContain("first backup", BackupsPage.DeleteText(later), StringComparison.Ordinal);
        Assert.Contains("cannot be undone", BackupsPage.DeleteText(later), StringComparison.Ordinal);
        Assert.Equal("Delete the first backup", BackupsPage.DeleteButton(kept));
        Assert.Equal("Delete", BackupsPage.DeleteButton(later));
        Assert.Equal($"Delete the backup from {later.When}?", BackupsPage.DeleteTitle(later));

        var (session, _) = OperationSession.Begin(local, "backup delete", new Processes(), null);
        using (session)
        {
            var deleted = session!.DeleteBackup(later.Info.Id);
            Assert.False(deleted.WasFirst);
            Assert.Equal(1, deleted.Remaining);
        }

        Assert.Equal([kept.Info.Id], BackupsPage.Load(local).Groups[0].Rows.Select(r => r.Info.Id));
        Assert.False(Directory.Exists(later.Info.Location));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_restore_destination_that_is_not_a_link_names_no_revert()
    {
        var failure = new PrecheckFailure(PrecheckKind.RestoreDestinationUnusable, "'x' exists but is not a folder.");
        var refusal = new Refusal(Id, RefusalKind.Precondition, Classification.NotClassified, failure.Message, [failure], [], false, null, PlanOperation.Restore);

        var view = ActionTexts.Describe(refusal, Role, "ppsspp", sandboxGrantPossible: false);

        Assert.Empty(view.Actions);
        Assert.StartsWith("The folder this backup was taken from cannot be restored into", view.Sentences.Single(), StringComparison.Ordinal);
    }

    private sealed class Processes : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => [];
    }
}
