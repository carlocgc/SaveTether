using SaveTether.Cli;
using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
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
