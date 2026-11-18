using SaveTether.Cli;
using SaveTether.Core.Execution;
using SaveTether.Core.Health;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.Execution;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 3's table of the desktop app spec, against devices set up through the CLI.</summary>
public class StatusCheckTests
{
    private const string Id = CliDevice.MappingId;

    [Fact]
    public void Every_core_state_and_reason_has_a_word_and_only_the_spec_amber_ones_are_amber()
    {
        HealthReason?[] reasons = [null, .. Enum.GetValues<HealthReason>()];
        foreach (var state in Enum.GetValues<HealthState>())
        {
            foreach (var reason in reasons)
            {
                var word = StateWords.For(state, reason);
                Assert.False(string.IsNullOrWhiteSpace(word.Word));
            }
        }

        Assert.Equal(new StateWord("Linked", Shade.Green), StateWords.For(HealthState.Ok, null));
        Assert.Equal(new StateWord("Ready to link", Shade.Grey), StateWords.For(HealthState.NotApplied, HealthReason.NotLinkedYet));
        Assert.Equal(new StateWord("Not on this device", Shade.Grey), StateWords.For(HealthState.NotApplied, HealthReason.NoSaveFolder));
        Assert.Equal(new StateWord("Folder setting is wrong", Shade.Amber), StateWords.For(HealthState.NotApplied, HealthReason.SaveFolderNotAbsolute));
        Assert.Equal(new StateWord("Not linked", Shade.Amber), StateWords.For(HealthState.Replaced, HealthReason.FolderHoldsFiles));
        Assert.Equal(new StateWord("Link missing", Shade.Amber), StateWords.For(HealthState.Replaced, HealthReason.FolderEmpty));
        Assert.Equal(new StateWord("Folder gone", Shade.Amber), StateWords.For(HealthState.Replaced, HealthReason.FolderMissing));
        Assert.Equal(new StateWord("Not a folder", Shade.Amber), StateWords.For(HealthState.Replaced, HealthReason.NotAFolder));
        Assert.Equal(new StateWord("Shared folder missing", Shade.Amber), StateWords.For(HealthState.Broken, HealthReason.SharedFolderMissing));
        Assert.Equal(new StateWord("Shared folder missing", Shade.Amber), StateWords.For(HealthState.Broken, HealthReason.SyncRootUnavailable));
        Assert.Equal(new StateWord("Linked elsewhere", Shade.Amber), StateWords.For(HealthState.Retargeted, null));
        Assert.Equal(new StateWord("Two shared copies", Shade.Amber), StateWords.For(HealthState.DoubleSeed, null));
        Assert.Equal(new StateWord("Not SaveTether's link", Shade.Amber), StateWords.For(HealthState.ForeignLink, null));
    }

    [Fact]
    public void A_device_not_set_up_is_row_1()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");

        var status = StatusCheck.Run(new LocalStore(pc.LocalFolder));
        Assert.Equal(OverallCondition.NotSetUp, status.Condition);
        Assert.True(status.IsAmber);
        Assert.Equal("Not set up", status.Headline);
    }

    [Fact]
    public void Linked_and_not_applied_mappings_are_green_and_a_replaced_link_is_amber()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        var local = new LocalStore(pc.LocalFolder);

        // Not applied stays green (5.14).
        var ready = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.AllLinked, ready.Condition);
        Assert.Equal("Ready to link", Assert.Single(ready.Mappings).Word.Word);

        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var linked = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.AllLinked, linked.Condition);
        Assert.Equal("All linked", linked.Headline);
        Assert.False(linked.IsAmber);

        // The link replaced by a real folder with files: the app's folder was restored from a backup, say.
        Directory.Delete(pc.Source);
        CliDevice.Write(pc.Source, "GAME1/save.bin", "local");
        var replaced = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.NeedsAttention, replaced.Condition);
        Assert.Equal("1 app needs attention", replaced.Headline);
        var row = Assert.Single(replaced.Attention);
        Assert.Equal(("ppsspp", "savedata", "Not linked"), (row.AppName, row.Role, row.Word.Word));
    }

    [Fact]
    public void The_device_wide_conditions_come_in_the_order_of_the_table()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        var local = new LocalStore(pc.LocalFolder);

        // Row 3: an unfinished operation.
        pc.Faults = new FailAt(StepKind.WriteState, FaultPoint.BeforeStep);
        pc.Expect(CliApp.ExitRecoveryNeeded, "apply", Id, "--copy");
        pc.Faults = null;
        Assert.Equal(OverallCondition.Unfinished, StatusCheck.Run(local).Condition);

        // Row 3 wins over row 4: with the sync folder gone too, the unfinished operation is still first.
        var aside = sandbox.At("pc", "sync-unplugged");
        Directory.Move(pc.SyncRoot, aside);
        Assert.Equal(OverallCondition.Unfinished, StatusCheck.Run(local).Condition);

        Directory.Move(aside, pc.SyncRoot);
        pc.Ok("recover");
        Assert.Equal(OverallCondition.AllLinked, StatusCheck.Run(local).Condition);

        // Row 4: the sync folder is not there. The linked mapping is amber too, but the condition covers it.
        Directory.Move(pc.SyncRoot, aside);
        var unplugged = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.SyncRootUnavailable, unplugged.Condition);
        Assert.NotNull(unplugged.Detail);
        Assert.Contains(pc.SyncRoot, Texts.Banner(unplugged), StringComparison.Ordinal);
        Directory.Move(aside, pc.SyncRoot);

        // Row 5: another install took the device name over.
        var reinstall = new CliDevice(sandbox, "reinstall");
        reinstall.Ok("init", "--sync-root", pc.SyncRoot, "--device", "pc", "--take-over");
        Assert.Equal(OverallCondition.DeviceNameInUse, StatusCheck.Run(local).Condition);
    }

    [Fact]
    public void A_file_from_a_newer_version_and_a_missing_device_file_are_their_own_conditions()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        var local = new LocalStore(pc.LocalFolder);

        var deviceFile = Path.Combine(pc.SyncRoot, "savetether", ".savetether", "devices", "pc.json");
        File.Delete(deviceFile);
        var cannot = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.CannotCheck, cannot.Condition);
        Assert.NotNull(cannot.Detail);

        var bootstrap = Path.Combine(pc.LocalFolder, "bootstrap.json");
        File.WriteAllText(bootstrap, File.ReadAllText(bootstrap).Replace("\"schema\": 1", "\"schema\": 99", StringComparison.Ordinal));
        Assert.Equal(OverallCondition.UpdateNeeded, StatusCheck.Run(local).Condition);
    }
}
