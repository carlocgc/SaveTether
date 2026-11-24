using SaveTether.Cli;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;

namespace SaveTether.Tests.FileSystem;

/// <summary>
/// A sync root on a network share (design 5.16). On Windows a save folder is linked there with a symlink, which
/// needs Developer Mode or elevation, so init checks it can make one first. On Linux a symbolic link into a
/// mounted share works. The paths here are decided from their text alone, or are this PC's admin share
/// <c>\\localhost\C$</c>, so no test reaches for another machine.
/// </summary>
public class NetworkDriveTests
{
    // .invalid never resolves (RFC 2606), so a share there does not answer.
    private const string Share = @"\\savetether-no-such-host.invalid\games\Sync";

    [Fact]
    public void Network_paths_are_told_from_local_ones_on_windows_and_never_on_linux()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(PathRules.IsOnNetworkDrive("/mnt/nas/Sync"));
            Assert.False(PathRules.IsOnNetworkDrive("/home/deck/Sync"));
            return;
        }

        Assert.True(PathRules.IsOnNetworkDrive(Share));
        Assert.True(PathRules.IsOnNetworkDrive(@"\\?\UNC\nas\games\Sync"));

        // The long forms of a local path are local: the previous check took any \\ for a share.
        Assert.False(PathRules.IsOnNetworkDrive(@"\\?\C:\Sync"));
        Assert.False(PathRules.IsOnNetworkDrive(@"\\.\C:\Sync"));
        Assert.False(PathRules.IsOnNetworkDrive(Path.GetTempPath()));
    }

    [Fact]
    public void Init_refuses_a_share_that_does_not_answer_before_writing_anything()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        var json = pc.Expect(CliApp.ExitRefused, "init", "--sync-root", Share, "--device", "pc", "--create-root", "--json").Json();

        Assert.Equal("not-found", json.GetProperty("problem").GetString());
        Assert.Contains("does not answer", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Null(new LocalStore(pc.LocalFolder).TryReadBootstrap());
    }

    [Fact]
    public void A_device_on_a_share_links_with_a_symlink_and_reverts()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        if (LocalAdminShare(pc.SyncRoot) is not { } share)
        {
            return; // Not Windows, no admin share, or neither Developer Mode nor elevation.
        }

        var init = pc.Ok("init", "--sync-root", share, "--device", "pc", "--create-root", "--json");
        Assert.Contains(
            init.Json().GetProperty("init").GetProperty("notes").EnumerateArray(),
            note => note.GetString() == Workspace.NetworkShareWarning);
        Assert.Equal(share, new LocalStore(pc.LocalFolder).TryReadBootstrap()!.SyncRoot, ignoreCase: true);
        pc.Ok("map", "add", CliDevice.MappingId, "--target", "ppsspp/SAVEDATA", "--source", pc.Source);
        pc.Save("GAME1/save.bin", "one");

        pc.Ok("apply", CliDevice.MappingId, "--copy");

        Assert.Equal(EntryKind.Symlink, SafeFs.Probe(pc.Source));
        Assert.Equal(LinkType.Symlink, new LocalStore(pc.LocalFolder).ReadState().Mappings[CliDevice.MappingId].LinkType);
        Assert.StartsWith(@"\\localhost\", SafeFs.ReadLinkTarget(pc.Source), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("one", File.ReadAllText(Path.Combine(pc.Target, "GAME1", "save.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(pc.LocalFolder, ".link-probe-*"));

        pc.Ok("revert", CliDevice.MappingId);

        Assert.Equal(EntryKind.Directory, SafeFs.Probe(pc.Source));
        Assert.Equal("one", File.ReadAllText(Path.Combine(pc.Source, "GAME1", "save.bin")));
    }

    /// <summary>
    /// <paramref name="localFolder"/> as a path on this PC's admin share, when a symlink can be made to it;
    /// otherwise null. CI runners are elevated, so there it must be a real share link: a silent skip there would
    /// leave share links untested.
    /// </summary>
    internal static string? LocalAdminShare(string localFolder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var full = Path.GetFullPath(localFolder);
        var unc = $@"\\localhost\{full[0]}${full[2..]}";
        var usable = Directory.Exists(unc) && SymlinkCapability.Probe(Path.GetTempPath(), unc) == ShareLinkSupport.Available;
        Assert.True(usable || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is null, $"On CI, '{unc}' must be reachable and a symlink to it allowed.");
        return usable ? unc : null;
    }

    [Fact]
    public void Setup_step_1_says_a_share_that_does_not_answer_is_missing_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var flow = new SetupFlow(new LocalStore(sandbox.At("local")), sandbox.At("local", "backups"), "PC");

        var check = flow.CheckSyncFolder(Share);

        Assert.Equal(SyncFolderFinding.Missing, check.Finding);
        Assert.False(check.CanContinue);
        Assert.Contains("does not answer", check.Sentence, StringComparison.Ordinal);
        Assert.Null(flow.SyncFolder);
    }

    [Fact]
    public void Setup_step_1_goes_on_with_a_share_it_can_link_to_and_warns_that_it_is_not_recommended()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.At("sync"));
        if (LocalAdminShare(sandbox.At("sync")) is not { } share)
        {
            return; // Not Windows, no admin share, or neither Developer Mode nor elevation.
        }

        var flow = new SetupFlow(new LocalStore(sandbox.At("local")), sandbox.At("local", "backups"), "PC");

        var check = flow.CheckSyncFolder(share);

        Assert.Equal(SyncFolderFinding.NotSetUp, check.Finding);
        Assert.True(check.OffersFirstDevice);
        Assert.Equal(Workspace.NetworkShareWarning, check.Warning);
        Assert.Null(flow.CheckSyncFolder(sandbox.At("sync")).Warning);
    }

    [Fact]
    public void Map_add_refuses_a_network_folder_to_link_on_windows_before_writing_anything()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");

        var json = pc.Expect(
            CliApp.ExitRefused, "map", "add", CliDevice.MappingId, "--target", "ppsspp/SAVEDATA", "--source", @"\\nas\games\SAVEDATA", "--json").Json();

        Assert.Equal("invalid", json.GetProperty("problem").GetString());
        Assert.Contains("is on a network drive", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(pc.SyncRoot, "savetether", ".savetether", "mappings", CliDevice.MappingId + ".json")));
    }

    [Fact]
    public void The_hand_form_refuses_a_network_folder_on_windows_at_its_field()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");
        var flow = new ManualAddFlow(new LocalStore(pc.LocalFolder));
        flow.StartNew();

        var input = new ManualAddInput(@"\\nas\games\Saves", "Some Game", "Saves", null, 1);
        var check = flow.Check(input);

        Assert.False(check.CanAdd);
        Assert.Contains(check.Problems, p => p.Field == ManualField.SaveFolder && p.Problem.Contains("network drive", StringComparison.Ordinal));
        Assert.False(flow.Add(input, joinAccepted: false).Succeeded);
        Assert.False(File.Exists(Path.Combine(pc.SyncRoot, "savetether", ".savetether", "mappings", "some-game.saves.json")));
    }
}
