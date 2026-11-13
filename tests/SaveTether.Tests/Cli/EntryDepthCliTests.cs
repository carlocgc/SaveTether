using SaveTether.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>Entry depth from the command line (design 5.13), on a save folder shaped like Cemu's.</summary>
public class EntryDepthCliTests
{
    private const string Id = CliDevice.MappingId;

    [Fact]
    public void Two_devices_at_depth_2_add_only_the_game_the_shared_copy_lacks()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        var deck = new CliDevice(sandbox, "deck");
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");
        pc.Ok("map", "add", Id, "--target", "ppsspp/SAVEDATA", "--source", pc.Source, "--entry-depth", "2");
        pc.Save("00050000/101c9500/game.sav", "pc zelda");
        pc.Save("system/act/account.dat", "pc account");
        pc.Ok("apply", Id, "--copy");
        pc.DeliverTo(deck);

        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        deck.Ok("map", "add", Id, "--source", deck.Source);
        deck.Save("00050000/101c9500/game.sav", "deck zelda");
        deck.Save("00050000/10143500/game.sav", "deck xenoblade");
        deck.Save("system/act/account.dat", "deck account");

        // The guard's comparison is per game, not per top-level folder.
        var comparison = deck.Expect(CliApp.ExitRefused, "apply", Id, "--json").Operation().GetProperty("refusal").GetProperty("comparison");
        Assert.Equal(
            [("00050000/10143500", "only-local"), ("00050000/101c9500", "different"), ("system/act", "different")],
            comparison.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("name").GetString(), e.GetProperty("presence").GetString())));

        var run = deck.Ok("apply", Id, "--add-missing");

        Assert.Contains("Will add 1 entry the shared copy lacks: 00050000/10143500", run.Out, StringComparison.Ordinal);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["00050000/10143500/game.sav"] = "deck xenoblade",
                ["00050000/101c9500/game.sav"] = "pc zelda",
                ["system/act/account.dat"] = "pc account",
            },
            deck.SharedFiles());

        deck.DeliverTo(pc);
        Assert.Equal("deck xenoblade", pc.LocalFiles()["00050000/10143500/game.sav"]);
        Assert.Equal("pc zelda", pc.LocalFiles()["00050000/101c9500/game.sav"]);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_depth_of_an_existing_mapping_can_be_changed_and_decides_what_add_missing_does()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("00050000/aaaa0000/game.sav", "mine");
        CliDevice.Write(pc.Target, "00050000/bbbb0000/game.sav", "theirs");
        string MappingJson() => File.ReadAllText(Path.Combine(pc.SyncRoot, "savetether", ".savetether", "mappings", Id + ".json"));

        // As first set up: depth 1, written in the format an older SaveTether still reads.
        Assert.Contains("\"schema\": 1", MappingJson(), StringComparison.Ordinal);
        var shallow = pc.Ok("apply", Id, "--add-missing", "--dry-run");
        Assert.Contains("already has every entry", shallow.Out, StringComparison.Ordinal);

        var changed = pc.Ok("map", "add", Id, "--source", pc.Source, "--process", "PPSSPPSDL", "--entry-depth", "2");
        Assert.Contains("entry depth:   2 (changed)", changed.Out, StringComparison.Ordinal);
        Assert.Contains("\"entry_depth\": 2", MappingJson(), StringComparison.Ordinal);
        Assert.Contains("\"schema\": 2", MappingJson(), StringComparison.Ordinal);

        var mapping = pc.Ok("map", "add", Id, "--source", pc.Source, "--json").Json().GetProperty("mapping");
        Assert.Equal(2, mapping.GetProperty("entry_depth").GetInt32());
        Assert.False(mapping.GetProperty("entry_depth_changed").GetBoolean());

        pc.Ok("apply", Id, "--add-missing");
        Assert.Equal(
            new Dictionary<string, string> { ["00050000/aaaa0000/game.sav"] = "mine", ["00050000/bbbb0000/game.sav"] = "theirs" },
            pc.SharedFiles());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("two")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void An_entry_depth_that_is_not_a_small_whole_number_is_a_usage_error(string value)
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");

        var run = pc.Expect(CliApp.ExitUsage, "map", "add", Id, "--target", "ppsspp/SAVEDATA", "--source", pc.Source, "--entry-depth", value);

        Assert.Contains("'--entry-depth' needs a whole number from 1 to 8", run.Err, StringComparison.Ordinal);
        Assert.Contains("no mapping", pc.Expect(CliApp.ExitRefused, "status", Id).Err, StringComparison.Ordinal);
    }
}
