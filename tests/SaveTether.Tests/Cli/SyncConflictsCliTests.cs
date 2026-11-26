using SaveTether.Cli;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"savetether sync-conflicts": the app's "Detect sync conflicts" switch, and what status shows with it on.</summary>
public class SyncConflictsCliTests
{
    private static bool On(CliRun run) => run.Json().GetProperty("sync_conflicts").GetProperty("on").GetBoolean();

    [Fact]
    public void Off_by_default_and_set_on_and_off_without_a_set_up_device()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        Assert.False(On(pc.Ok("sync-conflicts", "--json")));
        Assert.False(Directory.Exists(pc.LocalFolder));

        Assert.True(On(pc.Ok("sync-conflicts", "on", "--json")));
        Assert.True(new DeviceSettings(pc.LocalFolder).DetectSyncConflicts);
        Assert.Contains("Detect sync conflicts: on", pc.Ok("sync-conflicts").Out, StringComparison.Ordinal);

        Assert.False(On(pc.Ok("sync-conflicts", "off", "--json")));
        pc.Expect(CliApp.ExitUsage, "sync-conflicts", "maybe", "--json");
        Assert.False(new DeviceSettings(pc.LocalFolder).DetectSyncConflicts);
    }

    [Fact]
    public void Status_shows_a_file_conflict_and_lists_the_copies_only_with_the_switch_on()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", CliDevice.MappingId, "--copy");
        CliDevice.Write(pc.Target, "GAME1/save.sync-conflict-20261126-101500-ABC.bin", "two");

        Assert.Equal("ok", pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0].GetProperty("state").GetString());

        pc.Ok("sync-conflicts", "on");
        var mapping = pc.Ok("status", "--json").Json().GetProperty("status").GetProperty("mappings")[0];
        Assert.Equal("file-conflict", mapping.GetProperty("state").GetString());
        Assert.Equal("GAME1/save.sync-conflict-20261126-101500-ABC.bin", mapping.GetProperty("conflicts")[0].GetString());

        var text = pc.Ok("status").Out;
        Assert.Contains("File conflict", text, StringComparison.Ordinal);
        Assert.Contains("GAME1/save.sync-conflict-20261126-101500-ABC.bin\n", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }
}
