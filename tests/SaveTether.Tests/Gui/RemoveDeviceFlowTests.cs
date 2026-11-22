using SaveTether.Core.Operations;
using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>Phase 3.6 T8: Remove this device's choices, plan lines and result texts.</summary>
public class RemoveDeviceFlowTests
{
    [Fact]
    public void Delete_everything_is_disabled_with_two_devices_and_offered_with_one()
    {
        var two = RemoveDeviceFlow.DeleteEverythingOption(MaintenancePageTests.Overview(
            MaintenancePageTests.Device("deck", self: true), MaintenancePageTests.Device("pc")));
        Assert.False(two.Offered);
        Assert.Equal("1 other device uses this sync folder; remove it first.", two.Reason);

        var one = RemoveDeviceFlow.DeleteEverythingOption(MaintenancePageTests.Overview(MaintenancePageTests.Device("deck", self: true)));
        Assert.True(one.Offered);

        // A leftover file with this install's ID (an unfinished rename) counts as this device.
        var leftover = RemoveDeviceFlow.DeleteEverythingOption(MaintenancePageTests.Overview(
            MaintenancePageTests.Device("deck", self: true, id: "x"), MaintenancePageTests.Device("old", id: "x")));
        Assert.True(leftover.Offered);
    }

    [Fact]
    public void Choices_default_to_keep_and_delete_everything_replaces_the_orphan_choices()
    {
        var state = new RemoveChoicesState();
        Assert.False(state.ToChoices().DeleteBackups);
        Assert.Empty(state.ToChoices().Orphans!);

        state.Orphans["c.saves"] = OrphanChoice.DeleteWithSaves;
        Assert.Equal(["deck", "delete everything"], RemoveDeviceFlow.PhrasesToType("deck", state.ToChoices()));

        state.Orphans["c.saves"] = OrphanChoice.DeleteMapping;
        Assert.Equal(["deck"], RemoveDeviceFlow.PhrasesToType("deck", state.ToChoices()));

        state.DeleteEverything = true;
        var all = state.ToChoices();
        Assert.True(all.DeleteEverything);
        Assert.Null(all.Orphans);
        Assert.Equal(["deck", "delete everything"], RemoveDeviceFlow.PhrasesToType("deck", all));
    }

    [Fact]
    public void Plan_lines_list_what_is_given_back_deleted_and_kept()
    {
        var plan = new DeviceRemovalPlan(
            "deck", [], [], [], [new OrphanRemoval("c.saves", OrphanChoice.DeleteMapping), new OrphanRemoval("d.saves", OrphanChoice.Keep)],
            ["devices/deck.json"], [], false, []);

        var lines = RemoveDeviceFlow.PlanLines(plan);

        Assert.Contains("Delete mapping: c.saves", lines);
        Assert.DoesNotContain(lines, l => l.Contains("d.saves"));
        Assert.Contains("Deleted from the sync folder: devices/deck.json", lines);
        Assert.Contains("Backups: kept.", lines);
        Assert.Equal(RemoveDeviceFlow.Stays, lines[^1]);
    }

    [Fact]
    public void Result_text_lists_the_folders_and_says_the_program_stays()
    {
        var (text, details) = RemoveDeviceFlow.Result(new DeviceRemovalResult(["/saves/a", "/saves/b"], [], 3, null));

        Assert.Contains("2 folders were given back", text);
        Assert.Contains("3 backups deleted", text);
        Assert.Contains("Settings → Uninstall", text);
        Assert.Equal(["/saves/a", "/saves/b"], details);
    }

    [Fact]
    public void Every_orphan_choice_has_a_label()
    {
        foreach (var choice in Enum.GetValues<OrphanChoice>())
        {
            Assert.NotEmpty(RemoveDeviceFlow.OrphanLabel(choice));
        }
    }
}
