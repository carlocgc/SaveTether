using SaveTether.Cli;
using SaveTether.Core.Backup;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"backup auto" (unsafe mode) and "backup keep" (max backups per folder), and what they change in the actions.</summary>
public class BackupSettingsTests
{
    private const string Id = CliDevice.MappingId;

    private static Workspace Open(CliDevice device) => Workspace.Open(new LocalStore(device.LocalFolder), new NoProcesses());

    private static (bool BeforeActions, int Max) Read(CliRun run)
    {
        var settings = run.Json().GetProperty("backup_settings");
        return (settings.GetProperty("before_actions").GetBoolean(), settings.GetProperty("max_per_folder").GetInt32());
    }

    [Fact]
    public void Both_show_their_defaults_and_are_set_without_a_set_up_device()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        Assert.Equal((true, BackupStore.DefaultKeep), Read(pc.Ok("backup", "auto", "--json")));
        Assert.False(Directory.Exists(pc.LocalFolder));

        Assert.Equal((false, BackupStore.DefaultKeep), Read(pc.Ok("backup", "auto", "off", "--confirm-unsafe", "--json")));
        Assert.Equal((false, 0), Read(pc.Ok("backup", "keep", "0", "--json")));
        Assert.Equal((true, 0), Read(pc.Ok("backup", "auto", "on", "--json")));

        var text = pc.Ok("backup", "keep").Out;
        Assert.Contains("Back up before actions: on", text, StringComparison.Ordinal);
        Assert.Contains("Max backups per folder: unlimited", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto", "off")]
    [InlineData("auto", "maybe")]
    [InlineData("keep", "21")]
    [InlineData("keep", "-1")]
    [InlineData("keep", "five")]
    public void Unsafe_mode_without_its_confirmation_and_a_bad_value_are_usage_errors_and_change_nothing(string command, string value)
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        pc.Expect(CliApp.ExitUsage, "backup", command, value, "--json");
        Assert.False(Directory.Exists(pc.LocalFolder));
    }

    [Fact]
    public void In_unsafe_mode_replacing_the_shared_copy_takes_no_backup_and_says_so()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "mine");
        CliDevice.Write(pc.Target, "GAME1/save.bin", "theirs");
        pc.Ok("backup", "auto", "off", "--confirm-unsafe");

        var dry = pc.Ok("apply", Id, "--replace-shared", "--confirm-replace", "--dry-run").Out;
        Assert.Contains(Planner.UnsafeModeNote, dry, StringComparison.Ordinal);
        Assert.DoesNotContain("Back up", dry, StringComparison.Ordinal);

        pc.Ok("apply", Id, "--replace-shared", "--confirm-replace");

        Assert.True(pc.IsLinked);
        Assert.Equal("mine", pc.SharedFiles()["GAME1/save.bin"]);
        Assert.Empty(Open(pc).ListBackups(Id));
    }

    [Fact]
    public void In_unsafe_mode_deleting_a_mapping_takes_no_backup_and_still_deletes_its_shared_saves()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        pc.Ok("revert", Id);
        pc.Ok("map", "remove", Id);
        pc.Ok("backup", "auto", "off", "--confirm-unsafe");
        var before = Open(pc).ListBackups(Id).Count;

        var done = Open(pc).DeleteMapping(Id);

        Assert.Null(done.Backup);
        Assert.False(Directory.Exists(pc.Target));
        Assert.Equal(before, Open(pc).ListBackups(Id).Count);
    }

    [Fact]
    public void In_unsafe_mode_a_link_left_in_the_shared_folder_is_named_as_such_not_as_changed_after_a_backup()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        pc.Ok("revert", Id);
        pc.Ok("map", "remove", Id);
        Sandbox.Link(Path.Combine(pc.Target, "elsewhere"), sandbox.Canary);
        pc.Ok("backup", "auto", "off", "--confirm-unsafe");

        var incomplete = Assert.Throws<MappingDeletionIncompleteException>(() => Open(pc).DeleteMapping(Id));

        Assert.Contains("1 entry in its shared folder that is not a plain file", incomplete.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("backup", incomplete.Message, StringComparison.Ordinal);
        Assert.Null(incomplete.Backup);
        Assert.False(File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Back_up_now_keeps_the_first_backup_and_the_newest_max_and_0_keeps_all()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var workspace = Open(pc);
        var first = Assert.Single(workspace.ListBackups(Id));

        pc.Ok("backup", "keep", "1");
        workspace.BackUpNow(Id);
        workspace.BackUpNow(Id);
        var newest = workspace.BackUpNow(Id);
        Assert.Equal([first.Id, newest.Id], workspace.ListBackups(Id).Select(b => b.Id));

        pc.Ok("backup", "keep", "0");
        workspace.BackUpNow(Id);
        workspace.BackUpNow(Id);
        Assert.Equal(4, workspace.ListBackups(Id).Count);
    }
}
