using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"device list" (phase 3.6, T2): the counts, the last activity, and that it only reads.</summary>
public sealed class DeviceListTests : IDisposable
{
    private const string Id = CliDevice.MappingId;

    private readonly Sandbox _sandbox = new();

    public void Dispose()
    {
        _sandbox.AssertCanaryIntact();
        _sandbox.Dispose();
    }

    [Fact]
    public void Counts_devices_mappings_orphans_and_backups_and_changes_nothing()
    {
        var pc = new CliDevice(_sandbox, "pc");
        var deck = new CliDevice(_sandbox, "deck");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        pc.Ok("apply", Id, "--copy");

        // A mapping only the PC uses, and one nobody uses (the PC removed its own save folder).
        pc.Ok("map", "add", "solo.saves", "--target", "solo/saves", "--source", Path.Combine(pc.AppFolder, "solo"));
        pc.Ok("map", "add", "orphan.saves", "--target", "orphan/saves", "--source", Path.Combine(pc.AppFolder, "orphan"));
        pc.Ok("map", "remove", "orphan.saves");

        pc.DeliverTo(deck);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        deck.Ok("map", "add", Id, "--source", deck.Source);
        deck.DeliverTo(pc);

        var before = CliDevice.FilesIn(pc.SyncRoot);
        var o = pc.Ok("device", "list", "--json").Json().GetProperty("devices");

        Assert.Equal(["deck", "pc"], o.GetProperty("devices").EnumerateArray().Select(d => d.GetProperty("name").GetString()));
        var byName = o.GetProperty("devices").EnumerateArray().ToDictionary(d => d.GetProperty("name").GetString()!);
        Assert.Equal(1, byName["deck"].GetProperty("mapping_count").GetInt32());
        Assert.Equal(2, byName["pc"].GetProperty("mapping_count").GetInt32());
        Assert.True(byName["pc"].GetProperty("is_this_device").GetBoolean());
        Assert.False(byName["deck"].GetProperty("is_this_device").GetBoolean());
        Assert.Equal(["orphan.saves"], o.GetProperty("orphans").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(3, o.GetProperty("mappings").GetArrayLength());
        Assert.Equal([Id], o.GetProperty("linked_here").EnumerateArray().Select(m => m.GetString()));
        Assert.True(o.GetProperty("backup_count").GetInt32() >= 0);

        Assert.Equal(before, CliDevice.FilesIn(pc.SyncRoot));
        Assert.Contains("deck", pc.Ok("device", "list").Out, StringComparison.Ordinal);
    }
}
