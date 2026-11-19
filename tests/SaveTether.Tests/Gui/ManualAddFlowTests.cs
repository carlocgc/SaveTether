using SaveTether.Core.FileSystem;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 8 of the desktop app spec, "Add a folder by hand": the form's logic, driven against fixture devices.</summary>
public class ManualAddFlowTests
{
    private static (CliDevice Device, ManualAddFlow Flow) NewDevice(Sandbox sandbox, string name, bool createRoot)
    {
        var device = new CliDevice(sandbox, name);
        device.Ok(createRoot
            ? ["init", "--sync-root", device.SyncRoot, "--device", name, "--create-root"]
            : ["init", "--sync-root", device.SyncRoot, "--device", name]);
        return (device, new ManualAddFlow(new LocalStore(device.LocalFolder)));
    }

    private static string MappingFile(CliDevice device, string id) =>
        File.ReadAllText(Path.Combine(device.SyncRoot, "savetether", ".savetether", "mappings", id + ".json"));

    private static string DeviceFile(CliDevice device) =>
        File.ReadAllText(Path.Combine(device.SyncRoot, "savetether", ".savetether", "devices", device.Name + ".json"));

    [Theory]
    [InlineData("Rondo Duo", "Flash Player", "rondo-duo.flash-player", "rondo-duo/Flash Player")]
    [InlineData("  Pokémon Café ", "SAVEDATA", "pokemon-cafe.savedata", "pokemon-cafe/SAVEDATA")]
    [InlineData("ppsspp", "savedata", "ppsspp.savedata", "ppsspp/savedata")]
    [InlineData("A & B", "Saves (1)", "a-b.saves-1", "a-b/Saves (1)")]
    [InlineData("Straße Æon", "Spielstände", "strasse-aeon.spielstande", "strasse-aeon/Spielstände")]
    [InlineData("ペルソナ", "saves", null, null)]
    [InlineData("!!!", "saves", null, null)]
    [InlineData("App", "   ", null, null)]
    [InlineData(null, "saves", null, null)]
    public void Two_names_in_words_give_the_mapping_ID_and_the_shared_folder(string? app, string? folder, string? id, string? target)
    {
        Assert.Equal((id, target), ManualAddFlow.Derive(app, folder));
    }

    [Fact]
    public void The_form_suggests_the_folder_name_and_splits_program_names()
    {
        Assert.Equal("Flash Player", ManualAddFlow.SuggestFolderName(Path.Combine("x", "Flash Player") + Path.DirectorySeparatorChar));
        Assert.Null(ManualAddFlow.SuggestFolderName("  "));
        Assert.Equal(["Game", "Game Launcher", "other"], ManualAddFlow.SplitProcessNames("Game, Game Launcher;\nother,,Game"));
        Assert.Empty(ManualAddFlow.SplitProcessNames(null));
        Assert.Equal("Shared as a/B (mapping a.b).", ManualAddFlow.SharedAs("a.b", "a/B"));
    }

    [Fact]
    public void The_saves_name_follows_the_folder_as_it_is_typed_until_the_owner_types_one()
    {
        // Typing a path a character at a time, as on the Deck: the name follows to the folder's own name.
        string? name = string.Empty;
        string? last = string.Empty;
        var typed = string.Empty;
        foreach (var c in "/home/deck/games/Flash Player")
        {
            typed += c;
            var next = ManualAddFlow.FollowFolder(name, last, typed);
            if (next != name)
            {
                last = next;
                name = next;
            }
        }

        Assert.Equal("Flash Player", name);

        // A name the owner typed is kept when the folder changes; an emptied field follows again.
        Assert.Equal("Saves", ManualAddFlow.FollowFolder("Saves", "Flash Player", "/home/deck/games/Other"));
        Assert.Equal("Other", ManualAddFlow.FollowFolder(string.Empty, "Saves", "/home/deck/games/Other"));
    }

    [Fact]
    public void A_new_folder_is_checked_field_by_field_and_nothing_is_written_until_it_passes()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox, "deck", createRoot: true);
        var start = flow.StartNew();
        Assert.False(start.IsJoin);
        Assert.Equal(1, start.Input.EntryDepth);

        var empty = flow.Check(start.Input);
        Assert.False(empty.CanAdd);
        Assert.Equal(
            [ManualField.SaveFolder, ManualField.AppName, ManualField.FolderName],
            empty.Problems.Select(p => p.Field).Order());

        var bad = flow.Check(new ManualAddInput("relative/saves", "Game", "saves", "C:/Games/game.exe", 9));
        Assert.Contains(bad.Problems, p => p.Field == ManualField.SaveFolder && p.Problem.Contains("not a full path", StringComparison.Ordinal));
        Assert.Contains(bad.Problems, p => p.Field == ManualField.ProcessNames);
        Assert.Contains(bad.Problems, p => p.Field == ManualField.EntryDepth);

        // A name Windows cannot use as a folder is refused in the form's own words.
        var reserved = flow.Check(new ManualAddInput(sandbox.Dir("saves"), "Game", "CON", null, 1));
        Assert.Contains(reserved.Problems, p => p.Field == ManualField.FolderName && p.Problem.StartsWith("The name cannot be used", StringComparison.Ordinal));

        // Add checks again under the lock and writes nothing while a problem remains.
        var result = flow.Add(new ManualAddInput("relative", "Game", "saves", null, 1), joinAccepted: false);
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Check);
        Assert.False(File.Exists(Path.Combine(deck.SyncRoot, "savetether", ".savetether", "mappings", "game.saves.json")));
    }

    [Fact]
    public void Add_writes_the_mapping_and_this_devices_folder_and_links_nothing()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox, "deck", createRoot: true);
        var saves = sandbox.Dir("deck", "home", "Flash Player");
        File.WriteAllText(Path.Combine(saves, "save.sol"), "progress");
        flow.StartNew();

        var input = new ManualAddInput(saves, "Rondo Duo", "Flash Player", "Rondo Duo, projector", 2);
        var check = flow.Check(input);
        Assert.True(check.CanAdd);
        Assert.Equal("rondo-duo.flash-player", check.MappingId);
        Assert.False(check.JoinsExisting);

        var result = flow.Add(input, joinAccepted: false);
        Assert.True(result.Succeeded, result.Refused);
        Assert.Equal("rondo-duo.flash-player", result.Added);
        var mapping = MappingFile(deck, "rondo-duo.flash-player");
        Assert.Contains("\"rondo-duo/Flash Player\"", mapping, StringComparison.Ordinal);
        Assert.Contains("\"entry_depth\": 2", mapping, StringComparison.Ordinal);
        var device = DeviceFile(deck);
        Assert.Contains("projector", device, StringComparison.Ordinal);
        Assert.Contains("Rondo Duo", device, StringComparison.Ordinal);

        // Nothing is linked: the folder is still a real folder holding its file.
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(saves));
        Assert.Equal("progress", File.ReadAllText(Path.Combine(saves, "save.sol")));

        // Added again under the same names: this device already has it, so the form says to use its row.
        var again = flow.Check(input);
        Assert.Contains(again.Problems, p => p.Field == ManualField.FolderName && p.Problem.Contains("already has a folder", StringComparison.Ordinal));
    }

    [Fact]
    public void Add_is_refused_while_another_operation_holds_the_lock_and_a_core_refusal_is_said()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox, "deck", createRoot: true);
        flow.StartNew();
        var outer = sandbox.Dir("deck", "home", "Game");
        var input = new ManualAddInput(outer, "Game", "Game", null, 1);

        using (OperationLock.Acquire(new LocalStore(deck.LocalFolder), "cli", "apply"))
        {
            var held = flow.Add(input, joinAccepted: false);
            Assert.False(held.Succeeded);
            Assert.Contains("Another SaveTether operation is running", held.Refused, StringComparison.Ordinal);
        }

        Assert.True(flow.Add(input, joinAccepted: false).Succeeded);

        // A folder inside another mapping's folder: the core refuses, and nothing is written.
        var inner = sandbox.Dir("deck", "home", "Game", "Saves");
        var nested = flow.Add(new ManualAddInput(inner, "Game", "Saves", null, 1), joinAccepted: false);
        Assert.False(nested.Succeeded);
        Assert.Null(nested.Check);
        Assert.False(string.IsNullOrWhiteSpace(nested.Refused));
        Assert.DoesNotContain("game.saves", DeviceFile(deck), StringComparison.Ordinal);
    }

    [Fact]
    public void Another_device_adds_the_same_folder_by_its_row_or_by_the_same_names()
    {
        using var sandbox = new Sandbox();
        var (pc, pcFlow) = NewDevice(sandbox, "pc", createRoot: true);
        pcFlow.StartNew();
        Assert.True(pcFlow.Add(new ManualAddInput(sandbox.Dir("pc", "home", "Saves"), "Some Game", "Saves", null, 3), joinAccepted: false).Succeeded);
        var deck = new CliDevice(sandbox, "deck");
        pc.DeliverTo(deck, metadataOnly: true);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        var deckFlow = new ManualAddFlow(new LocalStore(deck.LocalFolder));

        // By its row: join mode, with the mapping's names and depth, and only the folder to give.
        var join = deckFlow.StartJoin("some-game.saves");
        Assert.True(join.IsJoin);
        Assert.Equal("some-game/Saves", join.Target);
        Assert.Equal(["pc"], join.SetUpOn);
        Assert.Equal(3, join.Input.EntryDepth);
        Assert.Null(join.Input.SaveFolder);
        var folder = sandbox.Dir("deck", "home", "Saves");
        var joined = deckFlow.Add(join.Input with { SaveFolder = folder, EntryDepth = 1 }, joinAccepted: false);
        Assert.True(joined.Succeeded, joined.Refused);
        Assert.Contains("\"entry_depth\": 3", MappingFile(deck, "some-game.saves"), StringComparison.Ordinal);
        Assert.Contains(Path.GetFileName(folder), DeviceFile(deck), StringComparison.Ordinal);

        // After Stop using here, by the same names from the new-folder form: it asks first, and the depth stays the mapping's.
        var laptopFlow = new ManualAddFlow(new LocalStore(deck.LocalFolder));
        deck.Ok("map", "remove", "some-game.saves");
        laptopFlow.StartNew();
        var same = new ManualAddInput(folder, "Some Game", "Saves", null, 5);
        var check = laptopFlow.Check(same);
        Assert.True(check is { CanAdd: true, JoinsExisting: true, ExistingDepth: 3 });
        Assert.Contains("some-game/Saves", ManualAddFlow.JoinQuestion(check), StringComparison.Ordinal);
        var asked = laptopFlow.Add(same, joinAccepted: false);
        Assert.False(asked.Succeeded);
        Assert.True(asked.Check?.JoinsExisting);
        Assert.True(laptopFlow.Add(same, joinAccepted: true).Succeeded);
        Assert.Contains("\"entry_depth\": 3", MappingFile(deck, "some-game.saves"), StringComparison.Ordinal);

        // The same app and saves names with another letter case would be a second shared folder: refused.
        laptopFlow.StartNew();
        var other = laptopFlow.Check(new ManualAddInput(sandbox.Dir("deck", "home", "Other"), "Some Game", "saves", null, 1));
        Assert.Contains(other.Problems, p => p.Field == ManualField.FolderName && p.Problem.Contains("already exists", StringComparison.Ordinal));
    }
}
