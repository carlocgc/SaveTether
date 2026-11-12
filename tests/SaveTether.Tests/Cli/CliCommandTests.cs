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
        Assert.Equal("planned", pc.Ok("apply", Id, "--copy", "--assume-closed", "--dry-run", "--json").Operation().GetProperty("outcome").GetString());
        pc.Running.Clear();

        // 2: a step failed and the operation was undone.
        pc.Faults = new FailAt(StepKind.CreateLink, FaultPoint.BeforeStep);
        var rolledBack = pc.Expect(CliApp.ExitRolledBack, "apply", Id, "--copy");
        Assert.Contains("failed and was undone", rolledBack.Err, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        Assert.False(pc.IsLinked);

        // The copy that reached the shared folder stays (invariant 2), so the choice is now a different one.
        Assert.Equal("guard", pc.Expect(CliApp.ExitRefused, "apply", Id, "--copy", "--json").Operation().GetProperty("refusal").GetProperty("classification").GetString());

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

    /// <summary>
    /// The JSON a command prints is what the Decky plugin will depend on (design 5.11). Each
    /// command's output is reduced to its shape, every property path with its type, and compared
    /// with a stored copy, so a renamed or removed field is caught here first.
    /// To accept an intended change, run the tests once with SAVETETHER_UPDATE_SHAPES=1.
    /// </summary>
    [Fact]
    public void Json_output_of_every_command_keeps_its_stored_shape()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        var shapes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Record(string name, int exit, params string[] args) => shapes[name] = JsonShape.Of(pc.Expect(exit, [.. args, "--json"]).Json());

        Record("init", 0, "init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");
        Record("map-add", 0, "map", "add", Id, "--target", "ppsspp/SAVEDATA", "--source", pc.Source, "--process", "PPSSPPSDL");
        pc.Save("GAME1/save.bin", "mine");
        pc.Save("GAME2/save.bin", "only-here");
        Record("status", 0, "status");
        Record("apply-refused-choice", 1, "apply", Id);
        Record("apply-dry-run", 0, "apply", Id, "--copy", "--dry-run");
        Record("apply-completed", 0, "apply", Id, "--copy");
        Record("apply-all", 0, "apply", "--all");
        Record("backup-list", 0, "backup", "list");
        Record("revert", 0, "revert", Id);
        CliDevice.Write(pc.Source, "GAME1/save.bin", "changed");
        Record("apply-refused-guard", 1, "apply", Id);
        var backupId = pc.Ok("backup", "list", "--json").Json().GetProperty("backups")[0].GetProperty("id").GetString()!;
        Record("backup-restore", 0, "backup", "restore", backupId);
        Record("recover", 0, "recover");
        Record("map-remove", 0, "map", "remove", Id);
        Record("refused", 1, "revert", Id);
        Record("usage-error", 64, "apply");

        JsonShape.AssertStored(shapes);
    }

    /// <summary>A terminal nobody may be asked at.</summary>
    private sealed class MustNotBeRead : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("the command asked a question");
    }
}

/// <summary>Reduces a JSON document to its property paths and value types.</summary>
internal static class JsonShape
{
    public static string Of(JsonElement root)
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        Walk(root, "$", lines);
        return string.Join('\n', lines);
    }

    public static void AssertStored(SortedDictionary<string, string> shapes)
    {
        var actual = string.Join("\n\n", shapes.Select(pair => $"## {pair.Key}\n{pair.Value}")) + "\n";
        var path = Path.Combine(FindRepositoryRoot(), "tests", "SaveTether.Tests", "Cli", "json-shapes.txt");
        if (Environment.GetEnvironmentVariable("SAVETETHER_UPDATE_SHAPES") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var stored = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : string.Empty;
        Assert.True(
            stored == actual,
            "The JSON output changed shape. If that is intended, run the tests once with SAVETETHER_UPDATE_SHAPES=1 and commit " +
            $"'{path}'. Actual shapes:\n{actual}");
    }

    private static void Walk(JsonElement element, string path, SortedSet<string> lines)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                lines.Add($"{path}: object");
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, $"{path}.{property.Name}", lines);
                }

                break;

            case JsonValueKind.Array:
                lines.Add($"{path}: array");
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, path + "[]", lines);
                }

                break;

            case JsonValueKind.True or JsonValueKind.False:
                lines.Add($"{path}: boolean");
                break;

            default:
                lines.Add($"{path}: {element.ValueKind.ToString().ToLowerInvariant()}");
                break;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaveTether.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find SaveTether.slnx above the test folder.");
    }
}
