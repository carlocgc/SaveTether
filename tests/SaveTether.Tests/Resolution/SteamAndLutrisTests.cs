using System.Text;
using SaveTether.Core.Definitions;
using SaveTether.Core.Resolution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Resolution;

/// <summary>
/// Steam and Lutris lookups against fixture trees: Steam's libraryfolders.vdf and shortcuts.vdf
/// and Lutris game configs written by the tests. No real Steam or Lutris is needed.
/// </summary>
public class SteamAndLutrisTests
{
    private const ulong FinalFantasyIx = 377840;
    private const ulong OtherGame = 228980;

    private static PathResolver Resolver(string home) => new(new ResolverEnvironment
    {
        Os = TargetOs.Linux,
        Home = home,
        UserName = "deck",
    });

    private static string Ok(ResolveResult result)
    {
        Assert.True(result.IsResolved, result.ToString());
        return result.Path!;
    }

    private static void Fails(ResolveResult result, ResolveProblem problem, string messagePart)
    {
        Assert.False(result.IsResolved, $"Expected {problem} but resolved to {result.Path}");
        Assert.Equal(problem, result.Problem);
        Assert.Contains(messagePart, result.Message, StringComparison.Ordinal);
    }

    /// <summary>A path as Steam writes it inside a text VDF: backslashes escaped.</summary>
    private static string VdfPath(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

    /// <summary>
    /// A home with native Steam at ~/.local/share/Steam and a second library on an "SD card",
    /// with libraryfolders.vdf saying which library holds which game.
    /// </summary>
    private static (string Home, string Steam, string Card) DeckWithTwoLibraries(Sandbox sandbox)
    {
        var home = sandbox.Dir("home");
        var steam = sandbox.Dir("home", ".local", "share", "Steam");
        sandbox.Dir("home", ".local", "share", "Steam", "steamapps");
        var card = sandbox.Dir("card", "SteamLibrary");
        sandbox.Dir("card", "SteamLibrary", "steamapps");

        sandbox.FileAt($$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"      "{{VdfPath(steam)}}"
                    "label"     ""
                    "apps"
                    {
                        "{{OtherGame}}"      "123456"
                    }
                }
                "1"
                {
                    "path"      "{{VdfPath(card)}}"
                    "apps"
                    {
                        "{{FinalFantasyIx}}"      "999"
                    }
                }
            }
            """, "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");
        return (home, steam, card);
    }

    /// <summary>A Proton prefix: compatdata/&lt;id&gt;/pfx/drive_c/users/steamuser.</summary>
    private static string ProtonPrefix(Sandbox sandbox, string library, ulong appId)
    {
        var compat = Path.Combine(library, "steamapps", "compatdata", appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.Combine(compat, "pfx", "drive_c", "users", "steamuser", "AppData", "LocalLow"));
        Directory.CreateDirectory(Path.Combine(compat, "pfx", "drive_c", "users", "Public"));
        return compat;
    }

    // ---- Text VDF ----

    [Fact]
    public void Text_vdf_reads_sections_comments_escapes_and_conditionals()
    {
        var root = Vdf.ParseText("""
            // A comment
            "outer"
            {
                "path"   "C:\\Program Files (x86)\\Steam"
                bare     value   [$WIN32]
                "quote"  "say \"hi\""
                "inner" { "k" "v" }
            }
            """);

        var outer = root.Get("OUTER")!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", outer.Get("path")!.Value);
        Assert.Equal("value", outer.Get("bare")!.Value);
        Assert.Equal("say \"hi\"", outer.Get("quote")!.Value);
        Assert.Equal("v", outer.Get("inner")!.Get("k")!.Value);
    }

    [Theory]
    [InlineData("\"a\" { \"b\" \"c\"")]
    [InlineData("}")]
    [InlineData("\"a\"")]
    [InlineData("\"a\" \"unclosed")]
    [InlineData("{ \"a\" \"b\" }")]
    public void Malformed_text_vdf_is_refused(string text)
    {
        Assert.Throws<VdfException>(() => Vdf.ParseText(text));
    }

    // ---- Binary VDF ----

    /// <summary>Writes Valve's binary KeyValues: type byte, NUL-terminated name, value; 0x08 ends a section.</summary>
    private sealed class BinaryVdf
    {
        private readonly List<byte> _bytes = [];

        public BinaryVdf Section(string name) => Type(0x00).Name(name);

        public BinaryVdf String(string name, string value) => Type(0x01).Name(name).Name(value);

        public BinaryVdf Int32(string name, uint value)
        {
            Type(0x02).Name(name);
            _bytes.AddRange(BitConverter.GetBytes(value));
            return this;
        }

        public BinaryVdf End() => Type(0x08);

        public byte[] ToArray() => [.. _bytes];

        private BinaryVdf Type(byte type)
        {
            _bytes.Add(type);
            return this;
        }

        private BinaryVdf Name(string text)
        {
            _bytes.AddRange(Encoding.UTF8.GetBytes(text));
            _bytes.Add(0);
            return this;
        }
    }

    /// <summary>A shortcuts.vdf holding one shortcut per (name, app ID) pair.</summary>
    private static byte[] Shortcuts(params (string Name, uint? AppId)[] shortcuts)
    {
        var vdf = new BinaryVdf().Section("shortcuts");
        for (var i = 0; i < shortcuts.Length; i++)
        {
            vdf.Section(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (shortcuts[i].AppId is { } id)
            {
                vdf.Int32("appid", id);
            }

            vdf.String("AppName", shortcuts[i].Name).String("Exe", "\"/games/x.exe\"").Section("tags").End().End();
        }

        return vdf.End().End().ToArray();
    }

    [Fact]
    public void Binary_vdf_reads_shortcuts_with_ids_as_unsigned()
    {
        var root = Vdf.ParseBinary(Shortcuts(("Game", 0xC0000001)));

        var shortcut = root.Get("shortcuts")!.Get("0")!;
        Assert.Equal("Game", shortcut.Get("appname")!.Value);
        Assert.Equal("3221225473", shortcut.Get("appid")!.Value);
    }

    [Fact]
    public void Truncated_or_unknown_binary_vdf_is_refused()
    {
        var whole = Shortcuts(("Game", 1u));

        Assert.Throws<VdfException>(() => Vdf.ParseBinary(whole.AsSpan(0, whole.Length - 3)));
        Assert.Throws<VdfException>(() => Vdf.ParseBinary(new byte[] { 0x09, (byte)'x', 0 }));
    }

    // ---- {steam_compatdata} and the steam prefix ----

    [Fact]
    public void A_game_on_a_second_library_is_found_through_libraryfolders()
    {
        using var sandbox = new Sandbox();
        var (home, _, card) = DeckWithTwoLibraries(sandbox);
        var compat = ProtonPrefix(sandbox, card, FinalFantasyIx);
        var resolver = Resolver(home);

        Assert.Equal(compat, Ok(resolver.Resolve($"{{steam_compatdata:{FinalFantasyIx}}}", DeviceValues.None)));
        Assert.Equal(
            Path.Combine(compat, "pfx", "drive_c", "users", "steamuser", "AppData", "LocalLow", "SquareEnix"),
            Ok(resolver.ResolveWinPath("{locallow}/SquareEnix", new PrefixRule(Steam: (long)FinalFantasyIx), DeviceValues.None)));
    }

    [Fact]
    public void The_library_listing_the_game_wins_even_before_its_prefix_exists()
    {
        using var sandbox = new Sandbox();
        var (home, _, card) = DeckWithTwoLibraries(sandbox);

        Assert.Equal(
            Path.Combine(card, "steamapps", "compatdata", "377840"),
            Ok(Resolver(home).Resolve("{steam_compatdata:377840}", DeviceValues.None)));
    }

    [Fact]
    public void An_unlisted_game_is_found_by_its_compatdata_folder_else_reported_missing()
    {
        using var sandbox = new Sandbox();
        var (home, steam, _) = DeckWithTwoLibraries(sandbox);
        var resolver = Resolver(home);

        Fails(resolver.Resolve("{steam_compatdata:111}", DeviceValues.None), ResolveProblem.Missing, "not installed");

        var compat = ProtonPrefix(sandbox, steam, 111);
        Assert.Equal(compat, Ok(resolver.Resolve("{steam_compatdata:111}", DeviceValues.None)));
    }

    [Fact]
    public void A_game_listed_in_two_libraries_is_ambiguous()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".local", "share", "Steam", "steamapps");
        var a = sandbox.Dir("a");
        var b = sandbox.Dir("b");
        sandbox.FileAt($$"""
            "libraryfolders"
            {
                "0" { "path" "{{VdfPath(a)}}" "apps" { "5" "1" } }
                "1" { "path" "{{VdfPath(b)}}" "apps" { "5" "1" } }
            }
            """, "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");

        Fails(Resolver(home).Resolve("{steam_compatdata:5}", DeviceValues.None), ResolveProblem.Ambiguous, "more than one library");
    }

    [Fact]
    public void Old_style_libraryfolders_and_flatpak_steam_are_read()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.Dir("home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps");
        var extra = sandbox.Dir("extra");
        sandbox.FileAt($$"""
            "LibraryFolders"
            {
                "TimeNextStatsReport"   "1234"
                "ContentStatsID"        "-1"
                "1"                     "{{VdfPath(extra)}}"
            }
            """, "home", ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");
        var compat = ProtonPrefix(sandbox, extra, 42);

        Assert.Equal(compat, Ok(Resolver(home).Resolve("{steam_compatdata:42}", DeviceValues.None)));
    }

    [Fact]
    public void A_broken_libraryfolders_file_is_named_and_the_install_folder_still_searched()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        var steam = sandbox.Dir("home", ".local", "share", "Steam");
        sandbox.FileAt("\"libraryfolders\" {", "home", ".local", "share", "Steam", "steamapps", "libraryfolders.vdf");
        var resolver = Resolver(home);

        Fails(resolver.Resolve("{steam_compatdata:7}", DeviceValues.None), ResolveProblem.Missing, "libraryfolders.vdf could not be read");

        var compat = ProtonPrefix(sandbox, steam, 7);
        Assert.Equal(compat, Ok(resolver.Resolve("{steam_compatdata:7}", DeviceValues.None)));
    }

    [Fact]
    public void A_steam_link_to_the_same_install_is_not_counted_twice()
    {
        using var sandbox = new Sandbox();
        var (home, steam, _) = DeckWithTwoLibraries(sandbox);
        sandbox.Dir("home", ".steam");
        if (!Sandbox.TrySymlink(Path.Combine(home, ".steam", "steam"), steam, directory: true))
        {
            return; // Windows without symlink rights; the Linux CI runs this.
        }

        ProtonPrefix(sandbox, steam, 9);

        Assert.True(Resolver(home).Resolve("{steam_compatdata:9}", DeviceValues.None).IsResolved);
    }

    // ---- steam_shortcut ----

    private static void WriteShortcuts(Sandbox sandbox, string userId, byte[] data) =>
        File.WriteAllBytes(
            Path.Combine(sandbox.Dir("home", ".local", "share", "Steam", "userdata", userId, "config"), "shortcuts.vdf"),
            data);

    [Fact]
    public void A_shortcut_is_found_by_name_and_its_prefix_by_its_id()
    {
        using var sandbox = new Sandbox();
        var (home, steam, _) = DeckWithTwoLibraries(sandbox);
        WriteShortcuts(sandbox, "1000", Shortcuts(("Other", 1u), ("My Visual Novel", 0xC0000001)));
        var compat = ProtonPrefix(sandbox, steam, 0xC0000001);

        Assert.Equal(
            Path.Combine(compat, "pfx", "drive_c", "users", "steamuser", "Documents", "Saves"),
            Ok(Resolver(home).ResolveWinPath("{documents}/Saves", new PrefixRule(SteamShortcut: "My Visual Novel"), DeviceValues.None)));
    }

    [Fact]
    public void A_shortcut_that_has_not_run_yet_or_does_not_exist_is_reported()
    {
        using var sandbox = new Sandbox();
        var (home, _, _) = DeckWithTwoLibraries(sandbox);
        WriteShortcuts(sandbox, "1000", Shortcuts(("Novel", 77u), ("No Id Yet", null)));
        var resolver = Resolver(home);

        Fails(resolver.ResolveWinPath("{home}", new PrefixRule(SteamShortcut: "Novel"), DeviceValues.None), ResolveProblem.Missing, "Run it once");
        Fails(resolver.ResolveWinPath("{home}", new PrefixRule(SteamShortcut: "No Id Yet"), DeviceValues.None), ResolveProblem.Missing, "no app ID yet");
        Fails(resolver.ResolveWinPath("{home}", new PrefixRule(SteamShortcut: "novel"), DeviceValues.None), ResolveProblem.Missing, "No Steam shortcut");
    }

    [Fact]
    public void Two_shortcuts_with_one_name_and_different_ids_are_ambiguous()
    {
        using var sandbox = new Sandbox();
        var (home, _, _) = DeckWithTwoLibraries(sandbox);
        WriteShortcuts(sandbox, "1000", Shortcuts(("Novel", 77u)));
        WriteShortcuts(sandbox, "2000", Shortcuts(("Novel", 78u)));

        Fails(Resolver(home).ResolveWinPath("{home}", new PrefixRule(SteamShortcut: "Novel"), DeviceValues.None), ResolveProblem.Ambiguous, "77, 78");
    }

    // ---- lutris ----

    private const string LutrisConfig = """
        game:
          exe: /home/deck/Games/novel/drive_c/Novel/novel.exe
          prefix: PREFIX
        system:
          prefix: /wrong
        wine:
          version: lutris-GE-Proton8-26-x86_64
        """;

    private static string LutrisPrefix(Sandbox sandbox, params string[] parts)
    {
        var prefix = sandbox.Dir(parts);
        sandbox.Dir([.. parts, "drive_c", "users", "deck", "AppData", "Roaming"]);
        return prefix;
    }

    [Fact]
    public void A_lutris_game_prefix_is_read_from_its_config()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        var prefix = LutrisPrefix(sandbox, "home", "Games", "novel");
        sandbox.FileAt(LutrisConfig.Replace("PREFIX", "\"" + prefix + "\"", StringComparison.Ordinal), "home", ".local", "share", "lutris", "games", "novel-1700000000.yml");

        Assert.Equal(
            Path.Combine(prefix, "drive_c", "users", "deck", "AppData", "Roaming", "Studio"),
            Ok(Resolver(home).ResolveWinPath("{appdata}/Studio", new PrefixRule(Lutris: "novel"), DeviceValues.None)));
    }

    [Fact]
    public void A_lutris_prefix_under_home_is_expanded_and_older_config_folders_are_read()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        LutrisPrefix(sandbox, "home", "Games", "novel");
        sandbox.FileAt(LutrisConfig.Replace("PREFIX", "~/Games/novel", StringComparison.Ordinal), "home", ".config", "lutris", "games", "novel-1.yml");

        var resolved = Ok(Resolver(home).ResolveWinPath("{home}", new PrefixRule(Lutris: "novel"), DeviceValues.None));

        Assert.Equal(Path.Combine(home + "/Games/novel", "drive_c", "users", "deck"), resolved);
    }

    [Fact]
    public void The_flatpak_lutris_config_folder_is_read_as_the_deck_has_it()
    {
        // The owner's Deck, 2026-11-17: Flatpak Lutris keeps game configs in config/lutris/games,
        // shaped like this (prefix with a trailing slash, multi-line exe, "system: {}").
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        var prefix = LutrisPrefix(sandbox, "home", "Documents", "visualnovelengine");
        sandbox.FileAt(
            $"game:\n  arch: win32\n  exe: /run/media/mmcblk0p1/VisualNovels/Escu:de/Re;Lord 1 The witch of Hertfort and\n    Stuffed Animals/game.exe\n  prefix: {prefix}/\n  working_dir: /run/media/mmcblk0p1/VisualNovels\nsystem: {{}}\nwine:\n  version: lutris-7.2-x86_64\n",
            "home", ".var", "app", "net.lutris.Lutris", "config", "lutris", "games", "sengoku-rance-1660413697.yml");

        var resolved = Ok(Resolver(home).ResolveWinPath("{home}", new PrefixRule(Lutris: "sengoku-rance"), DeviceValues.None));

        Assert.Equal(Path.Combine(prefix, "drive_c", "users", "deck"), resolved);
    }

    [Fact]
    public void Lutris_configs_for_other_slugs_or_without_a_prefix_do_not_count()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt(LutrisConfig.Replace("PREFIX", "/x", StringComparison.Ordinal), "home", ".local", "share", "lutris", "games", "novel-extra-1.yml");
        sandbox.FileAt(LutrisConfig.Replace("PREFIX", "/x", StringComparison.Ordinal), "home", ".local", "share", "lutris", "games", "novel-beta.yml");
        var resolver = Resolver(home);

        Fails(resolver.ResolveWinPath("{home}", new PrefixRule(Lutris: "novel"), DeviceValues.None), ResolveProblem.Missing, "No Lutris game config");

        sandbox.FileAt("game:\n  exe: /x.exe\nsystem:\n  prefix: /wrong\n", "home", ".local", "share", "lutris", "games", "novel-2.yml");
        Fails(resolver.ResolveWinPath("{home}", new PrefixRule(Lutris: "novel"), DeviceValues.None), ResolveProblem.Missing, "does not set a Wine prefix");
    }

    [Fact]
    public void Two_lutris_configs_with_different_prefixes_are_ambiguous()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt(LutrisConfig.Replace("PREFIX", "/a", StringComparison.Ordinal), "home", ".local", "share", "lutris", "games", "novel-1.yml");
        sandbox.FileAt(LutrisConfig.Replace("PREFIX", "/b", StringComparison.Ordinal), "home", ".local", "share", "lutris", "games", "novel-2.yml");

        Fails(Resolver(home).ResolveWinPath("{home}", new PrefixRule(Lutris: "novel"), DeviceValues.None), ResolveProblem.Ambiguous, "/a, /b");
    }

    [Theory]
    [InlineData("game:\n  prefix: /p\n", "/p")]
    [InlineData("game:\r\n  prefix: '/p q'\r\n", "/p q")]
    [InlineData("# comment\ngame:\n  exe: x\n  options:\n    prefix: /nested\n  prefix: /p\n", "/p")]
    [InlineData("system:\n  prefix: /wrong\n", null)]
    [InlineData("game:\n  prefix:\n", null)]
    public void Only_the_game_sections_own_prefix_is_read(string yaml, string? expected)
    {
        Assert.Equal(expected, LutrisGames.FindGamePrefix(yaml, "/home/deck"));
    }
}
