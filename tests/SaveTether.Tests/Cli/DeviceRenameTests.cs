using SaveTether.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"device rename" (phase 3.6, T3): the new files, the refusals, and finishing an unfinished rename.</summary>
public sealed class DeviceRenameTests : IDisposable
{
    private const string Id = CliDevice.MappingId;

    private readonly Sandbox _sandbox = new();

    public void Dispose()
    {
        _sandbox.AssertCanaryIntact();
        _sandbox.Dispose();
    }

    private static string Meta(CliDevice d) => Path.Combine(d.SyncRoot, "savetether", ".savetether");

    private CliDevice AppliedPc()
    {
        var pc = new CliDevice(_sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        pc.Ok("apply", Id, "--copy");
        return pc;
    }

    [Fact]
    public void Renames_the_device_file_the_markers_and_the_bootstrap_and_stays_healthy()
    {
        var pc = AppliedPc();
        var deck = new CliDevice(_sandbox, "deck");
        pc.DeliverTo(deck);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");

        Assert.Contains("pc2", pc.Ok("device", "rename", "pc2").Out, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(Meta(pc), "devices", "pc2.json")));
        Assert.False(File.Exists(Path.Combine(Meta(pc), "devices", "pc.json")));
        Assert.True(File.Exists(Path.Combine(Meta(pc), "seed", Id, "pc2.json")));
        Assert.False(File.Exists(Path.Combine(Meta(pc), "seed", Id, "pc.json")));
        Assert.Contains("\"pc2\"", File.ReadAllText(Path.Combine(pc.LocalFolder, "bootstrap.json")), StringComparison.Ordinal);
        Assert.Contains("\"device_name\": \"pc2\"", File.ReadAllText(Path.Combine(Meta(pc), "seed", Id, "pc2.json")), StringComparison.Ordinal);
        Assert.Equal("ok", pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0].GetProperty("state").GetString());
        Assert.Equal("pc2", pc.Ok("device", "list", "--json").Json().GetProperty("devices").GetProperty("devices")
            .EnumerateArray().Single(d => d.GetProperty("is_this_device").GetBoolean()).GetProperty("name").GetString());
    }

    [Fact]
    public void Refusals_change_nothing()
    {
        var pc = AppliedPc();
        var deck = new CliDevice(_sandbox, "deck");
        pc.DeliverTo(deck);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        deck.DeliverTo(pc);
        var before = CliDevice.FilesIn(pc.SyncRoot);

        Assert.Equal(CliApp.ExitRefused, pc.Run("device", "rename", "deck").Exit); // taken, by another install
        Assert.Equal(CliApp.ExitRefused, pc.Run("device", "rename", "Not Valid").Exit);
        Assert.Equal(before, CliDevice.FilesIn(pc.SyncRoot));

        File.WriteAllText(Path.Combine(pc.LocalFolder, "journal.json"), "{}");
        Assert.Equal(CliApp.ExitRecoveryNeeded, pc.Run("device", "rename", "pc2").Exit);
        File.Delete(Path.Combine(pc.LocalFolder, "journal.json"));
        Assert.Equal(before, CliDevice.FilesIn(pc.SyncRoot));
        Assert.True(File.Exists(Path.Combine(Meta(pc), "devices", "pc.json")));
    }

    [Fact]
    public void An_unfinished_rename_is_finished_by_running_it_again_and_leftovers_after_the_commit_are_cleared()
    {
        var pc = AppliedPc();
        var devices = Path.Combine(Meta(pc), "devices");
        var marker = Path.Combine(Meta(pc), "seed", Id);

        // Crash after the new files were written: two device files with this install's ID, the bootstrap on the old name.
        File.WriteAllText(Path.Combine(devices, "pc2.json"), File.ReadAllText(Path.Combine(devices, "pc.json")).Replace("\"pc\"", "\"pc2\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(marker, "pc2.json"), File.ReadAllText(Path.Combine(marker, "pc.json")).Replace("\"pc\"", "\"pc2\"", StringComparison.Ordinal));
        pc.Ok("device", "rename", "pc2");
        Assert.Equal(["pc2.json"], Directory.GetFiles(devices).Select(Path.GetFileName));
        Assert.Equal(["pc2.json"], Directory.GetFiles(marker).Select(Path.GetFileName));

        // Crash after the bootstrap was written: the old files are still there. The same name again clears them.
        File.WriteAllText(Path.Combine(devices, "pc.json"), File.ReadAllText(Path.Combine(devices, "pc2.json")).Replace("\"pc2\"", "\"pc\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(marker, "pc.json"), File.ReadAllText(Path.Combine(marker, "pc2.json")).Replace("\"pc2\"", "\"pc\"", StringComparison.Ordinal));
        pc.Ok("device", "rename", "pc2");
        Assert.Equal(["pc2.json"], Directory.GetFiles(devices).Select(Path.GetFileName));
        Assert.Equal(["pc2.json"], Directory.GetFiles(marker).Select(Path.GetFileName));
    }

    [Fact]
    public void Refused_while_the_sync_folder_cannot_be_reached_and_an_unreadable_device_file_does_not_stop_the_cleanup()
    {
        var pc = AppliedPc();
        var aside = _sandbox.At("pc", "sync-unplugged");
        Directory.Move(pc.SyncRoot, aside);
        Assert.Equal(CliApp.ExitRefused, pc.Run("device", "rename", "pc2").Exit);
        Assert.Contains("\"pc\"", File.ReadAllText(Path.Combine(pc.LocalFolder, "bootstrap.json")), StringComparison.Ordinal);
        Directory.Move(aside, pc.SyncRoot);

        File.WriteAllText(Path.Combine(Meta(pc), "devices", "other.json"), "not json");
        pc.Ok("device", "rename", "pc2");
        Assert.Equal(["other.json", "pc2.json"], Directory.GetFiles(Path.Combine(Meta(pc), "devices")).Select(Path.GetFileName).Order());
    }
}
