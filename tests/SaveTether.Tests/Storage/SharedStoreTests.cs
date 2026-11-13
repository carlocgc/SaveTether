using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Storage;

public class SharedStoreTests
{
    private static readonly DateTimeOffset Noon = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_works_until_the_marker_folder_exists()
    {
        using var sandbox = new Sandbox();
        var store = new SharedStore(sandbox.Dir("sync"));

        Assert.False(store.IsInitialized);
        AssertProblem(StoreProblem.NotInitialized, () => store.ListMappings());
        AssertProblem(StoreProblem.NotInitialized, () => store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/SAVEDATA")));
        AssertProblem(StoreProblem.NotInitialized, () => store.ListDeviceNames());
        AssertProblem(StoreProblem.NotInitialized, () => store.ReadSeedMarkers("ppsspp.savedata"));

        // Refusing must not have created anything, or the marker check would be worthless.
        Assert.Empty(Directory.GetFileSystemEntries(store.SyncRoot));
    }

    [Fact]
    public void CreateRoot_needs_an_existing_sync_root_and_can_be_repeated()
    {
        using var sandbox = new Sandbox();

        var missing = new SharedStore(sandbox.At("unmounted"));
        AssertProblem(StoreProblem.NotFound, missing.CreateRoot);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(sandbox.At("unmounted")));

        var store = new SharedStore(sandbox.Dir("sync"));
        store.CreateRoot();
        store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/SAVEDATA"));
        store.CreateRoot();

        Assert.True(store.IsInitialized);
        Assert.Equal(Path.Combine(store.SyncRoot, "savetether", ".savetether"), store.MetaRoot);
        Assert.Single(store.ListMappings());
        AssertProblem(StoreProblem.Invalid, () => new SharedStore("relative/sync"));
    }

    [Fact]
    public void Mapping_round_trips_and_resolves_its_target_folder()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        var mapping = Mapping("ppsspp.savedata", "ppsspp/SAVEDATA");

        store.WriteMapping(mapping);

        Assert.Equal(mapping.Normalized(), store.ReadMapping("ppsspp.savedata"));
        Assert.Equal(mapping.Normalized(), store.TryReadMapping("ppsspp.savedata"));
        Assert.Null(store.TryReadMapping("eden.save"));
        AssertProblem(StoreProblem.NotFound, () => store.ReadMapping("eden.save"));
        Assert.Equal(Path.Combine(store.DataRoot, "ppsspp", "SAVEDATA"), store.TargetPathOf(mapping));

        var json = File.ReadAllText(Path.Combine(store.MetaRoot, "mappings", "ppsspp.savedata.json"));
        Assert.Contains("\"schema\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"mechanism\": \"link\"", json, StringComparison.Ordinal);
        Assert.Contains("\"target\": \"ppsspp/SAVEDATA\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteMapping_refuses_clashing_and_changed_targets()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/SAVEDATA"));

        // Rewriting it unchanged is fine.
        store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/SAVEDATA"));

        AssertProblem(StoreProblem.Conflict, () => store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/other")));
        AssertProblem(StoreProblem.Conflict, () => store.WriteMapping(Mapping("ppsspp.copy", "ppsspp/SAVEDATA")));
        AssertProblem(StoreProblem.Conflict, () => store.WriteMapping(Mapping("ppsspp.lower", "ppsspp/savedata")));
        AssertProblem(StoreProblem.Invalid, () => store.WriteMapping(Mapping("ppsspp.states", "eden/states")));
        AssertProblem(StoreProblem.Invalid, () => store.WriteMapping(Mapping("Bad.Id", "Bad/x")));

        Assert.Equal(["ppsspp.savedata"], store.ListMappings().Select(m => m.Id));
        Assert.Equal("ppsspp/SAVEDATA", store.ReadMapping("ppsspp.savedata").Target);
    }

    [Theory]
    [InlineData("{ not json", StoreProblem.Invalid)]
    [InlineData("[]", StoreProblem.Invalid)]
    [InlineData("{ \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\" }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 0, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\" }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 1, \"id\": \"ppsspp.savedata\", \"mechanism\": \"link\" }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 1, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"config\" }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 1, \"id\": \"ppsspp.savedata\", \"targett\": \"x\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\" }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 1, \"id\": \"eden.save\", \"target\": \"eden/save\", \"mechanism\": \"link\" }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 1, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/../x\", \"mechanism\": \"link\" }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 3, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\", \"new_field\": true }", StoreProblem.SchemaTooNew)]
    [InlineData("{ \"schema\": 2, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\", \"new_field\": true }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 1, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\", \"entry_depth\": 2 }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 2, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\", \"entry_depth\": 0 }", StoreProblem.Invalid)]
    [InlineData("{ \"schema\": 2, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\", \"entry_depth\": 9 }", StoreProblem.Invalid)]
    public void A_bad_mapping_file_is_refused_with_the_reason(string json, StoreProblem expected)
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        var file = Path.Combine(store.MetaRoot, "mappings", "ppsspp.savedata.json");
        File.WriteAllText(file, json);

        var error = AssertProblem(expected, () => store.ReadMapping("ppsspp.savedata"));
        Assert.Contains("ppsspp.savedata", error.Message, StringComparison.Ordinal);
        AssertProblem(expected, () => store.ListMappings());
    }

    [Fact]
    public void A_file_from_a_newer_version_is_never_rewritten()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        var newer = "{ \"schema\": 7, \"id\": \"ppsspp.savedata\", \"target\": \"ppsspp/SAVEDATA\", \"mechanism\": \"link\", \"future\": [1, 2] }";
        var mappingFile = Path.Combine(store.MetaRoot, "mappings", "ppsspp.savedata.json");
        var deviceFile = Path.Combine(store.MetaRoot, "devices", "deck.json");
        File.WriteAllText(mappingFile, newer);
        File.WriteAllText(deviceFile, "{ \"schema\": 3 }");

        AssertProblem(StoreProblem.SchemaTooNew, () => store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/SAVEDATA")));
        AssertProblem(StoreProblem.SchemaTooNew, () => store.WriteMapping(Mapping("eden.save", "eden/save")));
        AssertProblem(StoreProblem.SchemaTooNew, () => store.WriteDevice(Device("deck")));
        AssertProblem(StoreProblem.SchemaTooNew, () => DeviceIdentity.Claim(store, "deck", "/backups", takeOver: true));

        Assert.Equal(newer, File.ReadAllText(mappingFile));
        Assert.Equal("{ \"schema\": 3 }", File.ReadAllText(deviceFile));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(Path.Combine(store.MetaRoot, "mappings", "eden.save.json")));
    }

    [Fact]
    public void Hidden_and_temporary_files_are_not_listed()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/SAVEDATA"));
        store.WriteDevice(Device("deck"));
        foreach (var folder in new[] { "mappings", "devices" })
        {
            File.WriteAllText(Path.Combine(store.MetaRoot, folder, ".ppsspp.states.json.0123abcd.tmp"), "{ half written");
            File.WriteAllText(Path.Combine(store.MetaRoot, folder, ".hidden.json"), "{ not ours");
            File.WriteAllText(Path.Combine(store.MetaRoot, folder, "notes.txt"), "not json");
        }

        Assert.Equal(["ppsspp.savedata"], store.ListMappings().Select(m => m.Id));
        Assert.Equal(["deck"], store.ListDeviceNames());
    }

    [Fact]
    public void Device_file_round_trips_with_another_devices_paths_untouched()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        var deck = Device("deck") with
        {
            Paths = new Dictionary<string, DevicePath>
            {
                ["ppsspp.savedata"] = new()
                {
                    Source = "/home/deck/.var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP/SAVEDATA",
                    ProcessNames = ["PPSSPPSDL", "PPSSPPQt"],
                },
            },
        };

        // A Windows path and a Linux path must both survive on either platform: a device file is
        // only acted on by its own device, but any device may read or list it.
        var desktop = Device("desktop") with
        {
            BackupRoot = @"C:\Users\me\AppData\Local\SaveTether\backups",
            Paths = new Dictionary<string, DevicePath> { ["ppsspp.savedata"] = new() { Source = @"E:\Emulators\PPSSPP\memstick\PSP\SAVEDATA" } },
        };

        store.WriteDevice(deck);
        store.WriteDevice(desktop);

        var readDeck = store.ReadDevice("deck");
        Assert.Equal(deck.Paths["ppsspp.savedata"].Source, readDeck.Paths["ppsspp.savedata"].Source);
        Assert.Equal(["PPSSPPSDL", "PPSSPPQt"], readDeck.Paths["ppsspp.savedata"].ProcessNames);
        Assert.Equal(desktop.BackupRoot, store.ReadDevice("desktop").BackupRoot);
        Assert.Empty(store.ReadDevice("desktop").Paths["ppsspp.savedata"].ProcessNames);
        Assert.Equal(["deck", "desktop"], store.ListDeviceNames());
        Assert.Null(store.TryReadDevice("laptop"));
    }

    [Fact]
    public void Two_devices_never_touch_each_others_files()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        store.WriteMapping(Mapping("ppsspp.savedata", "ppsspp/SAVEDATA"));
        store.WriteDevice(Device("deck"));
        store.WriteSeedMarker(Seed("ppsspp.savedata", "deck"));
        var before = Snapshot(store.MetaRoot);

        // The second device sets itself up and seeds a different mapping.
        store.WriteMapping(Mapping("eden.save", "eden/save"));
        store.WriteDevice(Device("desktop"));
        store.WriteSeedMarker(Seed("eden.save", "desktop"));
        store.WriteSeedMarker(Seed("ppsspp.savedata", "desktop"));

        var after = Snapshot(store.MetaRoot);
        foreach (var (path, content) in before)
        {
            Assert.Equal(content, after[path]);
        }

        Assert.Equal(
            [
                "devices/desktop.json", "mappings/eden.save.json",
                "seed/eden.save/desktop.json", "seed/ppsspp.savedata/desktop.json",
            ],
            after.Keys.Except(before.Keys).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_bad_device_file_is_refused()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        var file = Path.Combine(store.MetaRoot, "devices", "deck.json");

        File.WriteAllText(file, "{ \"schema\": 1, \"device_name\": \"other\", \"install_id\": \"ab\", \"backup_root\": \"/b\" }");
        AssertProblem(StoreProblem.Invalid, () => store.ReadDevice("deck"));

        File.WriteAllText(file, "{ \"schema\": 1, \"device_name\": \"deck\", \"install_id\": \"ab\", \"backup_root\": \"/b\", \"paths\": { \"ppsspp.savedata\": { \"sorce\": \"/x\" } } }");
        AssertProblem(StoreProblem.Invalid, () => store.ReadDevice("deck"));

        File.WriteAllText(file, "{ \"schema\": 1, \"device_name\": \"deck\", \"install_id\": \"ab\", \"backup_root\": \"/b\", \"paths\": { \"Not An Id\": { \"source\": \"/x\" } } }");
        AssertProblem(StoreProblem.Invalid, () => store.ReadDevice("deck"));

        AssertProblem(StoreProblem.Invalid, () => store.ReadDevice("../deck"));
        AssertProblem(StoreProblem.Invalid, () => store.WriteDevice(Device("deck") with { InstallId = " " }));
    }

    [Fact]
    public void Seed_markers_round_trip_and_supersede_without_deleting()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        Assert.Empty(store.ReadSeedMarkers("ppsspp.savedata"));

        store.WriteSeedMarker(Seed("ppsspp.savedata", "deck"));
        store.WriteSeedMarker(Seed("ppsspp.savedata", "desktop"));
        store.WriteSeedMarker(Seed("eden.save", "deck"));

        var both = store.ReadSeedMarkers("ppsspp.savedata");
        Assert.Equal(["deck", "desktop"], both.Select(m => m.DeviceName));
        Assert.All(both, m => Assert.True(m.IsActive));
        Assert.Equal([new SeedFile("ULUS10041/DATA.BIN", 4096), new SeedFile("readme.txt", 12)], both[0].Files);
        Assert.Equal(Noon, both[0].SeededUtc);

        // The desktop replaces the shared copy: the Deck's marker stops counting, but stays on disk.
        var later = Noon.AddHours(2);
        Assert.Equal(["deck"], store.SupersedeOtherMarkers("ppsspp.savedata", "desktop", later));

        var after = store.ReadSeedMarkers("ppsspp.savedata");
        Assert.Equal(["deck", "desktop"], after.Select(m => m.DeviceName));
        Assert.Equal(["desktop"], after.Where(m => m.IsActive).Select(m => m.DeviceName));
        Assert.Equal("desktop", after[0].SupersededBy);
        Assert.Equal(later, after[0].SupersededUtc);
        Assert.Equal(both[0].Files, after[0].Files);

        // Doing it again changes nothing, and other mappings are unaffected.
        Assert.Empty(store.SupersedeOtherMarkers("ppsspp.savedata", "desktop", later.AddHours(1)));
        Assert.True(store.ReadSeedMarkers("eden.save").Single().IsActive);
    }

    [Fact]
    public void A_seed_marker_in_the_wrong_place_is_refused()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        store.WriteSeedMarker(Seed("ppsspp.savedata", "deck"));
        var folder = Path.Combine(store.MetaRoot, "seed", "ppsspp.savedata");
        File.Copy(Path.Combine(folder, "deck.json"), Path.Combine(folder, "desktop.json"));

        AssertProblem(StoreProblem.Invalid, () => store.ReadSeedMarkers("ppsspp.savedata"));
    }

    internal static SharedStore NewStore(Sandbox sandbox)
    {
        var store = new SharedStore(sandbox.Dir("sync"));
        store.CreateRoot();
        return store;
    }

    internal static StoreException AssertProblem(StoreProblem expected, Action action)
    {
        var error = Assert.Throws<StoreException>(action);
        Assert.Equal(expected, error.Problem);
        return error;
    }

    internal static StoreException AssertProblem(StoreProblem expected, Func<object?> action) =>
        AssertProblem(expected, () => { action(); });

    internal static MappingFile Mapping(string id, string target) =>
        new() { Schema = MappingFile.CurrentSchema, Id = id, Target = target, Mechanism = Mechanism.Link };

    internal static DeviceFile Device(string name) =>
        new() { Schema = DeviceFile.CurrentSchema, DeviceName = name, InstallId = "0123456789abcdef", BackupRoot = "/home/deck/backups" };

    private static SeedMarker Seed(string mappingId, string device) => new()
    {
        Schema = SeedMarker.CurrentSchema,
        MappingId = mappingId,
        DeviceName = device,
        SeededUtc = Noon,
        Files = [new SeedFile("ULUS10041/DATA.BIN", 4096), new SeedFile("readme.txt", 12)],
    };

    private static Dictionary<string, string> Snapshot(string folder) =>
        SafeFs.EnumerateTree(folder)
            .Where(e => e.Kind == EntryKind.File)
            .ToDictionary(e => e.RelativePath, e => File.ReadAllText(Path.Combine(folder, e.RelativePath)));
}
