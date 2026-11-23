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
        Assert.Equal(1, o.GetProperty("backup_count").GetInt32());
        Assert.Equal(4, o.GetProperty("backup_bytes").GetInt64()); // "mine", the one file the apply backed up
        Assert.Equal([Id, "solo.saves"], o.GetProperty("mappings_here").EnumerateArray().Select(m => m.GetString()));

        Assert.Equal(before, CliDevice.FilesIn(pc.SyncRoot));
        Assert.Contains("deck", pc.Ok("device", "list").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Last_activity_is_the_newest_of_the_device_file_and_its_markers_and_a_bad_marker_is_not_fatal()
    {
        var pc = new CliDevice(_sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        pc.Ok("apply", Id, "--copy");

        var meta = Path.Combine(pc.SyncRoot, "savetether", ".savetether");
        var deviceFile = Path.Combine(meta, "devices", "pc.json");
        var marker = Path.Combine(meta, "seed", Id, "pc.json");
        string Activity() => pc.Ok("device", "list", "--json").Json()
            .GetProperty("devices").GetProperty("devices")[0].GetProperty("last_activity_utc").GetString()!;

        // The marker was written later than the device file.
        File.SetLastWriteTimeUtc(deviceFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var seeded = DateTimeOffset.Parse(
            System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker)).RootElement.GetProperty("seeded_utc").GetString()!);
        Assert.Equal(seeded, DateTimeOffset.Parse(Activity()));

        // The device file is newer than the marker.
        File.SetLastWriteTimeUtc(deviceFile, seeded.UtcDateTime.AddDays(1));
        Assert.Equal(seeded.AddDays(1), DateTimeOffset.Parse(Activity()));

        // Another device's marker that cannot be read does not stop the list.
        var other = Path.Combine(meta, "seed", Id, "deck.json");
        File.WriteAllText(other, "not json");
        Assert.Equal(seeded.AddDays(1), DateTimeOffset.Parse(Activity()));
    }
}
