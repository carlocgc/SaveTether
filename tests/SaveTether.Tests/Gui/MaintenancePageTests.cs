using SaveTether.Core.Backup;
using SaveTether.Core.Operations;
using SaveTether.Gui.Presentation;

namespace SaveTether.Tests.Gui;

/// <summary>Phase 3.6 T8: the Maintenance screen's rows, texts and checks, from synthetic overviews.</summary>
public class MaintenancePageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    internal static MaintenanceOverview Overview(params DeviceOverview[] devices) => new(
        "/sync", devices, ["a.saves", "b.saves", "c.saves"], ["a.saves"], ["a.saves"], ["c.saves"], 2, 2048);

    internal static DeviceOverview Device(string name, bool self = false, bool tombstoned = false, string? id = null) =>
        new(name, id ?? $"id-{name}", Now.AddHours(-3), 1, self, tombstoned);

    [Fact]
    public void Overview_lines_and_rows_show_the_counts_with_remove_disabled_for_this_device()
    {
        var view = MaintenancePage.From(Overview(Device("deck", self: true), Device("pc"), Device("old", tombstoned: true)), null, Now);

        Assert.Contains("Devices registered: 3", view.OverviewLines);
        Assert.Contains("Orphaned mappings: 1", view.OverviewLines);
        Assert.Contains("Mappings this device uses: 1, 1 linked here", view.OverviewLines);
        Assert.StartsWith("Backups: 2, ", view.OverviewLines[^1]);
        Assert.Equal(MaintenancePage.RemoveThisDeviceOnly, view.This.RemoveDisabled);
        Assert.Equal(2, view.Others.Count);
        Assert.All(view.Others, o => Assert.Null(o.RemoveDisabled));
        Assert.Equal("3 hours ago", view.Others[0].Activity);
        Assert.Null(view.Others[0].Note);
        Assert.Equal(MaintenancePage.ReturnedNote, view.Others[1].Note);
        Assert.Equal("c.saves", Assert.Single(view.Orphans).MappingId);
    }

    [Fact]
    public void A_blocker_disables_every_action_and_each_has_a_text()
    {
        Assert.Null(MaintenancePage.Blocker(false, false, true));
        var texts = new[]
        {
            MaintenancePage.Blocker(true, true, false),
            MaintenancePage.Blocker(false, true, false),
            MaintenancePage.Blocker(false, false, false),
        };
        Assert.Equal(new string?[] { MaintenancePage.BlockedByJournal, MaintenancePage.BlockedByLock, MaintenancePage.BlockedBySyncRoot }, texts);

        var view = MaintenancePage.From(Overview(Device("deck", self: true), Device("pc")), texts[1], Now);
        Assert.Equal(texts[1], view.Others[0].RemoveDisabled);
        Assert.Equal(texts[1], view.Orphans[0].Disabled);
    }

    [Fact]
    public void Typed_phrase_ignores_case_and_outer_spaces_but_not_inner_ones()
    {
        Assert.Null(MaintenancePage.TypedPhraseProblem("  Delete Everything ", "delete everything"));
        Assert.NotNull(MaintenancePage.TypedPhraseProblem("delete  everything", "delete everything"));
        Assert.NotNull(MaintenancePage.TypedPhraseProblem("", "deck"));
    }

    [Fact]
    public void Device_name_refuses_invalid_and_taken_names()
    {
        Assert.Null(MaintenancePage.DeviceNameProblem(" deck2 ", ["pc"]));
        Assert.NotNull(MaintenancePage.DeviceNameProblem("Bad Name", []));
        Assert.NotNull(MaintenancePage.DeviceNameProblem("", []));
        Assert.Contains("pc", MaintenancePage.DeviceNameProblem("pc", ["pc"]));
    }

    [Fact]
    public void Backup_cleanup_has_a_text_for_every_mode()
    {
        foreach (var mode in Enum.GetValues<BackupCleanup>())
        {
            Assert.NotEmpty(BackupCleanupTexts.Label(mode));
            Assert.Contains("all apps", BackupCleanupTexts.Describe(mode, BackupCleanupTexts.Scope(null)));
        }

        Assert.Equal("Deleted 1 backup.", BackupCleanupTexts.Result(1));
        Assert.Equal("Nothing to delete.", BackupCleanupTexts.Result(0));
    }

    [Theory]
    [InlineData(OverallCondition.RemovalUnfinished)]
    [InlineData(OverallCondition.RemovedByAnother)]
    public void Removal_conditions_have_banner_texts(OverallCondition condition) =>
        Assert.NotNull(Texts.Banner(new OverallStatus(condition, null, [])));
}
