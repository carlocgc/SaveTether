using SaveTether.Cli;
using SaveTether.Core.FileSystem;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>
/// The two-device scenarios of the implementation plan (M6). Each device has its own local
/// folder and its own copy of the sync root; <see cref="CliDevice.DeliverTo"/> stands in for
/// the sync tool, so "not delivered yet" can be staged.
/// </summary>
public sealed class TwoDeviceTests : IDisposable
{
    private const string Id = CliDevice.MappingId;

    private readonly Sandbox _sandbox = new();
    private readonly CliDevice _pc;
    private readonly CliDevice _deck;

    public TwoDeviceTests()
    {
        _pc = new CliDevice(_sandbox, "pc");
        _deck = new CliDevice(_sandbox, "deck");
    }

    public void Dispose()
    {
        _sandbox.AssertCanaryIntact();
        _sandbox.Dispose();
    }

    [Fact]
    public void Scenario_1_the_second_device_stops_at_the_guard_and_no_copy_links_it()
    {
        SeedFromPc(("GAME1/save.bin", "pc-1"), ("GAME2/save.bin", "pc-2"));
        _pc.DeliverTo(_deck);
        JoinDeck();
        _deck.Save("GAME1/save.bin", "deck-1");

        // The Deck has saves of its own, so it stops and shows what differs.
        var guard = _deck.Expect(CliApp.ExitRefused, "apply", Id, "--json").Operation().GetProperty("refusal");
        Assert.Equal("guard", guard.GetProperty("classification").GetString());
        Assert.Equal("choice-needed", guard.GetProperty("kind").GetString());
        Assert.Equal(
            ["no-copy", "add-missing", "replace-shared"],
            guard.GetProperty("allowed_choices").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(2, guard.GetProperty("comparison").GetProperty("entries").GetArrayLength());
        Assert.Contains("--no-copy | --add-missing | --replace-shared --confirm-replace", _deck.Run("apply", Id).Err, StringComparison.Ordinal);

        // Copying over the shared saves is refused outright.
        var copy = _deck.Expect(CliApp.ExitRefused, "apply", Id, "--copy", "--json").Operation().GetProperty("refusal");
        Assert.Equal("choice-not-allowed", copy.GetProperty("kind").GetString());
        Assert.False(_deck.IsLinked);
        Assert.Equal("deck-1", _deck.LocalFiles()["GAME1/save.bin"]);

        _deck.Ok("apply", Id, "--no-copy");

        Assert.True(_deck.IsLinked);
        Assert.Equal(_pc.SharedFiles(), _deck.LocalFiles());
        var backupId = _deck.Ok("backup", "list", "--json").Json().GetProperty("backups")[0].GetProperty("id").GetString()!;
        Assert.Equal("deck-1", CliDevice.FilesIn(Path.Combine(_deck.BackupRoot, backupId, "data"))["GAME1/save.bin"]);
        AssertState("ok", _pc);
        AssertState("ok", _deck);

        // A save made on the Deck reaches the PC through its link once the sync tool delivers it.
        _deck.Save("GAME3/save.bin", "played on the deck");
        _deck.DeliverTo(_pc);
        Assert.Equal("played on the deck", _pc.LocalFiles()["GAME3/save.bin"]);
    }

    [Fact]
    public void Scenario_2_add_missing_brings_only_what_the_shared_copy_lacks()
    {
        SeedFromPc(("GAME1/save.bin", "pc-1"));
        _pc.DeliverTo(_deck);
        JoinDeck();
        _deck.Save("GAME1/save.bin", "deck-1");
        _deck.Save("GAME2/save.bin", "deck-only");

        var run = _deck.Ok("apply", Id, "--add-missing");

        Assert.Contains("Will add 1 entry the shared copy lacks: GAME2", run.Out, StringComparison.Ordinal);
        Assert.Contains("the shared version is kept", run.Out, StringComparison.Ordinal);
        Assert.Equal(
            new Dictionary<string, string> { ["GAME1/save.bin"] = "pc-1", ["GAME2/save.bin"] = "deck-only" },
            _deck.SharedFiles());

        _deck.DeliverTo(_pc);
        Assert.Equal("deck-only", _pc.LocalFiles()["GAME2/save.bin"]);
        Assert.Equal("pc-1", _pc.LocalFiles()["GAME1/save.bin"]);
    }

    [Fact]
    public void Scenario_3_a_device_that_has_the_marker_but_not_yet_the_saves_stops()
    {
        SeedFromPc(("GAME1/save.bin", "pc-1"));
        _pc.DeliverTo(_deck, metadataOnly: true);
        JoinDeck();

        // Whatever the Deck has locally, and whatever it asks for, an empty shared folder with a seed marker stops it.
        var empty = _deck.Expect(CliApp.ExitRefused, "apply", Id, "--json").Operation().GetProperty("refusal");
        Assert.Equal("not-arrived", empty.GetProperty("kind").GetString());
        Assert.True(empty.GetProperty("assume_synced_would_help").GetBoolean());
        _deck.Save("GAME1/save.bin", "deck-1");
        _deck.Expect(CliApp.ExitRefused, "apply", Id, "--copy");
        Assert.Contains("--assume-synced", _deck.Expect(CliApp.ExitRefused, "apply", Id, "--no-copy").Err, StringComparison.Ordinal);
        Assert.False(_deck.IsLinked);

        _pc.DeliverTo(_deck);
        _deck.Ok("apply", Id, "--no-copy");
        Assert.Equal("pc-1", _deck.LocalFiles()["GAME1/save.bin"]);
    }

    [Fact]
    public void Scenario_4_two_devices_that_both_seeded_are_a_double_seed_until_one_is_declared_the_winner()
    {
        _pc.SetUp(createRoot: true);
        _pc.DeliverTo(_deck);
        JoinDeck();
        _pc.Save("GAME1/save.bin", "pc-1");
        _deck.Save("GAME1/save.bin", "deck-1");

        // Neither has received anything from the other, so each sees an empty shared folder and no marker.
        _pc.Ok("apply", Id, "--copy");
        _deck.Ok("apply", Id, "--copy");

        // The sync tool then delivers both ways. Here the PC's file happens to win the name clash.
        _pc.DeliverTo(_deck);
        _deck.DeliverTo(_pc);
        AssertState("double-seed", _pc);
        AssertState("double-seed", _deck);
        Assert.Contains("Double seed", _deck.Ok("status").Out, StringComparison.Ordinal);

        // Nothing was lost: the Deck's own saves are in its backup.
        var deckBackup = _deck.Ok("backup", "list", "--json").Json().GetProperty("backups")[0].GetProperty("id").GetString()!;
        Assert.Equal("deck-1", CliDevice.FilesIn(Path.Combine(_deck.BackupRoot, deckBackup, "data"))["GAME1/save.bin"]);

        var resolved = _pc.Ok("apply", Id, "--replace-shared", "--confirm-replace");
        Assert.Contains("no saves are copied or deleted", resolved.Out, StringComparison.Ordinal);
        Assert.Equal("pc-1", _pc.SharedFiles()["GAME1/save.bin"]);

        _pc.DeliverTo(_deck);
        AssertState("ok", _pc);
        AssertState("ok", _deck);
        Assert.Equal("pc-1", _deck.LocalFiles()["GAME1/save.bin"]);
    }

    [Fact]
    public void Scenario_4_when_the_other_devices_saves_won_the_clash_the_winner_reverts_restores_and_replaces()
    {
        _pc.SetUp(createRoot: true);
        _pc.DeliverTo(_deck);
        JoinDeck();
        _pc.Save("GAME1/save.bin", "pc-1");
        _deck.Save("GAME1/save.bin", "deck-1");
        _pc.Ok("apply", Id, "--copy");
        _deck.Ok("apply", Id, "--copy");

        // This time the sync tool kept the Deck's file, but the PC's saves are the ones that should win.
        _deck.DeliverTo(_pc);
        _pc.DeliverTo(_deck);
        Assert.Equal("deck-1", _pc.LocalFiles()["GAME1/save.bin"]);
        AssertState("double-seed", _pc);
        var pcBackup = _pc.Ok("backup", "list", "--json").Json().GetProperty("backups")[0].GetProperty("id").GetString()!;

        _pc.Ok("revert", Id);
        _pc.Ok("backup", "restore", pcBackup);
        Assert.Equal("pc-1", _pc.LocalFiles()["GAME1/save.bin"]);
        var replaced = _pc.Ok("apply", Id, "--replace-shared", "--confirm-replace");
        Assert.Contains("replaced on every device", replaced.Out, StringComparison.Ordinal);

        _pc.DeliverTo(_deck, mirror: true);
        AssertState("ok", _pc);
        AssertState("ok", _deck);
        Assert.Equal("pc-1", _deck.LocalFiles()["GAME1/save.bin"]);

        // The Deck's saves, displaced twice, are still in backups on both devices.
        Assert.Contains(
            _pc.Ok("backup", "list", "--json").Json().GetProperty("backups").EnumerateArray(),
            b => CliDevice.FilesIn(Path.Combine(_pc.BackupRoot, b.GetProperty("id").GetString()!, "data")).ContainsValue("deck-1"));
    }

    [Fact]
    public void Scenario_5_a_wiped_device_is_restored_with_init_and_apply_all_and_no_questions()
    {
        SeedFromPc(("GAME1/save.bin", "pc-1"));
        _pc.DeliverTo(_deck);
        JoinDeck();
        _deck.Ok("apply", "--all");
        Assert.True(_deck.IsLinked);

        // The wipe: local settings, state and backups gone, the app reinstalled with a fresh, empty save folder.
        var fs = new SafeFs();
        fs.DeleteTree(_deck.LocalFolder);
        fs.RemoveLink(_deck.Source);
        Directory.CreateDirectory(Path.Combine(_deck.Source, "fresh-layout"));

        // The device's name is in use (by its former self), so it has to be taken over on purpose.
        _deck.Expect(CliApp.ExitRefused, "init", "--sync-root", _deck.SyncRoot, "--device", "deck");
        _deck.Ok("init", "--sync-root", _deck.SyncRoot, "--device", "deck", "--take-over");
        var restored = _deck.Ok("apply", "--all", "--json").Json();

        Assert.Equal("completed", restored.GetProperty("operations")[0].GetProperty("outcome").GetString());
        Assert.True(_deck.IsLinked);
        Assert.Equal("pc-1", _deck.LocalFiles()["GAME1/save.bin"]);
        AssertState("ok", _deck);
        Assert.Equal("ok", _pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0].GetProperty("state").GetString());
    }

    [Fact]
    public void Scenario_6_no_copy_into_an_empty_shared_folder_warns_that_the_app_will_see_no_saves()
    {
        _pc.SetUp(createRoot: true);
        _pc.Save("GAME1/save.bin", "pc-1");

        var run = _pc.Ok("apply", Id, "--no-copy");

        Assert.Contains("the app will see no saves", run.Out, StringComparison.Ordinal);
        Assert.Contains("Backup: ", run.Out, StringComparison.Ordinal);
        Assert.True(_pc.IsLinked);
        Assert.Empty(_pc.LocalFiles());
    }

    private void SeedFromPc(params (string Path, string Content)[] saves)
    {
        _pc.SetUp(createRoot: true);
        foreach (var (path, content) in saves)
        {
            _pc.Save(path, content);
        }

        _pc.Ok("apply", Id, "--copy");
    }

    /// <summary>The Deck's init needs the shared folder to have arrived; it never creates it.</summary>
    private void JoinDeck()
    {
        _deck.Ok("init", "--sync-root", _deck.SyncRoot, "--device", "deck");
        _deck.Ok("map", "add", Id, "--source", _deck.Source, "--process", "PPSSPPSDL");
    }

    private static void AssertState(string expected, CliDevice device)
    {
        var mapping = device.Ok("status", Id, "--json").Json().GetProperty("status").GetProperty("mappings")[0];
        Assert.True(
            expected == mapping.GetProperty("state").GetString(),
            $"{device.Name}: expected {expected}, got {mapping.GetProperty("state").GetString()} ({mapping.GetProperty("detail").GetString()})");
    }
}
