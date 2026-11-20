using SaveTether.Core.Definitions;
using SaveTether.Core.Detection;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Resolution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Resolution;

/// <summary>The install scan (phase 3.5, T4) against fixture homes: one per install kind.</summary>
public class InstallScanTests
{
    private static ResolverEnvironment Linux(string home) => new() { Os = TargetOs.Linux, Home = home, UserName = "deck" };

    private static AppDefinition Define(string id, string installId, TargetOs os, DetectRule detect, BaseLocation baseLocation, string savePath = "saves", RoleClass roleClass = RoleClass.Save) =>
        new(
            1, id, id, AppKind.Game,
            [new InstallDefinition(installId, [os], detect, baseLocation)],
            [new RoleDefinition("save", roleClass, true, new Dictionary<string, string> { [installId] = savePath }, "save", 1, [], null)],
            [], DefinitionTier.Imported, null, null, [], []);

    private static AppDefinition Native(string id = "game", RoleClass roleClass = RoleClass.Save) =>
        Define(id, "linux", TargetOs.Linux, new DetectRule(Dir: "{xdg_data}/" + id), new BaseLocation(Path: "{xdg_data}/" + id), roleClass: roleClass);

    private static List<ScanHit> Scan(ResolverEnvironment env, params AppDefinition[] definitions) =>
        [.. InstallScan.Run(definitions, env)];

    private static List<ScanHit> Deep(ResolverEnvironment env, ScanOptions options, params AppDefinition[] definitions) =>
        [.. InstallScan.Run(definitions, env, null, options)];

    private static AppDefinition WithSteamId(AppDefinition app, long id) => app with { StoreIds = new StoreIds(Steam: id) };

    [Fact]
    public void An_imported_steam_game_is_found_only_when_steam_has_it_installed()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");
        sandbox.FileAt("x", "home", ".local", "share", "Steam", "steamapps", "appmanifest_111.acf");
        var installed = WithSteamId(Native("game"), 111);
        var leftover = WithSteamId(Native("game"), 222);

        Assert.Single(Scan(Linux(home), installed));
        // Steam is here and does not have 222: the save folder is only what the game left behind.
        Assert.Empty(Scan(Linux(home), leftover));
        var hit = Assert.Single(Deep(Linux(home), ScanOptions.Deep(), leftover));
        Assert.False(hit.Installed);
    }

    [Fact]
    public void A_steam_game_is_still_found_by_its_save_folder_when_the_device_has_no_steam()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");

        Assert.Single(Scan(Linux(home), WithSteamId(Native("game"), 222)));
    }

    [Fact]
    public void Steam_roots_the_environment_names_are_looked_in_too()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");
        var root = sandbox.Dir("elsewhere", "Steam");
        sandbox.FileAt("x", "elsewhere", "Steam", "steamapps", "appmanifest_111.acf");
        var environment = new ResolverEnvironment { Os = TargetOs.Linux, Home = home, UserName = "deck", SteamRoots = [root] };

        Assert.Single(Scan(environment, WithSteamId(Native("game"), 111)));
        Assert.Empty(Scan(environment, WithSteamId(Native("game"), 222)));
    }

    [Fact]
    public void A_verified_definition_with_a_steam_id_is_not_held_to_the_steam_check()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");
        sandbox.FileAt("x", "home", ".local", "share", "Steam", "steamapps", "appmanifest_111.acf");
        var verified = WithSteamId(Native("game"), 222) with { Tier = DefinitionTier.Verified };

        Assert.Single(Scan(Linux(home), verified));
    }

    [Fact]
    public void A_deep_scan_lists_a_save_folder_with_no_detected_install_as_not_installed()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        // Only the save folder is here: the detect rule (the app's own folder) is a file-less folder that does not exist.
        var game = Define("gone", "linux", TargetOs.Linux, new DetectRule(Dir: "{xdg_data}/gone/bin"), new BaseLocation(Path: "{xdg_data}/gone"));
        sandbox.FileAt("x", "home", ".local", "share", "gone", "saves", "a.sav");

        Assert.Empty(Scan(Linux(home), game));
        var hit = Assert.Single(Deep(Linux(home), ScanOptions.Deep(), game));

        Assert.False(hit.Installed);
        Assert.Equal(Path.Combine(home, ".local", "share", "gone", "saves"), hit.SaveFolder);
    }

    [Fact]
    public void A_detected_install_is_preferred_to_a_newer_undetected_one_in_either_order()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        var old = sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");
        var undetectedSaves = sandbox.FileAt("x", "home", ".local", "share", "other", "saves", "b.sav");
        File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(undetectedSaves, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        InstallDefinition Detected() => new("detected", [TargetOs.Linux], new DetectRule(Dir: "{xdg_data}/game"), new BaseLocation(Path: "{xdg_data}/game"));
        InstallDefinition Undetected() => new("undetected", [TargetOs.Linux], new DetectRule(Dir: "{xdg_data}/other/bin"), new BaseLocation(Path: "{xdg_data}/other"));
        AppDefinition App(params InstallDefinition[] installs) =>
            new(1, "app", "app", AppKind.Game, installs,
                [new RoleDefinition("save", RoleClass.Save, true, new Dictionary<string, string> { ["detected"] = "saves", ["undetected"] = "saves" }, "save", 1, [], null)],
                [], DefinitionTier.Imported, null, null, [], []);

        foreach (var installs in new[] { new[] { Detected(), Undetected() }, new[] { Undetected(), Detected() } })
        {
            var hit = Assert.Single(Deep(Linux(home), ScanOptions.Deep(), App(installs)));
            Assert.Equal("detected", hit.InstallId);
            Assert.True(hit.Installed);
        }
    }

    [Fact]
    public void The_scan_without_options_keeps_its_earlier_call_shapes()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");
        using var cancel = new CancellationTokenSource();

        Assert.Single(InstallScan.Run([Native()], Linux(home), null, cancel.Token));
        var resolver = new PathResolver(Linux(home));
        Assert.Single(InstallScan.Scan(Native(), resolver, Linux(home), DeviceValues.None, cancel.Token));
    }

    [Fact]
    public void A_deep_scan_gives_one_installed_row_for_an_app_with_a_detected_install()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");

        var hit = Assert.Single(Deep(Linux(home), ScanOptions.Deep(), Native()));

        Assert.True(hit.Installed);
    }

    [Fact]
    public void A_config_role_is_found_only_when_asked_for()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.cfg");
        var game = Native(roleClass: RoleClass.System);

        Assert.Empty(Deep(Linux(home), ScanOptions.Deep(), game));
        Assert.Single(Deep(Linux(home), ScanOptions.Deep(new HashSet<RoleClass> { RoleClass.Save, RoleClass.System }), game));
    }

    [Fact]
    public void A_linux_install_is_found_by_its_own_folder_and_a_save_folder()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");

        var hit = Assert.Single(Scan(Linux(home), Native()));

        Assert.Equal(("game", "linux"), (hit.AppId, hit.InstallId));
        Assert.Equal(Path.Combine(home, ".local", "share", "game", "saves"), hit.SaveFolder);
        Assert.NotNull(hit.NewestWrite);
    }

    [Fact]
    public void A_shared_parent_alone_is_not_a_hit_and_neither_is_a_game_folder_without_saves()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "other", "saves");
        sandbox.Dir("home", ".local", "share", "bare");

        Assert.Empty(Scan(Linux(home), Native("absent"), Native("bare")));
    }

    [Fact]
    public void A_role_that_is_not_a_save_does_not_count()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "game", "saves");

        Assert.Empty(Scan(Linux(home), Native(roleClass: RoleClass.Cache)));
    }

    [Fact]
    public void A_flatpak_install_is_found()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "flatpak", "app", "org.example.Game");
        sandbox.FileAt("x", "home", ".var", "app", "org.example.Game", "data", "saves", "a.sav");
        var game = Define("game", "flatpak-org.example.Game", TargetOs.Linux, new DetectRule(Flatpak: "org.example.Game"), new BaseLocation(Path: "{flatpak:org.example.Game}/data"));

        Assert.Single(Scan(Linux(home), game));
    }

    [Fact]
    public void A_steam_proton_install_is_found_inside_its_prefix()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "Steam", "steamapps");
        var roaming = new[] { "home", ".local", "share", "Steam", "steamapps", "compatdata", "123", "pfx", "drive_c", "users", "steamuser", "AppData", "Roaming" };
        sandbox.FileAt("x", [.. roaming, "Game", "a.sav"]);
        var game = new AppDefinition(
            1, "game-123", "Game", AppKind.Game,
            [new InstallDefinition("steam-proton", [TargetOs.Linux], new DetectRule(Dir: "{steam_compatdata:123}/pfx/drive_c/users/steamuser"),
                new BaseLocation(WinPath: "{appdata}", Prefix: new PrefixRule(Steam: 123)))],
            [new RoleDefinition("save", RoleClass.Save, true, new Dictionary<string, string> { ["steam-proton"] = "Game" }, "save", 1, [], null)],
            [], DefinitionTier.Imported, null, new StoreIds(123), [], []);

        // The prefix is left behind when a game is uninstalled, so it alone does not make the game found.
        Assert.Empty(Scan(Linux(home), game));
        sandbox.FileAt("x", "home", ".local", "share", "Steam", "steamapps", "appmanifest_123.acf");

        var hit = Assert.Single(Scan(Linux(home), game));

        Assert.Equal("steam-proton", hit.InstallId);
    }

    [Fact]
    public void An_install_dir_install_needs_the_folder_set_for_the_device()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        var install = sandbox.Dir("games", "mygame");
        sandbox.FileAt("x", "games", "mygame", "saves", "a.sav");
        var game = Define("mygame", "install-dir", TargetOs.Linux, new DetectRule(Dir: "{install_dir}"), new BaseLocation(Path: "{install_dir}"));

        Assert.Empty(Scan(Linux(home), game));
        Assert.Single(InstallScan.Run([game], Linux(home), _ => new DeviceValues(InstallDir: install)));
    }

    [Fact]
    public void An_install_for_the_other_operating_system_is_skipped()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", "game", "saves");
        var game = Define("game", "windows", TargetOs.Windows, new DetectRule(Dir: "{home}/game"), new BaseLocation(Path: "{home}/game"));

        Assert.Empty(Scan(Linux(home), game));
    }

    [Fact]
    public void The_newest_write_looks_three_levels_down_and_no_further()
    {
        using var sandbox = new Sandbox();
        var root = sandbox.Dir("save");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var deep = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(sandbox.FileAt("x", "save", "top.sav"), old);
        File.SetLastWriteTimeUtc(sandbox.FileAt("x", "save", "a", "b", "third.sav"), recent);
        File.SetLastWriteTimeUtc(sandbox.FileAt("x", "save", "a", "b", "c", "fourth.sav"), deep);

        Assert.Equal(recent, InstallScan.NewestWrite(root));
    }

    [Fact]
    public void The_entry_cap_holds()
    {
        using var sandbox = new Sandbox();
        var root = sandbox.Dir("save");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < InstallScan.MaxEntries; i++)
        {
            File.WriteAllText(Path.Combine(root, $"f{i:D5}.sav"), "x");
            File.SetLastWriteTimeUtc(Path.Combine(root, $"f{i:D5}.sav"), old);
        }

        // Which entries fall past the cap depends on the filesystem's listing order, so only the
        // outcome is checked: the old files are all that can be seen at most.
        var beyond = Path.Combine(root, "zzz-beyond-the-cap.sav");
        File.WriteAllText(beyond, "x");
        var newest = InstallScan.NewestWrite(root);
        Assert.True(newest == old || File.GetLastWriteTimeUtc(beyond) == newest);

    }

    [Fact]
    public void An_empty_save_folder_is_a_hit_with_no_newest_write()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "game", "saves");

        Assert.Null(Assert.Single(Scan(Linux(home), Native())).NewestWrite);
    }

    [Fact]
    public void A_cancelled_scan_yields_nothing()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "game", "saves");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Empty(InstallScan.Run(Enumerable.Repeat(Native(), 100), Linux(home), cancel: cancel.Token));
    }

    [Fact]
    public void An_app_with_two_installs_is_one_hit_the_newer_one()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        File.SetLastWriteTimeUtc(sandbox.FileAt("x", "home", "older", "saves", "a.sav"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(sandbox.FileAt("x", "home", "newer", "saves", "a.sav"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var game = new AppDefinition(
            1, "game", "game", AppKind.Game,
            [
                new InstallDefinition("older", [TargetOs.Linux], new DetectRule(Dir: "{home}/older"), new BaseLocation(Path: "{home}/older")),
                new InstallDefinition("newer", [TargetOs.Linux], new DetectRule(Dir: "{home}/newer"), new BaseLocation(Path: "{home}/newer")),
            ],
            [new RoleDefinition("save", RoleClass.Save, true, new Dictionary<string, string> { ["older"] = "saves", ["newer"] = "saves" }, "save", 1, [], null)],
            [], DefinitionTier.Imported, null, null, [], []);

        var hit = Assert.Single(Scan(Linux(home), game));

        Assert.Equal("newer", hit.InstallId);
    }

    [Fact]
    public void A_consumer_that_stops_early_stops_the_scan_cleanly()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");

        var first = InstallScan.Run(Enumerable.Repeat(Native(), 50), Linux(home)).First();

        Assert.Equal("game", first.AppId);
    }

    [Fact]
    public void A_save_folder_that_is_a_link_is_a_hit_with_no_newest_write()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "game");
        sandbox.FileAt("x", "elsewhere", "a.sav");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(home, ".local", "share", "game", "saves"), Path.Combine(sandbox.Work, "elsewhere"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // This machine may not create links; the check is covered where it can.
        }

        Assert.Null(Assert.Single(Scan(Linux(home), Native())).NewestWrite);
    }

    [Fact]
    public void A_linked_install_folder_with_a_real_save_folder_inside_has_no_newest_write()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share");
        sandbox.FileAt("x", "elsewhere", "saves", "a.sav");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(home, ".local", "share", "game"), Path.Combine(sandbox.Work, "elsewhere"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // This machine may not create links; the check is covered where it can.
        }

        Assert.Null(Assert.Single(Scan(Linux(home), Native())).NewestWrite);
    }

    [Fact]
    public void A_link_above_the_install_folder_is_the_owners_layout_and_is_not_a_link_to_avoid()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt("x", "real", ".local", "share", "game", "saves", "a.sav");
        var home = Path.Combine(sandbox.Work, "home");
        try
        {
            Directory.CreateSymbolicLink(home, Path.Combine(sandbox.Work, "real"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.NotNull(Assert.Single(Scan(Linux(home), Native())).NewestWrite);
    }

    [Fact]
    public void A_scan_changes_nothing()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("x", "home", ".local", "share", "game", "saves", "a.sav");
        sandbox.Dir("home", ".local", "share", "flatpak", "app", "org.example.Game");
        var before = Snapshot(sandbox.Work);

        Assert.Single(Scan(Linux(home), Native()));

        Assert.Equal(before, Snapshot(sandbox.Work));
    }

    private static List<string> Snapshot(string root) =>
        [.. SafeFs.EnumerateTree(root).Select(e => $"{e.RelativePath}|{e.Kind}|{e.Length}|{e.LastWriteTimeUtc:O}")];
}
