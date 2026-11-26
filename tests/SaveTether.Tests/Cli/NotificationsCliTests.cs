using SaveTether.Cli;
using SaveTether.Core.Startup;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>"savetether notifications": the app's switch, from the command line.</summary>
public class NotificationsCliTests
{
    private static bool On(CliRun run) => run.Json().GetProperty("notifications").GetProperty("on").GetBoolean();

    [Fact]
    public void On_by_default_and_set_off_and_on_without_a_set_up_device()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        Assert.True(On(pc.Ok("notifications", "--json")));
        Assert.False(Directory.Exists(pc.LocalFolder));

        Assert.False(On(pc.Ok("notifications", "off", "--json")));
        Assert.False(On(pc.Ok("notifications", "--json")));
        Assert.Contains("\"notifications\": false", File.ReadAllText(Path.Combine(pc.LocalFolder, DeviceSettings.FileName)), StringComparison.Ordinal);

        Assert.True(On(pc.Ok("notifications", "on", "--json")));
        Assert.Contains("Desktop notifications: on", pc.Ok("notifications").Out, StringComparison.Ordinal);
    }

    [Fact]
    public void It_works_while_an_operation_holds_the_lock()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        using var held = OperationLock.Acquire(new LocalStore(pc.LocalFolder), "gui", "apply");
        Assert.False(On(pc.Ok("notifications", "off", "--json")));
    }

    [Fact]
    public void An_unknown_value_is_a_usage_error_and_changes_nothing()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        pc.Expect(CliApp.ExitUsage, "notifications", "mute", "--json");
        Assert.False(Directory.Exists(pc.LocalFolder));
    }

    [Fact]
    public void A_setting_that_cannot_be_saved_is_refused_with_why()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        // A file where the local folder should be.
        Directory.CreateDirectory(Path.GetDirectoryName(pc.LocalFolder)!);
        File.WriteAllText(pc.LocalFolder, "in the way");

        var refused = pc.Expect(CliApp.ExitRefused, "notifications", "off", "--json").Json();
        Assert.Contains("could not be saved", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
    }
}
