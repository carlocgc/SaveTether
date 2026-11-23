using System.Text.Json;
using SaveTether.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"device remove --other" and the tombstone checks (phase 3.6, T6).</summary>
public sealed class DeviceRemoveOtherTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose()
    {
        _sandbox.AssertCanaryIntact();
        _sandbox.Dispose();
    }

    private static string Meta(CliDevice d) => Path.Combine(d.SyncRoot, "savetether", ".savetether");

    private static string Tombstone(CliDevice d, string name) => Path.Combine(Meta(d), "removed", name + ".json");

    /// <summary>The PC has an applied mapping that the deck shares; both have the same sync folder content.</summary>
    private (CliDevice Pc, CliDevice Deck) Devices()
    {
        var pc = new CliDevice(_sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        pc.Ok("apply", CliDevice.MappingId, "--copy");

        var deck = new CliDevice(_sandbox, "deck");
        pc.DeliverTo(deck);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        deck.Ok("map", "add", CliDevice.MappingId, "--source", deck.Source);
        deck.DeliverTo(pc);
        return (pc, deck);
    }

    [Fact]
    public void Removing_another_device_deletes_its_files_and_writes_its_tombstone_and_nothing_else()
    {
        var (pc, deck) = Devices();
        var deckFile = File.ReadAllText(Path.Combine(Meta(deck), "devices", "deck.json"));
        var shared = CliDevice.FilesIn(deck.Target);

        var dry = deck.Ok("device", "remove", "--other", "pc", "--json").Json().GetProperty("removal");
        Assert.True(dry.GetProperty("dry_run").GetBoolean());
        Assert.Equal(2, dry.GetProperty("shared_files").GetArrayLength());
        Assert.True(File.Exists(Path.Combine(Meta(deck), "devices", "pc.json")));

        deck.Ok("device", "remove", "--other", "pc", "--confirm-remove");

        Assert.False(File.Exists(Path.Combine(Meta(deck), "devices", "pc.json")));
        Assert.False(File.Exists(Path.Combine(Meta(deck), "seed", CliDevice.MappingId, "pc.json")));
        Assert.Contains("\"removed_by\": \"deck\"", File.ReadAllText(Tombstone(deck, "pc")), StringComparison.Ordinal);
        Assert.Equal(deckFile, File.ReadAllText(Path.Combine(Meta(deck), "devices", "deck.json")));
        Assert.Equal(shared, CliDevice.FilesIn(deck.Target));
        Assert.True(pc.IsLinked); // the other device's own links are not touched
    }

    [Fact]
    public void This_device_and_an_unknown_name_are_refused_and_change_nothing()
    {
        var (_, deck) = Devices();
        var before = CliDevice.FilesIn(deck.SyncRoot);

        Assert.Equal(CliApp.ExitRefused, deck.Run("device", "remove", "--other", "deck", "--confirm-remove").Exit);
        Assert.NotEqual(CliApp.ExitSuccess, deck.Run("device", "remove", "--other", "nobody", "--confirm-remove").Exit);
        Assert.Equal(CliApp.ExitUsage, deck.Run("device", "remove", "--other", "pc", "--delete-backups").Exit);

        Assert.Equal(before, CliDevice.FilesIn(deck.SyncRoot));
    }

    [Fact]
    public void A_device_removed_by_another_sees_it_and_finishes_the_removal_here()
    {
        var (pc, deck) = Devices();
        deck.Ok("device", "remove", "--other", "pc", "--confirm-remove");
        deck.DeliverTo(pc, mirror: true);

        var status = pc.Ok("status", "--json").Json().GetProperty("status");
        Assert.Equal("removed-by-another", status.GetProperty("removal").GetString());
        Assert.Equal("deck", status.GetProperty("removed_by").GetString());

        Directory.Delete(pc.BackupRoot, recursive: true); // found on Mint: the device file that named it is gone, and a revert refuses a missing folder
        pc.Ok("device", "remove", "--confirm-remove");

        Assert.False(pc.IsLinked);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(pc.Source, "GAME1", "save.bin")));
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "state.json")));
        Assert.Contains("\"removed_by\": \"deck\"", File.ReadAllText(Tombstone(pc, "pc")), StringComparison.Ordinal); // not rewritten
        Assert.False(File.Exists(Path.Combine(Meta(pc), "devices", "pc.json")));
    }

    [Fact]
    public void A_removal_is_finished_even_when_a_mapping_record_was_deleted_meanwhile()
    {
        var (pc, deck) = Devices();
        deck.Ok("device", "remove", "--other", "pc", "--confirm-remove");
        deck.DeliverTo(pc, mirror: true);
        File.Delete(Path.Combine(Meta(pc), "mappings", CliDevice.MappingId + ".json"));

        var o = pc.Ok("device", "remove", "--confirm-remove", "--json").Json().GetProperty("removal");

        Assert.Equal(CliDevice.MappingId, o.GetProperty("not_linked")[0].GetString());
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
    }

    [Fact]
    public void A_file_that_comes_back_is_flagged_by_status_and_device_list()
    {
        var (pc, deck) = Devices();
        deck.Ok("device", "remove", "--other", "pc", "--confirm-remove");
        pc.DeliverTo(deck); // the PC's sync tool puts the file back

        Assert.True(File.Exists(Path.Combine(Meta(deck), "devices", "pc.json")));
        var status = deck.Ok("status", "--json").Json().GetProperty("status");
        Assert.Equal("pc", status.GetProperty("returned_devices")[0].GetString());
        Assert.False(status.TryGetProperty("removal", out var removal) && removal.ValueKind != JsonValueKind.Null);

        var devices = deck.Ok("device", "list", "--json").Json().GetProperty("devices").GetProperty("devices");
        Assert.Contains(devices.EnumerateArray(), d => d.GetProperty("name").GetString() == "pc" && d.GetProperty("tombstoned").GetBoolean());
    }

    [Fact]
    public void A_removal_that_stopped_is_reported_as_not_finished_and_a_run_again_finishes_it()
    {
        var (pc, _) = Devices();
        var bootstrap = File.ReadAllText(Path.Combine(pc.LocalFolder, "bootstrap.json"));
        pc.Ok("device", "remove", "--confirm-remove");

        // The state a crash before the local files were deleted leaves: the bootstrap is still there, the shared files are gone.
        File.WriteAllText(Path.Combine(pc.LocalFolder, "bootstrap.json"), bootstrap);
        var status = pc.Ok("status", "--json").Json().GetProperty("status");
        Assert.Equal("unfinished", status.GetProperty("removal").GetString());
        Assert.Equal("pc", status.GetProperty("removed_by").GetString());

        pc.Ok("device", "remove", "--confirm-remove");
        Assert.False(File.Exists(Path.Combine(pc.LocalFolder, "bootstrap.json")));
    }

    [Fact]
    public void A_new_install_taking_a_removed_name_clears_its_tombstone()
    {
        var (pc, _) = Devices();
        pc.Ok("device", "remove", "--confirm-remove");
        Assert.True(File.Exists(Tombstone(pc, "pc")));

        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc");

        Assert.False(File.Exists(Tombstone(pc, "pc")));
        Assert.Equal(CliApp.ExitSuccess, pc.Run("status").Exit);
    }
}
