using SaveTether.Cli;
using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"map delete" (design 5.3, invariant 2's third exception): a mapping no device uses is removed from the shared folder.</summary>
public class MappingDeletionTests
{
    private const string Id = CliDevice.MappingId;

    private static Workspace Open(CliDevice device) => Workspace.Open(new LocalStore(device.LocalFolder), new NoProcesses());

    private static string MappingFile(CliDevice device) =>
        Path.Combine(device.SyncRoot, "savetether", ".savetether", "mappings", Id + ".json");

    private static string SeedFolder(CliDevice device) =>
        Path.Combine(device.SyncRoot, "savetether", ".savetether", "seed", Id);

    /// <summary>A device whose mapping was applied and reverted, then forgotten: the shared folder still holds the saves.</summary>
    private static CliDevice Unused(Sandbox sandbox)
    {
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        pc.Ok("revert", Id);
        pc.Ok("map", "remove", Id);
        return pc;
    }

    [Fact]
    public void An_unused_mapping_is_backed_up_then_its_shared_folder_markers_and_record_are_deleted()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        Assert.True(File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")));
        Assert.True(Directory.Exists(SeedFolder(pc)));
        var workspace = Open(pc);

        var described = workspace.DescribeMappingDeletion(Id);
        Assert.True(described.TargetExists);
        Assert.Equal(1, described.Files);
        Assert.True(File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")), "Describe changes nothing.");

        var done = workspace.DeleteMapping(Id);

        // A verified backup of the shared folder is on this device first, with its own reason.
        var backup = Assert.IsType<BackupInfo>(done.Backup);
        Assert.Equal(BackupReason.PreDelete, backup.Reason);
        Assert.Equal("one", File.ReadAllText(Path.Combine(backup.Location, "data", "GAME1", "save.bin")));
        Assert.Contains(workspace.ListBackups(Id), b => b.Reason == BackupReason.PreDelete);

        // The folder, its app folder, the markers and the record are gone; nothing else in the share changed.
        Assert.False(Directory.Exists(pc.Target));
        Assert.False(Directory.Exists(Path.GetDirectoryName(pc.Target)!));
        Assert.False(Directory.Exists(SeedFolder(pc)));
        Assert.False(File.Exists(MappingFile(pc)));
        Assert.True(Directory.Exists(Path.Combine(pc.SyncRoot, "savetether", ".savetether", "devices")));
        Assert.Empty(workspace.Status().Mappings);

        // The reverted copy on this device is untouched.
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_app_folder_stays_while_another_mapping_of_the_app_still_uses_it()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        var states = Path.Combine(pc.AppFolder, "STATES");
        CliDevice.Write(states, "state1.bin", "two");
        pc.Ok("map", "add", "ppsspp.states", "--target", "ppsspp/STATES", "--source", states);
        pc.Ok("apply", "ppsspp.states", "--copy");

        Open(pc).DeleteMapping(Id);

        Assert.False(Directory.Exists(pc.Target));
        Assert.True(File.Exists(Path.Combine(pc.SyncRoot, "savetether", "ppsspp", "STATES", "state1.bin")));
        Assert.Equal(["ppsspp.states"], Open(pc).Status().Mappings.Select(m => m.MappingId));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_mapping_a_device_still_has_a_folder_for_is_refused_and_nothing_is_deleted()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var workspace = Open(pc);

        // Applied here.
        var applied = Assert.Throws<StoreException>(() => workspace.DeleteMapping(Id));
        Assert.Equal(ConflictKind.InUseOnDevices, applied.Conflict!.Kind);
        Assert.Contains("pc", applied.Message, StringComparison.Ordinal);

        // Reverted but still set up here.
        pc.Ok("revert", Id);
        Assert.Equal(ConflictKind.InUseOnDevices, Assert.Throws<StoreException>(() => workspace.DeleteMapping(Id)).Conflict!.Kind);

        Assert.True(File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")));
        Assert.True(File.Exists(MappingFile(pc)));
        Assert.DoesNotContain(workspace.ListBackups(Id), b => b.Reason == BackupReason.PreDelete);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_mapping_another_device_has_a_folder_for_is_refused_by_name()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        var deck = new CliDevice(sandbox, "deck");
        pc.DeliverTo(deck, metadataOnly: true);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        deck.Ok("map", "add", Id, "--source", deck.Source);
        deck.DeliverTo(pc, metadataOnly: true);

        var refused = Assert.Throws<StoreException>(() => Open(pc).DeleteMapping(Id));

        Assert.Equal(ConflictKind.InUseOnDevices, refused.Conflict!.Kind);
        Assert.Contains("deck", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")));
        Assert.True(File.Exists(MappingFile(pc)));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_unknown_mapping_is_refused_and_an_empty_or_already_gone_shared_folder_needs_no_backup()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        var workspace = Open(pc);
        Assert.Equal(StoreProblem.NotFound, Assert.Throws<StoreException>(() => workspace.DeleteMapping("ppsspp.nothing")).Problem);

        // A run that stopped after the folder was deleted: the record and markers are still there. It can be run again.
        Directory.Delete(pc.Target, recursive: true);
        var done = workspace.DeleteMapping(Id);

        Assert.False(done.TargetExists);
        Assert.Null(done.Backup);
        Assert.False(File.Exists(MappingFile(pc)));
        Assert.False(Directory.Exists(SeedFolder(pc)));
        Assert.Equal(StoreProblem.NotFound, Assert.Throws<StoreException>(() => workspace.DeleteMapping(Id)).Problem);
        sandbox.AssertCanaryIntact();
    }

    private static string AppFolder(CliDevice device) => Path.Combine(device.SyncRoot, "savetether", "ppsspp");

    [Fact]
    public void An_app_folder_that_is_a_link_is_refused_and_what_it_points_at_is_untouched()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);

        // savetether/ppsspp is a link to a folder outside the share, which holds a SAVEDATA folder of its own.
        var outside = sandbox.Dir("outside");
        Directory.Move(AppFolder(pc), Path.Combine(outside, "ppsspp"));
        Sandbox.Link(AppFolder(pc), Path.Combine(outside, "ppsspp"));
        var canary = Path.Combine(outside, "ppsspp", "SAVEDATA", "GAME1", "save.bin");
        Assert.True(File.Exists(canary));
        var workspace = Open(pc);

        var described = Assert.Throws<StoreException>(() => workspace.DescribeMappingDeletion(Id));
        var refused = Assert.Throws<StoreException>(() => workspace.DeleteMapping(Id));

        Assert.Contains("link", described.Message, StringComparison.Ordinal);
        Assert.Contains("link", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(canary));
        Assert.True(File.Exists(MappingFile(pc)));
        Assert.DoesNotContain(workspace.ListBackups(Id), b => b.Reason == BackupReason.PreDelete);

        // The file layer refuses a link too, whatever called it.
        Assert.Throws<SafeFsException>(() => SafeFs.RemoveSharedFile(Path.Combine(pc.SyncRoot, "savetether"), Path.Combine(AppFolder(pc), "SAVEDATA", "GAME1", "save.bin")));
        Assert.True(File.Exists(canary));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_save_a_sync_tool_delivers_after_the_backup_is_kept_not_deleted_and_the_run_can_be_run_again()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        var workspace = Open(pc);
        var late = Path.Combine(pc.Target, "GAME2", "late.bin");
        workspace.DeletionSeam = stage =>
        {
            if (stage == "after-backup")
            {
                CliDevice.Write(pc.Target, "GAME2/late.bin", "delivered meanwhile");
            }
        };

        var partial = Assert.Throws<MappingDeletionIncompleteException>(() => workspace.DeleteMapping(Id));

        Assert.Equal(1, partial.Left);
        Assert.NotNull(partial.Backup);
        Assert.Contains(partial.Backup.Id, partial.Message, StringComparison.Ordinal);
        Assert.Equal("delivered meanwhile", File.ReadAllText(late));
        Assert.False(File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")), "what the backup holds is removed");
        Assert.True(File.Exists(MappingFile(pc)), "the record stays while something is left");
        Assert.False(File.Exists(Path.Combine(partial.Backup.Location, "data", "GAME2", "late.bin")), "the late file was not in that backup");

        // Run again, once the sync tool is done: the late file is backed up this time, then everything goes.
        workspace.DeletionSeam = null;
        var done = workspace.DeleteMapping(Id);
        Assert.NotNull(done.Backup);
        Assert.Equal("delivered meanwhile", File.ReadAllText(Path.Combine(done.Backup.Location, "data", "GAME2", "late.bin")));
        Assert.False(Directory.Exists(pc.Target));
        Assert.False(File.Exists(MappingFile(pc)));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_save_delivered_to_a_folder_that_looked_empty_is_backed_up_before_it_is_deleted()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        var workspace = Open(pc);

        // Nothing but empty folders when it is described; a save arrives before the delete looks again.
        Directory.Delete(pc.Target, recursive: true);
        Directory.CreateDirectory(Path.Combine(pc.Target, "GAME1"));
        Assert.Equal(0, workspace.DescribeMappingDeletion(Id).Files);
        workspace.DeletionSeam = stage =>
        {
            if (stage == "before-backup")
            {
                CliDevice.Write(pc.Target, "GAME1/new.bin", "arrived");
            }
        };

        var done = workspace.DeleteMapping(Id);

        var backup = Assert.IsType<BackupInfo>(done.Backup);
        Assert.Equal("arrived", File.ReadAllText(Path.Combine(backup.Location, "data", "GAME1", "new.bin")));
        Assert.False(Directory.Exists(pc.Target));
        Assert.False(File.Exists(MappingFile(pc)));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_app_folder_that_gains_another_role_while_the_delete_runs_is_left_with_what_it_holds()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        var workspace = Open(pc);
        var other = Path.Combine(AppFolder(pc), "STATES", "state1.bin");
        workspace.DeletionSeam = stage =>
        {
            if (stage == "before-app-folder")
            {
                CliDevice.Write(AppFolder(pc), "STATES/state1.bin", "another role's saves");
            }
        };

        var done = workspace.DeleteMapping(Id);

        Assert.NotNull(done.Backup);
        Assert.Equal("another role's saves", File.ReadAllText(other));
        Assert.False(Directory.Exists(pc.Target));
        Assert.False(File.Exists(MappingFile(pc)));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_failure_after_the_shared_copy_is_removed_says_so_names_the_backup_and_can_be_run_again()
    {
        using var sandbox = new Sandbox();
        var pc = Unused(sandbox);
        var workspace = Open(pc);
        workspace.DeletionSeam = stage =>
        {
            if (stage == "before-retire")
            {
                throw new IOException("the sync folder is read-only");
            }
        };

        var partial = Assert.Throws<MappingDeletionIncompleteException>(() => workspace.DeleteMapping(Id));

        Assert.Contains("only in part", partial.Message, StringComparison.Ordinal);
        Assert.Contains("read-only", partial.Message, StringComparison.Ordinal);
        Assert.NotNull(partial.Backup);
        Assert.Contains(partial.Backup.Id, partial.Message, StringComparison.Ordinal);
        Assert.Contains($"map delete {Id}", partial.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(pc.Target), "the shared saves are already gone");
        Assert.True(File.Exists(MappingFile(pc)), "the record is still there");

        workspace.DeletionSeam = null;
        var done = workspace.DeleteMapping(Id);
        Assert.False(done.TargetExists);
        Assert.False(File.Exists(MappingFile(pc)));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_command_asks_first_refuses_a_mapping_in_use_and_deletes_with_the_flag()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");

        // In use: refused, even with the flag.
        var inUse = pc.Expect(CliApp.ExitRefused, "map", "delete", Id, "--confirm-delete");
        Assert.Contains("still set up on pc", inUse.Err, StringComparison.Ordinal);

        pc.Ok("revert", Id);
        pc.Ok("map", "remove", Id);

        // Unused, but not confirmed (no terminal to ask at): refused, nothing deleted.
        var asked = pc.Expect(CliApp.ExitRefused, "map", "delete", Id);
        Assert.Contains("--confirm-delete", asked.Err, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")));

        var done = pc.Ok("map", "delete", Id, "--confirm-delete");
        Assert.Contains($"Deleted '{Id}'", done.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(pc.Target));
        Assert.False(File.Exists(MappingFile(pc)));
        Assert.Contains("pre-delete", pc.Ok("backup", "list", Id).Out, StringComparison.Ordinal);
        sandbox.AssertCanaryIntact();
    }

    private sealed class NoProcesses : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => [];
    }
}
