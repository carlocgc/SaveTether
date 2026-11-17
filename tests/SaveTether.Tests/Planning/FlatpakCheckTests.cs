using SaveTether.Core.Planning;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Planning;

/// <summary>
/// The Flatpak precheck (plan P2.6). The five permission sets below are the [Context] groups of
/// the Flathub builds, read on 2026-11-17 with "flatpak remote-info --show-metadata flathub &lt;id&gt;"
/// on Linux Mint (Flatpak 1.14.6). They are what a fresh install has, before any override.
/// </summary>
public class FlatpakCheckTests
{
    private const string Home = "/home/deck";

    private const string Xemu = """
        [Context]
        shared=ipc;network;
        sockets=fallback-x11;pulseaudio;wayland;
        devices=all;
        filesystems=host:ro;
        unset-environment=QEMU_AUDIO_DRV;
        """;

    private const string Dolphin = """
        [Context]
        shared=ipc;network;
        sockets=pulseaudio;x11;
        devices=all;
        features=bluetooth;
        filesystems=xdg-run/app/com.discordapp.Discord:create;xdg-config/kdeglobals:ro;xdg-run/app/com.discordapp.DiscordCanary:create;host:ro;xdg-run/gamescope-0:ro;
        """;

    private const string Ppsspp = """
        [Context]
        shared=ipc;network;
        sockets=pulseaudio;wayland;x11;
        devices=all;
        filesystems=host:ro;xdg-run/gamescope-0:ro;
        """;

    private const string MelonDs = """
        [Context]
        shared=network;ipc;
        sockets=x11;wayland;pulseaudio;fallback-x11;
        devices=all;
        filesystems=home;xdg-config/kdeglobals:ro;
        """;

    private const string Citra = """
        [Context]
        shared=network;ipc;
        sockets=x11;pulseaudio;
        devices=all;
        filesystems=xdg-run/app/com.discordapp.Discord:ro;xdg-config/kdeglobals:ro;host:ro;
        """;

    private const string Target = "/home/deck/Sync/savetether/ppsspp/SAVEDATA";

    private static bool CanWrite(string permissions, string path, Func<string, string?>? getVariable = null, string home = Home) =>
        FlatpakPermissions.CanWrite(FlatpakPermissions.ParseFilesystems(permissions), path, home, getVariable ?? (_ => null));

    private static string Context(string filesystems) => $"[Context]\nshared=network;\nfilesystems={filesystems}\n\n[Session Bus Policy]\norg.freedesktop.Flatpak=talk\n";

    // ---- The five emulators as Flathub ships them ----

    [Theory]
    [InlineData("xemu", false)]
    [InlineData("dolphin", false)]
    [InlineData("ppsspp", false)]
    [InlineData("melonds", true)]
    [InlineData("citra", false)]
    public void Flathub_defaults_let_only_melonds_write_the_sync_folder(string app, bool expected)
    {
        var permissions = app switch
        {
            "xemu" => Xemu,
            "dolphin" => Dolphin,
            "ppsspp" => Ppsspp,
            "melonds" => MelonDs,
            _ => Citra,
        };

        // host:ro lets the app see the shared folder but not save into it.
        Assert.Equal(expected, CanWrite(permissions, Target));
    }

    /// <summary>
    /// The owner's Deck, read on 2026-11-17 with "flatpak info --show-permissions": EmuDeck's
    /// user overrides of "host" have replaced Flathub's "host:ro".
    /// </summary>
    [Theory]
    [InlineData("/run/media/mmcblk0p1/Emulation/storage/xemu;host;", true)]
    [InlineData("xdg-run/app/com.discordapp.Discord:create;xdg-config/kdeglobals:ro;xdg-run/app/com.discordapp.DiscordCanary:create;host;xdg-run/gamescope-0:ro;", true)]
    [InlineData("host;xdg-run/gamescope-0:ro;", true)]
    [InlineData("home;xdg-config/kdeglobals:ro;host;", true)]
    [InlineData("xdg-run/app/com.discordapp.Discord:ro;xdg-config/kdeglobals:ro;host;", true)]
    [InlineData("xdg-run/pipewire-0;xdg-run/app/com.discordapp.Discord:create;xdg-config/kdeglobals:ro;/run/udev:ro;xdg-run/gamescope-0:ro;host;xdg-run/discord-ipc-0:create;", true)]
    [InlineData("home:ro;/media;xdg-config/kdeglobals:ro;/run/media;host;xdg-run/app/com.discordapp.Discord:create;", false)]
    public void The_decks_emulators_can_write_the_sync_folder_except_rpcs3(string filesystems, bool expected)
    {
        // RPCS3 has both "host" and "home:ro". The more specific entry decides, so home is read-only;
        // if Flatpak let "host" win instead, this only refuses where it could have allowed.
        Assert.Equal(expected, CanWrite(Context(filesystems), Target));
    }

    [Fact]
    public void The_more_specific_entry_decides()
    {
        Assert.False(CanWrite(Context("host;home:ro;"), Target));
        Assert.True(CanWrite(Context("host;home:ro;~/Sync;"), Target));
        Assert.True(CanWrite(Context("home:ro;home;"), Target));
        Assert.False(CanWrite(Context("home;~/Sync:ro;"), Target));
    }

    [Fact]
    public void An_override_granting_the_folder_makes_it_writable()
    {
        // What "flatpak info --show-permissions" prints after the suggested override: the grant joins the list.
        Assert.True(CanWrite(Context("host:ro;xdg-run/gamescope-0:ro;/home/deck/Sync/savetether;"), Target));
        Assert.True(CanWrite(Context("host;"), Target));
    }

    // ---- The entry forms ----

    [Theory]
    [InlineData("home;", true)]
    [InlineData("home:ro;", false)]
    [InlineData("home:rw;", true)]
    [InlineData("~/Sync;", true)]
    [InlineData("~/Sync:create;", true)]
    [InlineData("~/Other;", false)]
    [InlineData("/home/deck/Sync/savetether;", true)]
    [InlineData("/home/deck/Sync/savetether/ppsspp/SAVEDATA/deeper;", false)]
    [InlineData("host-os;host-etc;", false)]
    [InlineData("xdg-run/gamescope-0;", false)]
    [InlineData("home;!home;", false)]
    [InlineData("!home;home;", true)]
    [InlineData("host:ro;~/Sync;", true)]
    public void Entries_cover_what_flatpak_grants(string filesystems, bool expected)
    {
        Assert.Equal(expected, CanWrite(Context(filesystems), Target));
    }

    [Fact]
    public void Host_does_not_cover_the_folders_the_sandbox_keeps_its_own()
    {
        Assert.False(CanWrite(Context("host;"), "/tmp/Sync/savetether/x"));
        Assert.True(CanWrite(Context("host;"), "/run/media/deck/SD/Sync/savetether/x"));
    }

    [Fact]
    public void Filesystems_outside_the_context_group_are_ignored()
    {
        Assert.False(CanWrite("[Application]\nname=x\nfilesystems=home;\n[Context]\nshared=network;\n", Target));
    }

    [Fact]
    public void Xdg_entries_follow_the_variables_and_user_dirs()
    {
        using var sandbox = new Sandbox();
        var home = sandbox.Dir("home");
        sandbox.FileAt("XDG_DOCUMENTS_DIR=\"$HOME/Docs\"\n", "home", ".config", "user-dirs.dirs");
        var docs = Path.Combine(home, "Docs", "Sync", "savetether", "x");

        Assert.True(CanWrite(Context("xdg-documents;"), docs, home: home));
        Assert.False(CanWrite(Context("xdg-documents;"), Path.Combine(home, "Documents", "x"), home: home));
        Assert.True(CanWrite(Context("xdg-data/Sync;"), Path.Combine(home, ".local", "share", "Sync", "x"), home: home));
        Assert.True(CanWrite(Context("xdg-data/Sync;"), "/data/xdg/Sync/x", name => name == "XDG_DATA_HOME" ? "/data/xdg" : null, home));
    }

    // ---- The check as apply uses it ----

    /// <summary>Returns fixed permissions text for any app; never overrides anything.</summary>
    private sealed class FixedFlatpak(string? permissions) : IFlatpak
    {
        public List<string> Asked { get; } = [];

        public string? ShowPermissions(string appId)
        {
            Asked.Add(appId);
            return permissions;
        }

        public string? ShowUserOverrides(string appId) => null;

        public bool OverrideForUser(string appId, IReadOnlyList<string> options) => false;
    }

    [Fact]
    public void A_grant_adds_only_the_savetether_folder_and_is_found_and_taken_back_by_that_path()
    {
        var flatpak = new FakeFlatpak("org.ppsspp.PPSSPP", "host:ro;xdg-run/gamescope-0:ro");
        var check = new FlatpakCheck(flatpak, Home, _ => null);
        const string DataRoot = "/home/deck/Sync/savetether";

        var reach = check.Check(Source, Target, Target, DataRoot, DataRoot)!;
        Assert.Equal([DataRoot], reach.Grants);
        Assert.Null(check.FindGrant(Source, DataRoot, DataRoot));

        Assert.True(check.Grant(reach));
        Assert.Equal(["--filesystem=/home/deck/Sync/savetether"], flatpak.OverrideCalls);
        Assert.True(check.Check(Source, Target, Target, DataRoot, DataRoot)!.CanReach);

        var grant = check.FindGrant(Source, DataRoot, DataRoot)!;
        Assert.Equal("flatpak override --user --nofilesystem=/home/deck/Sync/savetether org.ppsspp.PPSSPP", grant.RemoveCommand);
        Assert.True(check.Revoke(grant));
        Assert.Null(check.FindGrant(Source, DataRoot, DataRoot));
        Assert.False(check.Check(Source, Target, Target, DataRoot, DataRoot)!.CanReach);
    }

    [Fact]
    public void A_broader_grant_such_as_emudecks_host_is_never_reported_as_savetethers()
    {
        var flatpak = new FakeFlatpak("org.ppsspp.PPSSPP", "host:ro");
        flatpak.Overrides.Add("host");

        Assert.Null(new FlatpakCheck(flatpak, Home, _ => null).FindGrant(Source, "/home/deck/Sync/savetether", "/home/deck/Sync/savetether"));
    }

    private const string Source = "/home/deck/.var/app/org.ppsspp.PPSSPP/config/ppsspp/PSP/SAVEDATA";

    [Theory]
    [InlineData(Source, "org.ppsspp.PPSSPP")]
    [InlineData("/home/deck/.var/app/org.ppsspp.PPSSPP", "org.ppsspp.PPSSPP")]
    [InlineData("/home/deck/.var/app", null)]
    [InlineData("/home/deck/.config/Cemu/save", null)]
    [InlineData("/home/other/.var/app/org.ppsspp.PPSSPP/x", null)]
    public void The_app_is_found_from_its_folder(string source, string? expected)
    {
        Assert.Equal(expected, new FlatpakCheck(new FixedFlatpak(null), Home, _ => null).AppOwning(source));
    }

    [Fact]
    public void A_save_folder_outside_any_flatpak_is_not_checked()
    {
        var flatpak = new FixedFlatpak(Ppsspp);

        Assert.Null(new FlatpakCheck(flatpak, Home, _ => null).Check("/home/deck/Emu/saves", Target, Target, "/home/deck/Sync/savetether", "/home/deck/Sync/savetether"));
        Assert.Empty(flatpak.Asked);
    }

    [Fact]
    public void A_read_only_sandbox_is_refused_with_the_command_that_fixes_it()
    {
        var reach = new FlatpakCheck(new FixedFlatpak(Ppsspp), Home, _ => null)
            .Check(Source, Target, Target, "/home/deck/Sync/savetether", "/home/deck/Sync/savetether")!;

        Assert.False(reach.CanReach);
        Assert.Equal([Target], reach.Unreachable);
        Assert.Equal("flatpak override --user --filesystem=/home/deck/Sync/savetether org.ppsspp.PPSSPP", reach.FixCommand);
    }

    [Fact]
    public void A_sync_folder_reached_through_a_link_needs_the_place_it_leads_to()
    {
        // ~/Sync is a link to /data/Sync: home covers the link, not where it leads.
        var resolved = "/data/Sync/savetether/ppsspp/SAVEDATA";
        var reach = new FlatpakCheck(new FixedFlatpak(MelonDs), Home, _ => null)
            .Check(Source, Target, resolved, "/home/deck/Sync/savetether", "/data/Sync/savetether")!;

        Assert.False(reach.CanReach);
        Assert.Equal([resolved], reach.Unreachable);
        Assert.Equal("flatpak override --user --filesystem=/data/Sync/savetether org.ppsspp.PPSSPP", reach.FixCommand);
    }

    [Fact]
    public void Unreadable_permissions_are_reported_as_unknown_not_refused()
    {
        var reach = new FlatpakCheck(new FixedFlatpak(null), Home, _ => null)
            .Check(Source, Target, Target, "/home/deck/Sync/savetether", "/home/deck/Sync/savetether")!;

        Assert.Null(reach.CanReach);
        Assert.Null(reach.FixCommand);
    }

    [Theory]
    [InlineData("/home/deck/Sync/savetether", "/home/deck/Sync/savetether")]
    [InlineData("/run/media/deck/SD Card/Sync", "'/run/media/deck/SD Card/Sync'")]
    [InlineData("/home/deck/it's", "'/home/deck/it'\\''s'")]
    public void Paths_in_the_command_are_quoted_for_the_shell(string path, string expected)
    {
        Assert.Equal(expected, FlatpakCheck.Quote(path));
    }
}
