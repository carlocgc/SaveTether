using SaveTether.Cli;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;

namespace SaveTether.Tests.FileSystem;

/// <summary>
/// A sync root on a network drive: on Windows no junction can point there, so it is refused when the device is
/// set up, by the CLI and by the app, not first at apply. On Linux a symbolic link into a mounted share works.
/// The paths here are decided from their text alone, so no test reaches for a network.
/// </summary>
public class NetworkDriveTests
{
    private const string Share = @"\\nas\games\Sync";

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
    public void Init_refuses_a_network_sync_root_on_windows_before_writing_or_reaching_for_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        var json = pc.Expect(CliApp.ExitRefused, "init", "--sync-root", Share, "--device", "pc", "--create-root", "--json").Json();

        Assert.Equal("invalid", json.GetProperty("problem").GetString());
        Assert.Contains("is on a network drive", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("junctions", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Null(new LocalStore(pc.LocalFolder).TryReadBootstrap());
    }

    [Fact]
    public void Setup_stops_at_its_first_step_on_a_network_folder_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var flow = new SetupFlow(new LocalStore(sandbox.At("local")), sandbox.At("local", "backups"), "PC");

        var check = flow.CheckSyncFolder(Share);

        Assert.Equal(SyncFolderFinding.NetworkDrive, check.Finding);
        Assert.False(check.CanContinue);
        Assert.False(check.OffersFirstDevice);
        Assert.Contains("can only point to a local drive", check.Sentence, StringComparison.Ordinal);
        Assert.Null(flow.SyncFolder);
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
