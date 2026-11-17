using System.Text.RegularExpressions;
using SaveTether.Core.Definitions;
using SaveTether.Core.Resolution;
using SaveTether.Tests.Definitions;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Resolution;

/// <summary>
/// The resolver against fixture trees: a fake home, mount table, EmuDeck settings file and Wine
/// prefixes, so no real SD card, EmuDeck or Wine is needed. Paths are joined with the separator of
/// the machine running the tests, so the same tests run on Windows and Linux.
/// </summary>
public partial class PathResolverTests
{
    private static ResolverEnvironment Linux(string home, string? mounts = null, Dictionary<string, string>? variables = null, string? user = "deck") => new()
    {
        Os = TargetOs.Linux,
        Home = home,
        UserName = user,
        GetVariable = name => variables is not null && variables.TryGetValue(name, out var value) ? value : null,
        ReadMounts = () => mounts,
    };

    private static ResolverEnvironment Windows(string home) => new()
    {
        Os = TargetOs.Windows,
        Home = home,
        UserName = "owner",
        AppData = Path.Combine(home, "AppData", "Roaming"),
        LocalAppData = Path.Combine(home, "AppData", "Local"),
        LocalLow = Path.Combine(home, "AppData", "LocalLow"),
        Documents = Path.Combine(home, "Documents"),
    };

    private static string Ok(ResolveResult result)
    {
        Assert.True(result.IsResolved, result.ToString());
        return result.Path!;
    }

    private static void Fails(ResolveResult result, ResolveProblem problem, string? messagePart = null)
    {
        Assert.False(result.IsResolved, $"Expected {problem} but resolved to {result.Path}");
        Assert.Equal(problem, result.Problem);
        if (messagePart is not null)
        {
            Assert.Contains(messagePart, result.Message, StringComparison.Ordinal);
        }
    }

    // ---- Plain tokens on Linux ----

    [Fact]
    public void Home_and_the_xdg_folders_default_under_home()
    {
        var resolver = new PathResolver(Linux("/home/deck"));

        Assert.Equal("/home/deck", Ok(resolver.Resolve("{home}", DeviceValues.None)));
        Assert.Equal(Path.Combine("/home/deck", ".config", "Cemu"), Ok(resolver.Resolve("{xdg_config}/Cemu", DeviceValues.None)));
        Assert.Equal(Path.Combine("/home/deck", ".local/share".Replace('/', Path.DirectorySeparatorChar), "Cemu", "mlc01"), Ok(resolver.Resolve("{xdg_data}/Cemu/mlc01", DeviceValues.None)));
    }

    [Fact]
    public void Xdg_variables_are_used_when_absolute_and_ignored_when_relative()
    {
        var resolver = new PathResolver(Linux("/home/deck", variables: new() { ["XDG_DATA_HOME"] = "/data/xdg", ["XDG_CONFIG_HOME"] = "relative/config" }));

        Assert.Equal(Path.Combine("/data/xdg", "Cemu"), Ok(resolver.Resolve("{xdg_data}/Cemu", DeviceValues.None)));
        Assert.Equal(Path.Combine("/home/deck", ".config", "Cemu"), Ok(resolver.Resolve("{xdg_config}/Cemu", DeviceValues.None)));
    }

    [Fact]
    public void A_flatpak_token_is_the_apps_folder_under_var_app()
    {
        var resolver = new PathResolver(Linux("/home/deck"));

        Assert.Equal(
            Path.Combine("/home/deck", ".var", "app", "org.ppsspp.PPSSPP", "config", "ppsspp", "PSP"),
            Ok(resolver.Resolve("{flatpak:org.ppsspp.PPSSPP}/config/ppsspp/PSP", DeviceValues.None)));
    }

    [Fact]
    public void An_absolute_path_with_no_token_is_used_as_it_is()
    {
        var resolver = new PathResolver(Linux("/home/deck"));

        Assert.Equal(Path.Combine("/", "opt", "Cemu"), Ok(resolver.Resolve("/opt/Cemu", DeviceValues.None)));
    }

    [Theory]
    [InlineData("{appdata}/x")]
    [InlineData("{localappdata}/x")]
    [InlineData("{locallow}/x")]
    [InlineData("{documents}/x")]
    public void Windows_folders_outside_a_winpath_are_refused_on_linux(string template)
    {
        Fails(new PathResolver(Linux("/home/deck")).Resolve(template, DeviceValues.None), ResolveProblem.NotOnThisOs, "winpath");
    }

    [Theory]
    [InlineData("{xdg_config}/x")]
    [InlineData("{xdg_data}/x")]
    [InlineData("{flatpak:org.example.App}/x")]
    [InlineData("{sd_card}/x")]
    [InlineData("{emudeck}/x")]
    [InlineData("{steam_compatdata:377840}/x")]
    public void Linux_only_tokens_are_refused_on_windows(string template)
    {
        Fails(new PathResolver(Windows(@"C:\Users\owner")).Resolve(template, DeviceValues.None), ResolveProblem.NotOnThisOs, "only on Linux");
    }

    // ---- Windows ----

    [Fact]
    public void Windows_tokens_resolve_to_the_users_folders()
    {
        var home = @"C:\Users\owner";
        var resolver = new PathResolver(Windows(home));

        Assert.Equal(Path.Combine(home, "AppData", "Roaming", "Dolphin"), Ok(resolver.Resolve("{appdata}/Dolphin", DeviceValues.None)));
        Assert.Equal(Path.Combine(home, "AppData", "Local", "x"), Ok(resolver.Resolve("{localappdata}/x", DeviceValues.None)));
        Assert.Equal(Path.Combine(home, "AppData", "LocalLow", "SquareEnix"), Ok(resolver.Resolve("{locallow}/SquareEnix", DeviceValues.None)));
        Assert.Equal(Path.Combine(home, "Documents", "My Games"), Ok(resolver.Resolve("{documents}/My Games", DeviceValues.None)));
    }

    [Fact]
    public void A_windows_folder_the_system_did_not_report_is_missing()
    {
        var env = new ResolverEnvironment { Os = TargetOs.Windows, Home = @"C:\Users\owner" };

        Fails(new PathResolver(env).Resolve("{locallow}/x", DeviceValues.None), ResolveProblem.Missing);
    }

    // ---- {install_dir} ----

    [Fact]
    public void Install_dir_needs_a_value_for_this_device()
    {
        var resolver = new PathResolver(Linux("/home/deck"));

        Fails(resolver.Resolve("{install_dir}/mlc01", DeviceValues.None), ResolveProblem.NeedsValue, "{install_dir}");
        Assert.Equal(Path.Combine("/opt/cemu", "mlc01"), Ok(resolver.Resolve("{install_dir}/mlc01", new DeviceValues(InstallDir: "/opt/cemu"))));
        Fails(resolver.Resolve("{install_dir}/mlc01", new DeviceValues(InstallDir: "opt/cemu")), ResolveProblem.Invalid, "absolute");
    }

    [Fact]
    public void Install_dir_works_on_windows_too()
    {
        var resolver = new PathResolver(Windows(@"C:\Users\owner"));

        Assert.Equal(Path.Combine(@"D:\Emu\Cemu", "mlc01"), Ok(resolver.Resolve("{install_dir}/mlc01", new DeviceValues(InstallDir: @"D:\Emu\Cemu"))));
    }

    // ---- {sd_card} ----

    private const string DeckMounts = """
        /dev/nvme0n1p8 / btrfs ro,relatime 0 0
        /dev/nvme0n1p8 /home ext4 rw,relatime 0 0
        tmpfs /tmp tmpfs rw 0 0
        /dev/mmcblk0p1 /run/media/deck/SD\040Card ext4 rw,nosuid,nodev,relatime 0 0
        """;

    [Fact]
    public void The_sd_card_is_found_from_the_mount_table()
    {
        var resolver = new PathResolver(Linux("/home/deck", mounts: DeckMounts));

        Assert.Equal(Path.Combine("/run/media/deck/SD Card", "Emulation"), Ok(resolver.Resolve("{sd_card}/Emulation", DeviceValues.None)));
    }

    [Fact]
    public void A_missing_sd_card_is_reported_not_guessed()
    {
        var mounts = "/dev/nvme0n1p8 /home ext4 rw 0 0\n";

        Fails(new PathResolver(Linux("/home/deck", mounts: mounts)).Resolve("{sd_card}/Emulation", DeviceValues.None), ResolveProblem.Missing, "No SD card");
        Fails(new PathResolver(Linux("/home/deck", mounts: null)).Resolve("{sd_card}/Emulation", DeviceValues.None), ResolveProblem.Missing, "mount table");
    }

    [Fact]
    public void An_mmc_device_mounted_outside_removable_media_is_not_an_sd_card()
    {
        var mounts = "/dev/mmcblk0p2 / ext4 rw 0 0\n/dev/mmcblk0p1 /boot vfat rw 0 0\n";

        Fails(new PathResolver(Linux("/home/pi", mounts: mounts)).Resolve("{sd_card}", DeviceValues.None), ResolveProblem.Missing);
    }

    [Fact]
    public void Two_sd_cards_are_ambiguous_unless_one_is_set_for_the_device()
    {
        var mounts = DeckMounts + "\n/dev/mmcblk1p1 /media/deck/other vfat rw 0 0\n";
        var resolver = new PathResolver(Linux("/home/deck", mounts: mounts));

        Fails(resolver.Resolve("{sd_card}", DeviceValues.None), ResolveProblem.Ambiguous, "/media/deck/other");
        Assert.Equal("/media/deck/other", Ok(resolver.Resolve("{sd_card}", new DeviceValues(SdCard: "/media/deck/other"))));
    }

    // ---- {emudeck} ----

    [Fact]
    public void EmuDeck_root_is_read_from_its_settings_file()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("""
            #!/bin/bash
            expert=false
            emulationPath="/run/media/deck/SD Card/Emulation/"
            romsPath=/run/media/deck/SD Card/Emulation/roms
            """, "home", ".config", "EmuDeck", "settings.sh");

        var resolved = Ok(new PathResolver(Linux(home)).Resolve("{emudeck}/storage/eden/nand", DeviceValues.None));

        Assert.Equal(Path.Combine("/run/media/deck/SD Card/Emulation", "storage", "eden", "nand"), resolved);
    }

    [Theory]
    [InlineData("emulationPath=$HOME/Emulation")]
    [InlineData("export emulationPath='${HOME}/Emulation'")]
    [InlineData("emulationPath=~/Emulation")]
    public void EmuDeck_root_under_home_is_expanded(string line)
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt(line + "\n", "home", ".config", "EmuDeck", "settings.sh");

        Assert.Equal(home + "/Emulation", Ok(new PathResolver(Linux(home)).Resolve("{emudeck}", DeviceValues.None)));
    }

    [Fact]
    public void EmuDeck_missing_or_without_the_setting_is_reported()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        var resolver = new PathResolver(Linux(home));

        Fails(resolver.Resolve("{emudeck}", DeviceValues.None), ResolveProblem.Missing, "not found");

        sandbox.FileAt("romsPath=/x\n", "home", ".config", "EmuDeck", "settings.sh");
        Fails(resolver.Resolve("{emudeck}", DeviceValues.None), ResolveProblem.Missing, "emulationPath");
    }

    // ---- Malformed templates ----

    [Theory]
    [InlineData("relative/path", "must start with a token")]
    [InlineData("{nope}/x", "unknown token")]
    [InlineData("{home", "closing")]
    [InlineData("{home}x", "'/' after its token")]
    [InlineData("{home}/", "ends with")]
    [InlineData("{home}/a/../b", "'..'")]
    [InlineData("{home}/a//b", "''")]
    [InlineData("{home}/./b", "'.'")]
    [InlineData(@"{home}/a\b", @"'a\b'")]
    [InlineData("{home}/{xdg_data}", "'{xdg_data}'")]
    [InlineData("{flatpak}/x", "needs an argument")]
    [InlineData("{flatpak:}/x", "needs an argument")]
    [InlineData("{flatpak:a/b}/x", "not a valid argument")]
    [InlineData("{flatpak:..}/x", "not a valid argument")]
    [InlineData("{home:x}/y", "takes no argument")]
    [InlineData("{steam_compatdata:abc}/pfx", "numeric")]
    public void A_malformed_template_is_invalid(string template, string messagePart)
    {
        Fails(new PathResolver(Linux("/home/deck")).Resolve(template, DeviceValues.None), ResolveProblem.Invalid, messagePart);
    }

    // ---- winpath on Windows ----

    [Fact]
    public void A_winpath_on_windows_resolves_natively_and_ignores_the_prefix()
    {
        var home = @"C:\Users\owner";
        var resolver = new PathResolver(Windows(home));

        Assert.Equal(
            Path.Combine(home, "AppData", "LocalLow", "SquareEnix", "FINAL FANTASY IX"),
            Ok(resolver.ResolveWinPath("{locallow}/SquareEnix/FINAL FANTASY IX", new PrefixRule(Steam: 377840), DeviceValues.None)));
        Assert.Equal(Path.Combine(@"D:\", "Games", "Saves"), Ok(resolver.ResolveWinPath("D:/Games/Saves", null, DeviceValues.None)));
    }

    // ---- winpath on Linux ----

    /// <summary>A Wine prefix with drive_c/users/&lt;each user&gt;, plus "Public".</summary>
    private static string Prefix(Sandbox sandbox, string name, params string[] users)
    {
        var prefix = sandbox.Dir(name);
        sandbox.Dir(name, "drive_c", "users", "Public");
        foreach (var user in users)
        {
            sandbox.Dir(name, "drive_c", "users", user, "AppData", "LocalLow");
        }

        return prefix;
    }

    [Fact]
    public void A_winpath_on_linux_resolves_inside_the_prefix_named_by_path()
    {
        using var sandbox = new Sandbox();
        var prefix = Prefix(sandbox, "pfx", "steamuser");
        var resolver = new PathResolver(Linux("/home/deck"));
        var profile = Path.Combine(prefix, "drive_c", "users", "steamuser");

        var location = new BaseLocation(WinPath: "{locallow}/SquareEnix", Prefix: new PrefixRule(Path: prefix.Replace('\\', '/')));

        Assert.Equal(Path.Combine(profile, "AppData", "LocalLow", "SquareEnix"), Ok(resolver.ResolveBase(location, DeviceValues.None)));
        Assert.Equal(Path.Combine(profile, "AppData", "Roaming", "x"), Ok(resolver.ResolveWinPath("{appdata}/x", location.Prefix, DeviceValues.None)));
        Assert.Equal(Path.Combine(profile, "AppData", "Local", "x"), Ok(resolver.ResolveWinPath("{localappdata}/x", location.Prefix, DeviceValues.None)));
        Assert.Equal(Path.Combine(profile, "Saved Games"), Ok(resolver.ResolveWinPath("{home}/Saved Games", location.Prefix, DeviceValues.None)));
        Assert.Equal(Path.Combine(prefix, "drive_c", "Games", "x"), Ok(resolver.ResolveWinPath("C:/Games/x", location.Prefix, DeviceValues.None)));
    }

    [Fact]
    public void A_prefix_path_may_use_a_token()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        Prefix(sandbox, Path.Combine("home", "Games", "vn", "prefix"), "deck");

        var resolved = Ok(new PathResolver(Linux(home)).ResolveWinPath("{locallow}/Studio", new PrefixRule(Path: "{home}/Games/vn/prefix"), DeviceValues.None));

        Assert.Equal(Path.Combine(home, "Games", "vn", "prefix", "drive_c", "users", "deck", "AppData", "LocalLow", "Studio"), resolved);
    }

    [Fact]
    public void A_steam_compatdata_folder_is_accepted_for_its_pfx()
    {
        using var sandbox = new Sandbox();
        Prefix(sandbox, Path.Combine("compatdata", "377840", "pfx"), "steamuser");
        var compat = sandbox.At("compatdata", "377840");

        var resolved = Ok(new PathResolver(Linux("/home/deck")).ResolveWinPath("{locallow}/x", null, new DeviceValues(WinePrefix: compat)));

        Assert.Equal(Path.Combine(compat, "pfx", "drive_c", "users", "steamuser", "AppData", "LocalLow", "x"), resolved);
    }

    [Fact]
    public void A_prefix_set_for_the_device_wins_over_the_definition()
    {
        using var sandbox = new Sandbox();
        var set = Prefix(sandbox, "set", "steamuser");

        var resolved = Ok(new PathResolver(Linux("/home/deck")).ResolveWinPath("{home}", new PrefixRule(Steam: 377840), new DeviceValues(WinePrefix: set)));

        Assert.Equal(Path.Combine(set, "drive_c", "users", "steamuser"), resolved);
    }

    [Fact]
    public void With_several_users_the_login_name_then_proton_user_is_taken_else_it_is_ambiguous()
    {
        using var sandbox = new Sandbox();
        var both = Prefix(sandbox, "both", "deck", "steamuser");
        var proton = Prefix(sandbox, "proton", "other", "steamuser");
        var neither = Prefix(sandbox, "neither", "alice", "bob");
        var resolver = new PathResolver(Linux("/home/deck", user: "deck"));

        Assert.Equal(Path.Combine(both, "drive_c", "users", "deck"), Ok(resolver.ResolveWinPath("{home}", null, new DeviceValues(WinePrefix: both))));
        Assert.Equal(Path.Combine(proton, "drive_c", "users", "steamuser"), Ok(resolver.ResolveWinPath("{home}", null, new DeviceValues(WinePrefix: proton))));
        Fails(resolver.ResolveWinPath("{home}", null, new DeviceValues(WinePrefix: neither)), ResolveProblem.Ambiguous, "alice, bob");
    }

    [Fact]
    public void Documents_falls_back_to_my_documents_only_when_that_is_the_one_there()
    {
        using var sandbox = new Sandbox();
        var old = Prefix(sandbox, "old", "steamuser");
        sandbox.Dir("old", "drive_c", "users", "steamuser", "My Documents");
        var current = Prefix(sandbox, "current", "steamuser");
        var resolver = new PathResolver(Linux("/home/deck"));

        Assert.Equal(
            Path.Combine(old, "drive_c", "users", "steamuser", "My Documents", "x"),
            Ok(resolver.ResolveWinPath("{documents}/x", null, new DeviceValues(WinePrefix: old))));
        Assert.Equal(
            Path.Combine(current, "drive_c", "users", "steamuser", "Documents", "x"),
            Ok(resolver.ResolveWinPath("{documents}/x", null, new DeviceValues(WinePrefix: current))));
    }

    [Fact]
    public void A_winpath_on_linux_without_a_usable_prefix_is_refused()
    {
        using var sandbox = new Sandbox();
        var notPrefix = sandbox.Dir("not-a-prefix");
        var resolver = new PathResolver(Linux("/home/deck"));

        Fails(resolver.ResolveWinPath("{locallow}/x", null, DeviceValues.None), ResolveProblem.NeedsValue, "Wine prefix");
        Fails(resolver.ResolveWinPath("{locallow}/x", null, new DeviceValues(WinePrefix: notPrefix)), ResolveProblem.Missing, "drive_c");
        Fails(resolver.ResolveWinPath("{locallow}/x", new PrefixRule(Steam: 377840), DeviceValues.None), ResolveProblem.Missing, "Steam was not found");
        Fails(resolver.ResolveWinPath("{locallow}/x", new PrefixRule(SteamShortcut: "Game"), DeviceValues.None), ResolveProblem.Missing, "No Steam shortcut");
        Fails(resolver.ResolveWinPath("{locallow}/x", new PrefixRule(Lutris: "game"), DeviceValues.None), ResolveProblem.Missing, "No Lutris game config");
    }

    [Fact]
    public void A_winpath_on_another_drive_is_not_looked_up_in_a_prefix()
    {
        using var sandbox = new Sandbox();
        var prefix = Prefix(sandbox, "pfx", "steamuser");

        Fails(new PathResolver(Linux("/home/deck")).ResolveWinPath("D:/Games", null, new DeviceValues(WinePrefix: prefix)), ResolveProblem.NotYetSupported, "D:");
    }

    [Theory]
    [InlineData("{xdg_data}/x", "may only start with")]
    [InlineData("{install_dir}/x", "may only start with")]
    [InlineData("SquareEnix/x", "must start with a token")]
    [InlineData("/home/x", "must start with a token")]
    [InlineData("{locallow}/../x", "'..'")]
    public void A_malformed_winpath_is_invalid(string winPath, string messagePart)
    {
        Fails(new PathResolver(Linux("/home/deck")).ResolveWinPath(winPath, null, DeviceValues.None), ResolveProblem.Invalid, messagePart);
    }

    // ---- Roles ----

    private const string Cemu = """
        {
          "schema": 1,
          "id": "cemu",
          "name": "Cemu",
          "kind": "emulator",
          "installs": [
            { "id": "win-portable", "os": "windows", "detect": { "dir": "{install_dir}/mlc01" }, "base": "{install_dir}/mlc01" },
            { "id": "linux-native", "os": "linux", "detect": { "dir": "{xdg_data}/Cemu/mlc01" }, "base": "{xdg_data}/Cemu/mlc01" },
          ],
          "roles": [
            { "id": "save", "class": "save", "default": true, "path": "usr/save", "target": "save", "entry_depth": 2 },
            { "id": "linux-only", "class": "cache", "default": false, "path": { "linux-native": "cache" }, "target": "cache" },
          ],
        }
        """;

    [Fact]
    public void A_role_resolves_under_its_installs_base()
    {
        var cemu = DefinitionLoader.Parse(Cemu, "cemu.jsonc").Definition!;
        var linux = new PathResolver(Linux("/home/deck"));
        var windows = new PathResolver(Windows(@"C:\Users\owner"));

        Assert.Equal(
            Path.Combine("/home/deck", ".local", "share", "Cemu", "mlc01", "usr", "save"),
            Ok(linux.ResolveRole(cemu.Installs[1], cemu.Roles[0], DeviceValues.None)));
        Assert.Equal(
            Path.Combine(@"D:\Emu\Cemu", "mlc01", "usr", "save"),
            Ok(windows.ResolveRole(cemu.Installs[0], cemu.Roles[0], new DeviceValues(InstallDir: @"D:\Emu\Cemu"))));
    }

    [Fact]
    public void A_role_on_an_install_for_another_os_or_without_that_role_is_refused()
    {
        var cemu = DefinitionLoader.Parse(Cemu, "cemu.jsonc").Definition!;
        var linux = new PathResolver(Linux("/home/deck"));
        var windows = new PathResolver(Windows(@"C:\Users\owner"));

        Fails(linux.ResolveRole(cemu.Installs[0], cemu.Roles[0], DeviceValues.None), ResolveProblem.NotOnThisOs, "win-portable");
        Fails(windows.ResolveRole(cemu.Installs[0], cemu.Roles[1], new DeviceValues(InstallDir: @"D:\Emu")), ResolveProblem.Invalid, "no path for install");
    }

    // ---- The design's own examples ----

    [GeneratedRegex(@"```jsonc\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsoncBlock();

    /// <summary>
    /// Every path in the design's example definitions is a valid template. Some cannot resolve on a
    /// bare test machine (no SD card, no install folder set), but none may be malformed.
    /// </summary>
    [Fact]
    public void Every_path_in_the_designs_examples_is_a_valid_template()
    {
        var design = File.ReadAllText(Path.Combine(DefinitionLoaderTests.FindRepositoryRoot(), "docs", "design", "save-redirection-design.md"));
        var definitions = JsoncBlock().Matches(design)
            .Select(m => DefinitionLoader.Parse(m.Groups[1].Value, "example.jsonc").Definition!)
            .Where(d => d.Id != "string")
            .ToList();
        Assert.NotEmpty(definitions);

        foreach (var os in new[] { TargetOs.Linux, TargetOs.Windows })
        {
            var resolver = new PathResolver(os == TargetOs.Linux ? Linux("/home/deck") : Windows(@"C:\Users\owner"));
            foreach (var install in definitions.SelectMany(d => d.Installs).Where(i => i.Os.Contains(os)))
            {
                var results = new List<ResolveResult> { resolver.ResolveBase(install.Base, DeviceValues.None) };
                if ((install.Detect.Dir ?? install.Detect.File) is { } detect)
                {
                    results.Add(resolver.Resolve(detect, DeviceValues.None));
                }

                Assert.All(results, result => Assert.NotEqual(ResolveProblem.Invalid, result.Problem));
            }
        }
    }
}
