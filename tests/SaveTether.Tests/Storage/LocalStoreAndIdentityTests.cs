using SaveTether.Core.Diagnostics;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;
using static SaveTether.Tests.Storage.SharedStoreTests;

namespace SaveTether.Tests.Storage;

public class LocalStoreAndIdentityTests
{
    [Fact]
    public void Bootstrap_is_absent_until_written_then_round_trips()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.At("local", "savetether"));
        Assert.Null(local.TryReadBootstrap());

        var bootstrap = new BootstrapFile
        {
            Schema = BootstrapFile.CurrentSchema, SyncRoot = sandbox.At("sync"), DeviceName = "deck", InstallId = "abc123",
        };
        local.WriteBootstrap(bootstrap);

        Assert.Equal(bootstrap, local.TryReadBootstrap());
        AssertProblem(StoreProblem.Invalid, () => local.WriteBootstrap(bootstrap with { DeviceName = "Not Valid" }));
        AssertProblem(StoreProblem.Invalid, () => local.WriteBootstrap(bootstrap with { SyncRoot = "relative" }));
        AssertProblem(StoreProblem.Invalid, () => new LocalStore("relative"));
    }

    [Fact]
    public void State_is_empty_when_missing_and_round_trips()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.At("local"));
        Assert.Empty(local.ReadState().Mappings);

        var applied = new AppliedMapping
        {
            Source = sandbox.At("app", "saves"),
            Target = "ppsspp/SAVEDATA",
            LinkType = LinkType.Junction,
            SetupChoice = SetupChoice.ReplaceShared,
            AppliedUtc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
            BackupIds = ["ppsspp.savedata/20260304T050607Z"],
        };
        local.WriteState(new StateFile
        {
            Schema = StateFile.CurrentSchema,
            Mappings = new Dictionary<string, AppliedMapping> { ["ppsspp.savedata"] = applied },
        });

        var read = local.ReadState().Mappings["ppsspp.savedata"];
        Assert.Equal(applied.Source, read.Source);
        Assert.Equal(LinkType.Junction, read.LinkType);
        Assert.Equal(SetupChoice.ReplaceShared, read.SetupChoice);
        Assert.Equal(applied.AppliedUtc, read.AppliedUtc);
        Assert.Equal(applied.BackupIds, read.BackupIds);

        var json = File.ReadAllText(Path.Combine(local.Folder, "state.json"));
        Assert.Contains("\"setup_choice\": \"replace-shared\"", json, StringComparison.Ordinal);
        Assert.Contains("\"link_type\": \"junction\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_or_broken_local_file_is_refused_not_overwritten()
    {
        using var sandbox = new Sandbox();
        var local = new LocalStore(sandbox.Dir("local"));
        var state = Path.Combine(local.Folder, "state.json");

        File.WriteAllText(state, "{ \"schema\": 5, \"mappings\": {} }");
        AssertProblem(StoreProblem.SchemaTooNew, () => local.ReadState());
        AssertProblem(StoreProblem.SchemaTooNew, () => local.WriteState(StateFile.Empty));
        Assert.Equal("{ \"schema\": 5, \"mappings\": {} }", File.ReadAllText(state));

        // A file that is merely broken can be replaced: state is rebuildable from disk.
        File.WriteAllText(state, "{ truncated");
        AssertProblem(StoreProblem.Invalid, () => local.ReadState());
        local.WriteState(StateFile.Empty);
        Assert.Empty(local.ReadState().Mappings);
    }

    [Fact]
    public void Claiming_a_new_name_creates_a_device_file_with_a_fresh_install_id()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);

        var deck = DeviceIdentity.Claim(store, "deck", "/home/deck/backups", takeOver: false);
        var desktop = DeviceIdentity.Claim(store, "desktop", @"C:\backups", takeOver: false);

        Assert.Equal(16, deck.InstallId.Length);
        Assert.NotEqual(deck.InstallId, desktop.InstallId);
        Assert.Equal("/home/deck/backups", store.ReadDevice("deck").BackupRoot);
        Assert.Empty(store.ReadDevice("deck").Paths);
        AssertProblem(StoreProblem.Invalid, () => DeviceIdentity.Claim(store, "My Deck", "/b", takeOver: false));
    }

    [Fact]
    public void An_existing_name_needs_an_explicit_takeover_which_keeps_its_paths()
    {
        using var sandbox = new Sandbox();
        var store = NewStore(sandbox);
        var original = DeviceIdentity.Claim(store, "deck", "/home/deck/backups", takeOver: false);
        store.WriteDevice(original with
        {
            Paths = new Dictionary<string, DevicePath> { ["ppsspp.savedata"] = new() { Source = "/saves", ProcessNames = ["PPSSPPSDL"] } },
        });
        var oldBootstrap = Bootstrap(sandbox, original);

        var refused = AssertProblem(StoreProblem.NameInUse, () => DeviceIdentity.Claim(store, "deck", "/elsewhere", takeOver: false));
        Assert.Contains("take the name over", refused.Message, StringComparison.Ordinal);
        Assert.Equal(original.InstallId, store.ReadDevice("deck").InstallId);

        // After a wipe: same name, new install. Paths and backup location come back.
        var restored = DeviceIdentity.Claim(store, "deck", "/elsewhere", takeOver: true);

        Assert.NotEqual(original.InstallId, restored.InstallId);
        Assert.Equal("/home/deck/backups", restored.BackupRoot);
        Assert.Equal("/saves", store.ReadDevice("deck").Paths["ppsspp.savedata"].Source);
        Assert.Equal(["PPSSPPSDL"], store.ReadDevice("deck").Paths["ppsspp.savedata"].ProcessNames);

        // The new install matches the file; the old one can tell it has been replaced.
        Assert.True(DeviceIdentity.Matches(Bootstrap(sandbox, restored), store.ReadDevice("deck")));
        Assert.False(DeviceIdentity.Matches(oldBootstrap, store.ReadDevice("deck")));
        Assert.False(DeviceIdentity.Matches(Bootstrap(sandbox, restored) with { DeviceName = "desktop" }, store.ReadDevice("deck")));
    }

    [Fact]
    public void WriteFileAtomically_replaces_content_and_leaves_no_temporary_file()
    {
        using var sandbox = new Sandbox();
        var folder = sandbox.Dir("files");
        var file = Path.Combine(folder, "settings.json");

        SafeFs.WriteFileAtomically(file, "first");
        SafeFs.WriteFileAtomically(file, "second");

        Assert.Equal("second", File.ReadAllText(file));
        Assert.Equal([file], Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void WriteFileAtomically_refuses_to_replace_a_folder_or_a_link()
    {
        using var sandbox = new Sandbox();
        var folder = sandbox.Dir("a-folder");
        var link = sandbox.At("a-link");
        Sandbox.Link(link, sandbox.Canary);

        Assert.Throws<SafeFsException>(() => SafeFs.WriteFileAtomically(folder, "x"));
        Assert.Throws<SafeFsException>(() => SafeFs.WriteFileAtomically(link, "x"));
        Assert.Throws<SafeFsException>(() => SafeFs.WriteFileAtomically("relative.json", "x"));
        Assert.ThrowsAny<IOException>(() => SafeFs.WriteFileAtomically(sandbox.At("no-such-folder", "x.json"), "x"));

        Assert.Equal(EntryKind.Directory, SafeFs.Probe(folder));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_self_test_passes_and_cleans_up_after_itself()
    {
        var before = SelfTestFolders();
        using var output = new StringWriter();

        var passed = SelfTest.Run(output);

        Assert.True(passed, output.ToString());
        Assert.Contains("ok: mapping file", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("ok: directory link", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("self-test passed", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(SelfTestFolders().Except(before));
    }

    private static string[] SelfTestFolders() => Directory.GetDirectories(Path.GetTempPath(), "savetether-selftest-*");

    private static BootstrapFile Bootstrap(Sandbox sandbox, DeviceFile device) => new()
    {
        Schema = BootstrapFile.CurrentSchema, SyncRoot = sandbox.At("sync"), DeviceName = device.DeviceName, InstallId = device.InstallId,
    };
}
