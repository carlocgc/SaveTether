using SaveTether.Core.Health;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Health;

/// <summary>Phase 3.8: a sync tool's conflict copies in a linked shared folder, looked for only when the device's setting is on.</summary>
public class ConflictCopyTests
{
    [Theory]
    [InlineData("save.sync-conflict-20261126-101500-ABCDEFG.dat", true)]
    [InlineData("SAVE.SYNC-CONFLICT-20261126-101500-ABCDEFG.DAT", true)]
    [InlineData("save (conflicted copy 2026-11-26 101500).dat", true)]
    [InlineData("save (Sam's conflicted copy 2026-11-26).dat", true)]
    [InlineData("save_conflict-20261126-101500.dat", true)]
    [InlineData("save.dat", false)]
    [InlineData("save (1).dat", false)]
    [InlineData("save-PC.dat", false)]
    [InlineData(".syncthing.save.dat.tmp", false)]
    [InlineData("~syncthing~save.dat.tmp", false)]
    public void Only_the_names_sync_tools_give_conflict_copies_match(string name, bool expected) =>
        Assert.Equal(expected, HealthCheck.IsConflictCopy(name));

    [Fact]
    public void A_linked_folder_with_a_conflict_copy_is_a_file_conflict_only_with_the_setting_on()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", CliDevice.MappingId, "--copy");
        CliDevice.Write(pc.Target, "GAME1/save.sync-conflict-20261126-101500-ABC.bin", "two");
        var local = new LocalStore(pc.LocalFolder);

        Assert.Equal(HealthState.Ok, Health(local).State);

        Assert.Null(new DeviceSettings(pc.LocalFolder).SetDetectSyncConflicts(true));
        var health = Health(local);
        Assert.Equal(HealthState.FileConflict, health.State);
        Assert.Equal(["GAME1/save.sync-conflict-20261126-101500-ABC.bin"], health.Conflicts);
        Assert.Equal(
            "The sync tool left a conflict copy in the shared folder, 'GAME1/save.sync-conflict-20261126-101500-ABC.bin'. " +
            "Keep the right one and delete the other.",
            health.Detail.Text);

        CliDevice.Write(pc.Target, "save (conflicted copy 2026-11-26 101500).bin", "three");
        health = Health(local);
        Assert.Equal(2, health.Conflicts!.Count);
        Assert.StartsWith("The sync tool left 2 conflict copies in the shared folder, such as ", health.Detail.Text, StringComparison.Ordinal);

        // Deleting the copies, as the owner does by hand, makes it Linked again.
        File.Delete(Path.Combine(pc.Target, "GAME1", "save.sync-conflict-20261126-101500-ABC.bin"));
        File.Delete(Path.Combine(pc.Target, "save (conflicted copy 2026-11-26 101500).bin"));
        Assert.Equal(HealthState.Ok, Health(local).State);
    }

    [Fact]
    public void A_row_names_the_first_copy_and_counts_the_rest()
    {
        Assert.Equal("Conflict copy from the sync tool: a.dat.", AppsPage.ConflictSentence(["a.dat"]));
        Assert.Equal("Conflict copies from the sync tool: a.dat and 2 more.", AppsPage.ConflictSentence(["a.dat", "b.dat", "c.dat"]));
        Assert.Equal(new StateWord("File conflict", Shade.Amber), StateWords.For(HealthState.FileConflict, null));
    }

    private static MappingHealth Health(LocalStore local) => Assert.Single(StatusCheck.Run(local).Mappings).Health;
}
