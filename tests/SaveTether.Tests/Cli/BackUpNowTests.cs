using SaveTether.Core.Backup;
using SaveTether.Core.Operations;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"Back up now": a backup taken by hand, of the shared copy a linked folder holds.</summary>
public class BackUpNowTests
{
    private const string Id = CliDevice.MappingId;

    [Fact]
    public void It_backs_up_the_shared_copy_of_a_linked_folder()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var workspace = Workspace.Open(new LocalStore(pc.LocalFolder), NoProcesses.Instance);

        var backup = workspace.BackUpNow(Id);
        Assert.Equal(BackupReason.Snapshot, backup.Reason);
        Assert.Equal(1, backup.FileCount);
        Assert.Equal(Path.GetFullPath(pc.Target), Path.GetFullPath(backup.SourcePath));
        Assert.True(Directory.Exists(backup.Location));
        Assert.Contains(backup.Id, workspace.ListBackups(Id).Select(b => b.Id));
    }
}
