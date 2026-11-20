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

        // Green, but not "All linked" while nothing is (found by the owner with every link reverted).
        Assert.Equal("1 ready to link", ready.Headline);
        Assert.False(ready.IsAmber);

        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var linked = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.AllLinked, linked.Condition);
        Assert.Equal("All linked", linked.Headline);
        Assert.False(linked.IsAmber);

        // A second folder here, not linked yet: both counted. A mapping only another device has is not counted.
        var states = sandbox.Dir("pc", "states");
        pc.Ok("map", "add", "ppsspp.states", "--target", "ppsspp/STATES", "--source", states);
        var deck = new CliDevice(sandbox, "deck");
        deck.Ok("init", "--sync-root", pc.SyncRoot, "--device", "deck");
        deck.Ok("map", "add", "other.saves", "--target", "other/Saves", "--source", sandbox.Dir("deck", "other"));
        var mixed = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.AllLinked, mixed.Condition);
        Assert.Contains(mixed.Mappings, m => m.Word.Word == "Not on this device");
        Assert.Equal("1 linked, 1 ready to link", mixed.Headline);
        pc.Ok("apply", "ppsspp.states");
        Assert.Equal("All linked", StatusCheck.Run(local).Headline);

        // The link replaced by a real folder with files: the app's folder was restored from a backup, say.
        Directory.Delete(pc.Source);
        CliDevice.Write(pc.Source, "GAME1/save.bin", "local");
        var replaced = StatusCheck.Run(local);
        Assert.Equal(OverallCondition.NeedsAttention, replaced.Condition);
        Assert.Equal("1 app needs attention", replaced.Headline);
        var row = Assert.Single(replaced.Attention);
        Assert.Equal(("PPSSPP", "savedata", "Not linked"), (row.AppName, row.Role, row.Word.Word));
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
        Assert.Equal("it does not exist", unplugged.Detail);
        Assert.Equal($"The sync folder {pc.SyncRoot} is not available: it does not exist. If it is on a drive or a network share, connect it.", Texts.Banner(unplugged));

        // One sentence, with the reason in it: no second line repeating it.
        Assert.Null(Texts.BannerDetail(unplugged));

        // A sync folder that cannot be read (on the Deck, a FUSE mount whose process had died) is row 4 too, not a failed check.
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(pc.SyncRoot, Path.GetFileName(pc.SyncRoot));
            var unreadable = StatusCheck.Run(local);
            Assert.Equal(OverallCondition.SyncRootUnavailable, unreadable.Condition);
            Assert.Contains("is not available: it cannot be read (", Texts.Banner(unreadable), StringComparison.Ordinal);
            File.Delete(pc.SyncRoot);
        }

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
