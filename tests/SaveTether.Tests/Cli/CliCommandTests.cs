using System.Text.Json;
using SaveTether.Cli;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Planning;
using SaveTether.Tests.Execution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

public class CliCommandTests
{
    private const string Id = CliDevice.MappingId;

    [Fact]
    public void Init_creates_the_shared_folder_only_when_asked_to()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        var refused = pc.Expect(CliApp.ExitRefused, "init", "--sync-root", pc.SyncRoot, "--device", "pc");
        Assert.Contains("--create-root", refused.Err, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(pc.SyncRoot));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(pc.LocalFolder));

        var created = pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");
        Assert.Contains("This device is 'pc' (new device)", created.Out, StringComparison.Ordinal);
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(Path.Combine(pc.SyncRoot, "savetether", ".savetether")));
        Assert.Equal(EntryKind.Directory, SafeFs.Probe(pc.BackupRoot));

        // Running it again on the same install is harmless and takes nothing over.
        var again = pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--json").Json().GetProperty("init");
        Assert.False(again.GetProperty("new_device").GetBoolean());
        Assert.False(again.GetProperty("took_over").GetBoolean());
    }

    [Fact]
    public void A_new_sync_folder_is_refused_while_a_mapping_is_linked_into_the_old_one()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var newRoot = sandbox.Dir("pc", "sync2");

        // Init again with the same sync folder is still fine.
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc");

        var refused = pc.Expect(CliApp.ExitRefused, "init", "--sync-root", newRoot, "--device", "pc", "--create-root", "--json").Json();
        Assert.Equal("mappings-applied", refused.GetProperty("problem").GetString());
        Assert.Contains($"savetether revert {Id}", refused.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Nothing was written: no SaveTether folder in the new place, and the device still uses the old one.
        Assert.Empty(Directory.GetFileSystemEntries(newRoot));
        Assert.Equal(pc.SyncRoot, pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("sync_root").GetString());
        Assert.True(pc.IsLinked);

        // Losing the local record does not hide the link: the old sync folder's device file names it.
        File.Delete(Path.Combine(pc.LocalFolder, "state.json"));
        Assert.Equal(
            "mappings-applied",
            pc.Expect(CliApp.ExitRefused, "init", "--sync-root", newRoot, "--device", "pc", "--create-root", "--json")
                .Json().GetProperty("problem").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(newRoot));

        // Reverted, it goes ahead.
        pc.Ok("revert", Id);
        pc.Ok("init", "--sync-root", newRoot, "--device", "pc", "--create-root");
        Assert.Equal(newRoot, pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("sync_root").GetString());
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_link_into_an_old_sync_folder_that_is_gone_does_not_block_a_new_one()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");

        // The old sync folder moves for good, as when its drive letter changes. The link cannot be reverted now.
        var moved = sandbox.At("pc", "sync-moved");
        Directory.Move(pc.SyncRoot, moved);
        var newRoot = sandbox.Dir("pc", "sync2");

        var init = pc.Ok("init", "--sync-root", newRoot, "--device", "pc", "--create-root", "--json").Json().GetProperty("init");
        var note = Assert.Single(init.GetProperty("notes").EnumerateArray()).GetString();
        Assert.Contains($"({Id}) is still a link", note, StringComparison.Ordinal);
        Assert.Contains("cannot be reached", note, StringComparison.Ordinal);

        // The link is left as it was: SaveTether deletes nothing here.
        Assert.True(pc.IsLinked);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, CliDevice.FilesIn(Path.Combine(moved, "savetether", "ppsspp", "SAVEDATA")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_shared_folder_set_up_only_in_part_still_takes_devices_and_mappings()
    {
        // Found on the Deck: a .savetether folder holding only definitions, with no devices or mappings folder.
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        Directory.CreateDirectory(Path.Combine(pc.SyncRoot, "savetether", ".savetether", "definitions"));

        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc");
        pc.Ok("map", "add", Id, "--target", "ppsspp/SAVEDATA", "--source", pc.Source);

        Assert.True(File.Exists(Path.Combine(pc.SyncRoot, "savetether", ".savetether", "devices", "pc.json")));
        Assert.True(File.Exists(Path.Combine(pc.SyncRoot, "savetether", ".savetether", "mappings", Id + ".json")));
    }

    [Fact]
    public void A_device_name_in_use_is_only_taken_over_when_told_to()
    {
        using var sandbox = new Sandbox();
        var deck = new CliDevice(sandbox, "deck");
        deck.SetUp(createRoot: true);
        var reinstall = new CliDevice(sandbox, "reinstalled");

        var unnamed = reinstall.Expect(CliApp.ExitRefused, "init", "--sync-root", deck.SyncRoot);
        Assert.Contains("Devices already set up here: deck", unnamed.Err, StringComparison.Ordinal);

        var refused = reinstall.Expect(CliApp.ExitRefused, "init", "--sync-root", deck.SyncRoot, "--device", "deck");
        Assert.Contains("--take-over", refused.Err, StringComparison.Ordinal);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(reinstall.LocalFolder));

        var taken = reinstall.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--take-over", "--json").Json().GetProperty("init");
        Assert.True(taken.GetProperty("took_over").GetBoolean());

        // The install that lost the name is told, and may no longer act on the device's paths.
        var status = deck.Ok("status", "--json").Json().GetProperty("status");
        Assert.True(status.GetProperty("device_file_mismatch").GetBoolean());
        Assert.Contains("device file mismatch", deck.Ok("status").Err, StringComparison.Ordinal);
        deck.Save("GAME1/save.bin", "one");
        var apply = deck.Expect(CliApp.ExitRefused, "apply", Id, "--copy");
        Assert.Contains("belongs to another install", apply.Err, StringComparison.Ordinal);
        Assert.False(deck.IsLinked);
    }

    [Fact]
    public void A_person_at_a_terminal_is_asked_for_the_name_and_before_taking_one_over()
    {
        using var sandbox = new Sandbox();
        var deck = new CliDevice(sandbox, "deck");
        deck.SetUp(createRoot: true);

        var reinstall = new CliDevice(sandbox, "reinstalled") { Typing = new StringReader("deck\nno\n") };
        var declined = reinstall.Expect(CliApp.ExitRefused, "init", "--sync-root", deck.SyncRoot);
        Assert.Contains("Device name:", declined.Out, StringComparison.Ordinal);
        Assert.Contains("Take it over?", declined.Out, StringComparison.Ordinal);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(reinstall.LocalFolder));

        reinstall.Typing = new StringReader("deck\nyes\n");
        Assert.Contains("name taken over", reinstall.Ok("init", "--sync-root", deck.SyncRoot).Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Commands_refuse_before_init()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        foreach (var args in new string[][] { ["status"], ["apply", Id], ["revert", Id], ["backup", "list"], ["map", "remove", Id], ["recover"] })
        {
            Assert.Contains("savetether init", pc.Expect(CliApp.ExitRefused, args).Err, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Map_add_and_remove_change_no_saves_and_only_this_devices_entry()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");
        pc.Save("GAME1/save.bin", "one");

        Assert.Contains("--target", pc.Expect(CliApp.ExitRefused, "map", "add", Id, "--source", pc.Source).Err, StringComparison.Ordinal);
        Assert.Contains("not a valid mapping ID", pc.Expect(CliApp.ExitRefused, "map", "add", "Bad", "--target", "x/y", "--source", pc.Source).Err, StringComparison.Ordinal);
        Assert.Contains("first part must be 'ppsspp'", pc.Expect(CliApp.ExitRefused, "map", "add", Id, "--target", "other/SAVEDATA", "--source", pc.Source).Err, StringComparison.Ordinal);
        Assert.Contains("--source", pc.Expect(CliApp.ExitUsage, "map", "add", Id, "--target", "ppsspp/SAVEDATA").Err, StringComparison.Ordinal);

        var added = pc.Ok("map", "add", Id, "--target", "ppsspp/SAVEDATA", "--source", pc.Source, "--process", "PPSSPPSDL", "--process", "PPSSPPQt");
        Assert.Contains("Added mapping ppsspp.savedata", added.Out, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(pc.Target));

        // The target of an existing mapping cannot be changed; without --target it is simply reused.
        Assert.Contains("cannot be changed", pc.Expect(CliApp.ExitRefused, "map", "add", Id, "--target", "ppsspp/OTHER", "--source", pc.Source).Err, StringComparison.Ordinal);
        Assert.Contains("already existed", pc.Ok("map", "add", Id, "--source", pc.Source).Out, StringComparison.Ordinal);

        pc.Ok("apply", Id, "--copy");
        Assert.Contains("revert", pc.Expect(CliApp.ExitRefused, "map", "remove", Id).Err, StringComparison.Ordinal);
        pc.Ok("revert", Id);

        pc.Ok("map", "remove", Id);
        var status = pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0];
        Assert.Equal("not-applied", status.GetProperty("state").GetString());
        Assert.False(status.TryGetProperty("source", out _));
        Assert.Contains("no save folder", pc.Expect(CliApp.ExitRefused, "apply", Id).Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_add_refuses_a_save_folder_another_mapping_has_contains_or_is_inside()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        var deviceFile = Path.Combine(pc.SyncRoot, "savetether", ".savetether", "devices", "pc.json");
        var before = File.ReadAllText(deviceFile);

        foreach (var (source, how) in new[]
        {
            (pc.Source, "is already the save folder of"),
            (Path.Combine(pc.Source, "GAME1"), "is inside the save folder of"),
            (pc.AppFolder, "contains the save folder of"),
        })
        {
            var err = pc.Expect(CliApp.ExitRefused, "map", "add", "other.saves", "--target", "other/saves", "--source", source).Err;
            Assert.Contains($"{how} '{Id}'", err, StringComparison.Ordinal);
            Assert.Contains($"savetether map remove {Id}", err, StringComparison.Ordinal);
        }

        // With --json, the store's refusal says what is in the way as fields.
        var json = pc.Expect(CliApp.ExitRefused, "map", "add", "other.saves", "--target", "other/saves", "--source", pc.AppFolder, "--json").Json();
        Assert.Equal("conflict", json.GetProperty("problem").GetString());
        var conflict = json.GetProperty("conflict");
        Assert.Equal("folder-in-use", conflict.GetProperty("kind").GetString());
        Assert.Equal(Id, conflict.GetProperty("mapping_id").GetString());
        Assert.Equal(pc.Source, conflict.GetProperty("folder").GetString());

        Assert.Equal(before, File.ReadAllText(deviceFile));

        // A sibling folder is fine, and so is the same mapping given its own folder again.
        pc.Ok("map", "add", "other.saves", "--target", "other/saves", "--source", Path.Combine(pc.AppFolder, "OTHER"));
        pc.Ok("map", "add", Id, "--source", pc.Source);
    }

    [Fact]
    public void Status_reports_each_health_state()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        string State() => pc.Ok("status", Id, "--json").Json().GetProperty("status").GetProperty("mappings")[0].GetProperty("state").GetString()!;
        var fs = new SafeFs();

        Assert.Equal("not-applied", State());
        Assert.Contains("Not applied", pc.Ok("status").Out, StringComparison.Ordinal);

        // A link someone else made, for example one left by the old scripts.
        var elsewhere = sandbox.Dir("elsewhere");
        fs.CreateDirectoryLink(pc.Source, elsewhere);
        Assert.Equal("foreign-link", State());
        Assert.Contains("did not create", pc.Expect(CliApp.ExitRefused, "apply", Id).Err, StringComparison.Ordinal);
        fs.RemoveLink(pc.Source);

        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        Assert.Equal("ok", State());

        fs.RemoveLink(pc.Source);
        fs.CreateDirectoryLink(pc.Source, elsewhere);
        Assert.Equal("retargeted", State());

        fs.RemoveLink(pc.Source);
        pc.Save("GAME2/save.bin", "stranded");
        Assert.Equal("replaced", State());
        Assert.Contains("not shared", pc.Ok("status").Out, StringComparison.Ordinal);

        fs.DeleteTree(pc.Source);
        fs.CreateDirectoryLink(pc.Source, pc.Target);
        fs.DeleteTree(pc.Target);
        Assert.Equal("broken", State());
        Assert.Contains("that folder is missing", pc.Expect(CliApp.ExitRefused, "apply", Id).Err, StringComparison.Ordinal);

        Assert.Contains("no mapping", pc.Expect(CliApp.ExitRefused, "status", "nope.nothing").Err, StringComparison.Ordinal);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Status_reports_broken_links_when_the_sync_root_is_missing_or_empty_and_everything_else_refuses()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var away = pc.SyncRoot + ".away";

        void AssertUnavailable(string problem)
        {
            var status = pc.Ok("status", "--json").Json().GetProperty("status");
            Assert.False(status.GetProperty("sync_root_available").GetBoolean());
            Assert.Contains(problem, status.GetProperty("sync_root_problem").GetString(), StringComparison.Ordinal);
            var mapping = Assert.Single(status.GetProperty("mappings").EnumerateArray());
            Assert.Equal(Id, mapping.GetProperty("mapping_id").GetString());
            Assert.Equal("broken", mapping.GetProperty("state").GetString());

            var text = pc.Ok("status");
            Assert.Contains("from its own record", text.Out, StringComparison.Ordinal);
            Assert.Contains("Broken", text.Out, StringComparison.Ordinal);
            Assert.StartsWith("Warning: ", text.Err, StringComparison.Ordinal);
            Assert.Contains(problem, text.Err, StringComparison.Ordinal);
            Assert.Equal("broken", pc.Ok("status", Id, "--json").Json().GetProperty("status").GetProperty("mappings")[0].GetProperty("state").GetString());

            // Status of a mapping this device never applied cannot be known without the shared list.
            Assert.Contains(problem, pc.Expect(CliApp.ExitRefused, "status", "other.saves").Err, StringComparison.Ordinal);
            Assert.Contains("shared ones could not be read", pc.Ok("app", "list").Out, StringComparison.Ordinal);

            foreach (var args in new string[][]
            {
                ["apply", Id], ["apply", "--all"], ["revert", Id], ["backup", "list"],
                ["map", "add", Id, "--source", pc.Source], ["map", "remove", Id], ["app", "add", "cemu", "--dry-run"],
            })
            {
                Assert.Contains(problem, pc.Expect(CliApp.ExitRefused, args).Err, StringComparison.Ordinal);
            }

            Assert.True(pc.IsLinked);
        }

        Directory.Move(pc.SyncRoot, away);
        AssertUnavailable($"The sync root '{pc.SyncRoot}' does not exist.");
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(pc.SyncRoot));

        // An unmounted drive leaves an empty mount point.
        Directory.CreateDirectory(pc.SyncRoot);
        AssertUnavailable("is not set up for SaveTether");
        Assert.Empty(Directory.EnumerateFileSystemEntries(pc.SyncRoot));

        Directory.Delete(pc.SyncRoot);
        Directory.Move(away, pc.SyncRoot);
        var back = pc.Ok("status", "--json").Json().GetProperty("status");
        Assert.True(back.GetProperty("sync_root_available").GetBoolean());
        Assert.False(back.TryGetProperty("sync_root_problem", out _));
        Assert.Equal("ok", back.GetProperty("mappings")[0].GetProperty("state").GetString());
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Exit_codes_tell_success_refusal_rollback_and_recovery_apart()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");

        // 1: the owner has a choice to make.
        Assert.Equal("refused", pc.Expect(CliApp.ExitRefused, "apply", Id, "--json").Operation().GetProperty("outcome").GetString());

        // 1: a precondition. The app is running.
        pc.Running.Add("PPSSPPSDL");
        Assert.Contains("appears to be running", pc.Expect(CliApp.ExitRefused, "apply", Id, "--copy").Err, StringComparison.Ordinal);
        var running = pc.Expect(CliApp.ExitRefused, "apply", Id, "--copy", "--json")
            .Operation().GetProperty("refusal").GetProperty("failures")[0];
        Assert.Equal("app-running", running.GetProperty("kind").GetString());
        Assert.Equal(["PPSSPPSDL"], running.GetProperty("processes").GetProperty("running").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["PPSSPPSDL"], running.GetProperty("processes").GetProperty("looked_for").EnumerateArray().Select(e => e.GetString()));
        Assert.False(running.TryGetProperty("space", out _));
        Assert.Equal("planned", pc.Ok("apply", Id, "--copy", "--assume-closed", "--dry-run", "--json").Operation().GetProperty("outcome").GetString());
        pc.Running.Clear();

        // 2: a step failed and the operation was undone.
        pc.Faults = new FailAt(StepKind.CreateLink, FaultPoint.BeforeStep);
        var rolledBack = pc.Expect(CliApp.ExitRolledBack, "apply", Id, "--copy");
        Assert.Contains("failed and was undone", rolledBack.Err, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        Assert.False(pc.IsLinked);

        // The copy it made into the shared folder was undone too, so the same choice can be made again.
        Assert.False(Directory.Exists(pc.Target));
        Assert.Equal("planned", pc.Ok("apply", Id, "--copy", "--dry-run", "--json").Operation().GetProperty("outcome").GetString());

        // 3: a step failed after the link was verified. Everything but looking and recovering refuses.
        pc.Faults = new FailAt(StepKind.WriteState, FaultPoint.BeforeStep);
        pc.Expect(CliApp.ExitRecoveryNeeded, "apply", Id, "--no-copy");
        pc.Faults = null;
        Assert.Contains("savetether recover", pc.Expect(CliApp.ExitRecoveryNeeded, "apply", Id).Err, StringComparison.Ordinal);
        pc.Expect(CliApp.ExitRecoveryNeeded, "revert", Id);
        pc.Expect(CliApp.ExitRecoveryNeeded, "map", "add", Id, "--source", pc.Source);
        pc.Expect(CliApp.ExitRecoveryNeeded, "init", "--sync-root", pc.SyncRoot, "--device", "pc");
        var status = pc.Expect(CliApp.ExitRecoveryNeeded, "status", "--json").Json().GetProperty("status");
        Assert.True(status.GetProperty("journal_open").GetBoolean());
        pc.Ok("backup", "list");

        // 0: recovery finishes it, and then there is nothing left to recover.
        Assert.Contains("was completed", pc.Ok("recover").Out, StringComparison.Ordinal);
        Assert.Contains("Nothing to recover", pc.Ok("recover").Out, StringComparison.Ordinal);
        Assert.Contains("Already applied", pc.Ok("apply", Id).Out, StringComparison.Ordinal);
        Assert.Equal("ok", pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0].GetProperty("state").GetString());
    }

    [Fact]
    public void Recovery_after_a_crash_undoes_the_operation_and_exits_zero()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Faults = new CrashAt(3, FaultPoint.AfterStep);
        Assert.Throws<SimulatedCrashException>(() => pc.Run("apply", Id, "--copy"));
        pc.Faults = null;

        var recovered = pc.Ok("recover", "--json").Operation();

        Assert.Equal("rolled-back", recovered.GetProperty("outcome").GetString());
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        Assert.False(pc.IsLinked);
        Assert.False(Directory.Exists(pc.Target));
    }

    [Fact]
    public void Apply_all_runs_each_mapping_and_stops_at_the_first_that_needs_a_choice()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");
        Assert.Contains("No mapping has a save folder", pc.Ok("apply", "--all").Out, StringComparison.Ordinal);

        foreach (var role in new[] { "a", "b", "c" })
        {
            pc.Ok("map", "add", $"ppsspp.{role}", "--target", $"ppsspp/{role}", "--source", Path.Combine(pc.AppFolder, role));
        }

        CliDevice.Write(Path.Combine(pc.AppFolder, "b"), "save.bin", "needs a choice");

        var run = pc.Expect(CliApp.ExitRefused, "apply", "--all", "--json");
        var operations = run.Json().GetProperty("operations");
        Assert.Equal(
            [("ppsspp.a", "completed"), ("ppsspp.b", "refused")],
            operations.EnumerateArray().Select(o => (o.GetProperty("mapping_id").GetString(), o.GetProperty("outcome").GetString())));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(Path.Combine(pc.AppFolder, "c")));

        pc.Ok("apply", "ppsspp.b", "--copy");
        Assert.Equal(3, pc.Ok("apply", "--all", "--json").Json().GetProperty("operations").GetArrayLength());
    }

    [Theory]
    [InlineData("'apply' needs a mapping ID, or --all", "apply")]
    [InlineData("'apply' takes a mapping ID or --all, not both", "apply", "a.b", "--all")]
    [InlineData("choose one of --copy, --no-copy, not several", "apply", "a.b", "--copy", "--no-copy")]
    [InlineData("'--copy' cannot be combined with --all", "apply", "--all", "--copy")]
    [InlineData("'--confirm-replace' only goes with --replace-shared", "apply", "a.b", "--confirm-replace")]
    [InlineData("'init' needs --sync-root <path>", "init", "--device", "pc")]
    public void Combinations_that_make_no_sense_are_usage_errors(string message, params string[] args)
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);

        var text = pc.Expect(CliApp.ExitUsage, args);
        Assert.Contains(message, text.Err, StringComparison.Ordinal);
        Assert.Empty(text.Out);

        // Asked for JSON, a usage error is still one JSON object on stdout and nothing on stderr.
        var json = pc.Expect(CliApp.ExitUsage, [.. args, "--json"]);
        Assert.Empty(json.Err);
        Assert.Equal(CliApp.ExitUsage, json.Json().GetProperty("exit_code").GetInt32());
        Assert.Contains(message, json.Json().GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Json_mode_never_asks_and_text_mode_at_a_terminal_does()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        CliDevice.Write(pc.Target, "GAME1/save.bin", "theirs");
        var other = new CliDevice(sandbox, "other") { Typing = new MustNotBeRead() };
        pc.Typing = new MustNotBeRead();

        // Every place a person would be asked: the device name, taking a name over, replacing shared data, a first seed.
        other.Expect(CliApp.ExitRefused, "init", "--sync-root", pc.SyncRoot, "--json");
        other.Expect(CliApp.ExitRefused, "init", "--sync-root", pc.SyncRoot, "--device", "pc", "--json");
        var unconfirmed = pc.Expect(CliApp.ExitRefused, "apply", Id, "--replace-shared", "--json").Operation();
        Assert.Equal("confirmation-needed", unconfirmed.GetProperty("refusal").GetProperty("kind").GetString());
        Assert.Equal("theirs", pc.SharedFiles()["GAME1/save.bin"]);
        new SafeFs().DeleteTree(pc.Target);
        pc.Ok("apply", Id, "--copy", "--json");

        // The same reader in text mode is read, which shows the check above is a real one.
        Assert.Throws<InvalidOperationException>(() => other.Run("init", "--sync-root", pc.SyncRoot));
    }

    [Fact]
    public void A_person_at_a_terminal_confirms_replacing_shared_data_by_typing_yes()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        CliDevice.Write(pc.Target, "GAME1/save.bin", "theirs");

        pc.Typing = new StringReader("\n");
        var cancelled = pc.Expect(CliApp.ExitRefused, "apply", Id, "--replace-shared");
        Assert.Contains("Cancelled", cancelled.Err, StringComparison.Ordinal);
        Assert.Equal("theirs", pc.SharedFiles()["GAME1/save.bin"]);

        pc.Typing = new StringReader("yes\n");
        var replaced = pc.Ok("apply", Id, "--replace-shared");
        Assert.Contains("Replace the shared saves?", replaced.Out, StringComparison.Ordinal);
        Assert.Equal("mine", pc.SharedFiles()["GAME1/save.bin"]);
        Assert.Equal(2, replaced.Out.Split('\n').Count(line => line.StartsWith("Backup: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Backups_are_listed_and_a_restore_brings_back_files_and_links()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        Sandbox.Link(Path.Combine(pc.Source, "link-to-canary"), sandbox.Canary);
        Assert.Contains("No backups", pc.Ok("backup", "list").Out, StringComparison.Ordinal);

        pc.Ok("apply", Id, "--no-copy");
        var backupId = pc.Ok("backup", "list", Id, "--json").Json().GetProperty("backups")[0].GetProperty("id").GetString()!;
        Assert.Contains(backupId, pc.Ok("backup", "list").Out, StringComparison.Ordinal);

        // While linked, restoring would write into the shared folder.
        Assert.Contains("revert", pc.Expect(CliApp.ExitRefused, "backup", "restore", backupId).Err, StringComparison.Ordinal);
        pc.Ok("revert", Id);
        Assert.Empty(pc.LocalFiles());

        var plan = pc.Ok("backup", "restore", backupId, "--dry-run");
        Assert.Contains("Recreate the link(s) recorded in the backup: link-to-canary", plan.Out, StringComparison.Ordinal);
        Assert.Empty(pc.LocalFiles());

        Assert.Contains("Restored the backup", pc.Ok("backup", "restore", backupId).Out, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        var link = Path.Combine(pc.Source, "link-to-canary");
        Assert.True(SafeFs.Probe(link) is EntryKind.Junction or EntryKind.Symlink);
        Assert.True(PathRules.AreSame(SafeFs.ReadLinkTarget(link), sandbox.Canary));

        Assert.Contains("no backup", pc.Expect(CliApp.ExitRefused, "backup", "restore", "ppsspp.savedata/20200101T000000Z").Err, StringComparison.Ordinal);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Revert_and_dry_run_change_only_what_they_say()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");

        var dryRun = pc.Ok("apply", Id, "--copy", "--dry-run");
        Assert.Contains("Dry run: nothing was changed.", dryRun.Out, StringComparison.Ordinal);
        Assert.Contains("1. Back up", dryRun.Out, StringComparison.Ordinal);
        Assert.False(pc.IsLinked);
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(pc.Target));
        Assert.Empty(Directory.GetFileSystemEntries(pc.BackupRoot));

        Assert.Contains("not linked", pc.Expect(CliApp.ExitRefused, "revert", Id).Err, StringComparison.Ordinal);
        pc.Ok("apply", Id, "--copy");

        Assert.Contains("Plan to revert", pc.Ok("revert", Id, "--dry-run").Out, StringComparison.Ordinal);
        Assert.True(pc.IsLinked);
        Assert.Contains("is a real folder again", pc.Ok("revert", Id).Out, StringComparison.Ordinal);
        Assert.False(pc.IsLinked);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.SharedFiles());
    }

    /// <summary>A terminal nobody may be asked at.</summary>
    private sealed class MustNotBeRead : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("the command asked a question");
    }
}
