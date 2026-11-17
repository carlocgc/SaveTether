using System.Text.Json;
using SaveTether.Cli;
using SaveTether.Core.Definitions;
using SaveTether.Tests.Definitions;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>
/// "app add" and "app list" end to end, against fixture homes: the app's folders are created by
/// the test, and definitions come from the built-in set or the shared definitions folder.
/// </summary>
public class AppAddCliTests
{
    /// <summary>A definition with two Linux installs, both under home, and roles on and off by default.</summary>
    private const string TestApp = """
        {
          "schema": 1,
          "id": "testapp",
          "name": "Test App",
          "kind": "emulator",
          "installs": [
            { "id": "native", "os": "linux", "detect": { "dir": "{home}/native/data" }, "base": "{home}/native/data" },
            { "id": "portable", "os": "linux", "detect": { "file": "{install_dir}/app.cfg" }, "base": "{install_dir}" },
          ],
          "roles": [
            { "id": "saves", "class": "save", "default": true, "path": "saves", "target": "saves" },
            { "id": "states", "class": "state", "default": true, "path": { "native": "states" }, "target": "states" },
            { "id": "shots", "class": "media", "default": false, "path": "shots", "target": "shots" },
          ],
          "quirks": ["A quirk the owner should read."],
        }
        """;

    private static string SharedDefinitions(CliDevice device) =>
        Path.Combine(device.SyncRoot, "savetether", ".savetether", DefinitionCatalog.SharedFolderName);

    private static void ShareDefinition(CliDevice device, string id, string text)
    {
        Directory.CreateDirectory(SharedDefinitions(device));
        File.WriteAllText(Path.Combine(SharedDefinitions(device), id + ".jsonc"), text);
    }

    private static CliDevice NewDevice(Sandbox sandbox, string name = "deck")
    {
        var device = new CliDevice(sandbox, name);
        device.Ok("init", "--sync-root", device.SyncRoot, "--device", name, "--create-root");
        return device;
    }

    private static JsonElement DeviceFile(CliDevice device)
    {
        var path = Path.Combine(device.SyncRoot, "savetether", ".savetether", "devices", device.Name + ".json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static bool MappingExists(CliDevice device, string id) =>
        File.Exists(Path.Combine(device.SyncRoot, "savetether", ".savetether", "mappings", id + ".json"));

    // ---- The built-in definitions ----

    [Fact]
    public void Every_definition_in_the_repository_is_built_in_and_valid()
    {
        var folder = Path.Combine(DefinitionLoaderTests.FindRepositoryRoot(), "definitions");
        var files = Directory.GetFiles(folder, "*.jsonc").Select(f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal).ToList();

        Assert.Contains("cemu", files);
        Assert.Equal(files, DefinitionCatalog.BuiltInIds);
        foreach (var id in files)
        {
            var entry = new DefinitionCatalog(null).Find(id)!;
            Assert.True(entry.IsValid, string.Join("\n", entry.Errors));
            Assert.Equal(DefinitionSource.BuiltIn, entry.Source);
        }
    }

    // ---- Cemu, the shipped definition ----

    [Fact]
    public void Cemu_is_found_under_xdg_data_and_added_at_entry_depth_2()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        var save = Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save");
        CliDevice.Write(save, "00050000/101c9500/user/80000001/game.sav", "zelda save");

        var run = deck.Ok("app", "add", "cemu");

        Assert.Contains("cemu.save  (1 file(s), 10 bytes)", run.Out, StringComparison.Ordinal);
        Assert.Contains("entry depth:   2", run.Out, StringComparison.Ordinal);
        Assert.Contains("Note: Link usr/save, not mlc01", run.Out, StringComparison.Ordinal);

        var device = DeviceFile(deck);
        Assert.Equal(2, device.GetProperty("schema").GetInt32());
        Assert.Equal("linux-native", device.GetProperty("apps").GetProperty("cemu").GetProperty("install").GetString());
        Assert.Equal(save, device.GetProperty("paths").GetProperty("cemu.save").GetProperty("source").GetString());

        var mapping = deck.Ok("status", "cemu.save", "--json").Json().GetProperty("status").GetProperty("mappings")[0];
        Assert.Equal("cemu.save", mapping.GetProperty("mapping_id").GetString());

        // And the mapping it wrote can be applied like any other.
        deck.Ok("apply", "cemu.save", "--copy");
        Assert.Equal("zelda save", CliDevice.FilesIn(Path.Combine(deck.SyncRoot, "savetether", "cemu", "save"))["00050000/101c9500/user/80000001/game.sav"]);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_app_that_is_not_installed_is_refused_with_what_was_looked_at_and_nothing_is_written()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        var before = DeviceFile(deck).ToString();

        var json = deck.Expect(CliApp.ExitRefused, "app", "add", "cemu", "--json").Json();

        Assert.Equal("not-installed", json.GetProperty("app").GetProperty("problem").GetString());
        var candidate = json.GetProperty("app").GetProperty("candidates")[0];
        Assert.Equal("linux-native", candidate.GetProperty("id").GetString());
        Assert.Contains("No folder at", candidate.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, DeviceFile(deck).ToString());
        Assert.False(MappingExists(deck, "cemu.save"));
    }

    [Fact]
    public void An_unknown_app_lists_the_known_ones()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);

        var run = deck.Expect(CliApp.ExitRefused, "app", "add", "nope");

        Assert.Contains("There is no definition for 'nope'. Known apps: cemu", run.Err, StringComparison.Ordinal);
    }

    // ---- Shared definitions ----

    [Fact]
    public void A_shared_definition_is_listed_and_replaces_a_built_in_one_with_the_same_id()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);
        ShareDefinition(deck, "cemu", """
            {
              "schema": 1, "id": "cemu", "name": "Cemu (mine)", "kind": "emulator",
              "installs": [ { "id": "here", "os": "linux", "detect": { "dir": "{home}/cemu" }, "base": "{home}/cemu" } ],
              "roles": [ { "id": "save", "class": "save", "default": true, "path": "save", "target": "save" } ],
            }
            """);
        Directory.CreateDirectory(Path.Combine(deck.Home, "cemu", "save"));

        var list = deck.Ok("app", "list", "--json").Json().GetProperty("apps").EnumerateArray()
            .Select(a => (a.GetProperty("id").GetString(), a.GetProperty("name").GetString(), a.GetProperty("source").GetString()))
            .ToList();
        Assert.Equal([("cemu", "Cemu (mine)", "shared"), ("testapp", "Test App", "shared")], list);

        var plan = deck.Ok("app", "add", "cemu", "--json").Json().GetProperty("app").GetProperty("plan");
        Assert.Equal("shared", plan.GetProperty("definition_source").GetString());
        Assert.Equal("here", plan.GetProperty("install").GetString());
    }

    [Fact]
    public void A_shared_definition_with_mistakes_is_refused_with_file_and_line()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp.Replace("\"emulator\"", "\"toaster\"", StringComparison.Ordinal));

        var run = deck.Expect(CliApp.ExitRefused, "app", "add", "testapp");

        Assert.Contains("testapp.jsonc(5):", run.Err, StringComparison.Ordinal);
    }

    // ---- Choosing an install ----

    private static CliDevice WithBothInstalls(Sandbox sandbox, out string portable)
    {
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);
        Directory.CreateDirectory(Path.Combine(deck.Home, "native", "data"));
        portable = Path.Combine(deck.Home, "portable");
        CliDevice.Write(portable, "app.cfg", "config");
        return deck;
    }

    [Fact]
    public void Two_installs_found_are_never_guessed_between_without_a_person()
    {
        using var sandbox = new Sandbox();
        var deck = WithBothInstalls(sandbox, out var portable);

        var json = deck.Expect(CliApp.ExitRefused, "app", "add", "testapp", "--install-dir", portable, "--json").Json();

        Assert.Equal("install-choice-needed", json.GetProperty("app").GetProperty("problem").GetString());
        Assert.Contains("--install <id>", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.False(MappingExists(deck, "testapp.saves"));
    }

    [Fact]
    public void Two_installs_found_are_asked_about_at_a_terminal()
    {
        using var sandbox = new Sandbox();
        var deck = WithBothInstalls(sandbox, out var portable);
        deck.Typing = new StringReader("2\n");

        var run = deck.Ok("app", "add", "testapp", "--install-dir", portable);

        Assert.Contains("Which install?", run.Out, StringComparison.Ordinal);
        Assert.Equal("portable", DeviceFile(deck).GetProperty("apps").GetProperty("testapp").GetProperty("install").GetString());
        Assert.Equal(Path.Combine(portable, "saves"), DeviceFile(deck).GetProperty("paths").GetProperty("testapp.saves").GetProperty("source").GetString());
    }

    [Fact]
    public void A_bad_answer_at_the_terminal_changes_nothing()
    {
        using var sandbox = new Sandbox();
        var deck = WithBothInstalls(sandbox, out var portable);
        deck.Typing = new StringReader("7\n");

        deck.Expect(CliApp.ExitRefused, "app", "add", "testapp", "--install-dir", portable);

        Assert.False(MappingExists(deck, "testapp.saves"));
    }

    [Fact]
    public void Install_picks_one_and_unknown_installs_are_refused()
    {
        using var sandbox = new Sandbox();
        var deck = WithBothInstalls(sandbox, out var portable);

        deck.Ok("app", "add", "testapp", "--install-dir", portable, "--install", "native");
        Assert.Equal(Path.Combine(deck.Home, "native", "data", "states"), DeviceFile(deck).GetProperty("paths").GetProperty("testapp.states").GetProperty("source").GetString());

        var json = deck.Expect(CliApp.ExitRefused, "app", "add", "testapp", "--install", "nope", "--json").Json();
        Assert.Equal("unknown-install", json.GetProperty("app").GetProperty("problem").GetString());
    }

    [Fact]
    public void The_install_used_last_time_is_kept_when_both_are_found_later()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);
        Directory.CreateDirectory(Path.Combine(deck.Home, "native", "data"));
        deck.Ok("app", "add", "testapp");

        var portable = Path.Combine(deck.Home, "portable");
        CliDevice.Write(portable, "app.cfg", "config");
        var plan = deck.Ok("app", "add", "testapp", "--install-dir", portable, "--dry-run", "--json").Json().GetProperty("app").GetProperty("plan");

        Assert.Equal("native", plan.GetProperty("install").GetString());
    }

    // ---- Roles ----

    [Fact]
    public void Default_roles_are_added_and_others_only_when_named()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);
        Directory.CreateDirectory(Path.Combine(deck.Home, "native", "data"));

        deck.Ok("app", "add", "testapp");
        Assert.True(MappingExists(deck, "testapp.saves"));
        Assert.True(MappingExists(deck, "testapp.states"));
        Assert.False(MappingExists(deck, "testapp.shots"));

        deck.Ok("app", "add", "testapp", "--role", "shots");
        Assert.True(MappingExists(deck, "testapp.shots"));

        var json = deck.Expect(CliApp.ExitRefused, "app", "add", "testapp", "--role", "nope", "--json").Json();
        Assert.Equal("unknown-role", json.GetProperty("app").GetProperty("problem").GetString());
        Assert.Contains("Its roles: saves, states, shots", json.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_role_the_chosen_install_lacks_is_left_out_of_the_defaults()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);
        var portable = Path.Combine(deck.Home, "portable");
        CliDevice.Write(portable, "app.cfg", "config");

        deck.Ok("app", "add", "testapp", "--install-dir", portable);

        Assert.True(MappingExists(deck, "testapp.saves"));
        Assert.False(MappingExists(deck, "testapp.states"));
    }

    // ---- Values set for the device ----

    [Fact]
    public void A_needed_value_that_is_missing_says_which_option_gives_it()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);

        var run = deck.Expect(CliApp.ExitRefused, "app", "add", "testapp", "--install", "portable");

        Assert.Contains("{install_dir}", run.Err, StringComparison.Ordinal);
        Assert.Contains("--install-dir", run.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_remembered_and_a_moved_install_is_followed_while_not_applied()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);
        var first = Path.Combine(deck.Home, "first");
        CliDevice.Write(first, "app.cfg", "config");

        deck.Ok("app", "add", "testapp", "--install-dir", first);
        Assert.Equal(first, DeviceFile(deck).GetProperty("apps").GetProperty("testapp").GetProperty("install_dir").GetString());

        // Again without the option: the remembered folder is used.
        deck.Ok("app", "add", "testapp");
        Assert.Equal(Path.Combine(first, "saves"), DeviceFile(deck).GetProperty("paths").GetProperty("testapp.saves").GetProperty("source").GetString());

        // Moved: the new folder is remembered and the save folder follows.
        var second = Path.Combine(deck.Home, "second");
        CliDevice.Write(second, "app.cfg", "config");
        deck.Ok("app", "add", "testapp", "--install-dir", second);
        Assert.Equal(Path.Combine(second, "saves"), DeviceFile(deck).GetProperty("paths").GetProperty("testapp.saves").GetProperty("source").GetString());
    }

    [Fact]
    public void A_role_that_is_applied_elsewhere_must_be_reverted_before_it_moves()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        ShareDefinition(deck, "testapp", TestApp);
        var first = Path.Combine(deck.Home, "first");
        CliDevice.Write(first, "app.cfg", "config");
        CliDevice.Write(first, "saves/slot1.sav", "save");
        deck.Ok("app", "add", "testapp", "--install-dir", first);
        deck.Ok("apply", "testapp.saves", "--copy");

        var second = Path.Combine(deck.Home, "second");
        CliDevice.Write(second, "app.cfg", "config");
        var json = deck.Expect(CliApp.ExitRefused, "app", "add", "testapp", "--install-dir", second, "--json").Json();

        Assert.Equal("conflict", json.GetProperty("app").GetProperty("problem").GetString());
        Assert.Contains("savetether revert testapp.saves", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(first, DeviceFile(deck).GetProperty("apps").GetProperty("testapp").GetProperty("install_dir").GetString());
        sandbox.AssertCanaryIntact();
    }

    // ---- Existing mappings ----

    [Fact]
    public void An_existing_mapping_keeps_its_depth_and_processes_and_says_so()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        var save = Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save");
        Directory.CreateDirectory(save);
        deck.Ok("map", "add", "cemu.save", "--target", "cemu/save", "--source", save, "--process", "Cemu");

        var run = deck.Ok("app", "add", "cemu");

        Assert.Contains("The existing mapping's entry depth (1) is kept; the definition says 2.", run.Out, StringComparison.Ordinal);
        var path = DeviceFile(deck).GetProperty("paths").GetProperty("cemu.save");
        Assert.Equal("Cemu", path.GetProperty("process_names")[0].GetString());
    }

    [Fact]
    public void An_existing_mapping_with_another_target_is_a_conflict()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        var save = Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save");
        Directory.CreateDirectory(save);
        deck.Ok("map", "add", "cemu.save", "--target", "cemu/other", "--source", save);

        var run = deck.Expect(CliApp.ExitRefused, "app", "add", "cemu");

        Assert.Contains("already exists with target 'cemu/other'", run.Err, StringComparison.Ordinal);
    }

    // ---- Dry run and file formats ----

    [Fact]
    public void A_dry_run_writes_nothing()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        Directory.CreateDirectory(Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01"));
        var before = DeviceFile(deck).ToString();

        var run = deck.Ok("app", "add", "cemu", "--dry-run");

        Assert.Contains("Dry run: nothing was changed.", run.Out, StringComparison.Ordinal);
        Assert.Contains("(not there yet)", run.Out, StringComparison.Ordinal);
        Assert.Equal(before, DeviceFile(deck).ToString());
        Assert.False(MappingExists(deck, "cemu.save"));
    }

    [Fact]
    public void A_device_file_stays_at_format_1_until_an_app_is_added()
    {
        using var sandbox = new Sandbox();
        var deck = NewDevice(sandbox);
        deck.Ok("map", "add", CliDevice.MappingId, "--target", "ppsspp/SAVEDATA", "--source", deck.Source);

        Assert.Equal(1, DeviceFile(deck).GetProperty("schema").GetInt32());
        Assert.False(DeviceFile(deck).TryGetProperty("apps", out _));

        Directory.CreateDirectory(Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save"));
        deck.Ok("app", "add", "cemu");

        Assert.Equal(2, DeviceFile(deck).GetProperty("schema").GetInt32());
    }
}
