using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 5 of the desktop app spec: the Apps page, its rows and their actions, and Apply all (9.4).</summary>
public class AppsPageTests
{
    private const string Id = CliDevice.MappingId;

    [Fact]
    public void Each_state_has_the_spec_sentence_main_action_and_others()
    {
        AppRow For(HealthState state, HealthReason? reason = null) => AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", state, reason));

        var ok = For(HealthState.Ok);
        Assert.Equal("Saves here are the shared copy.", ok.Sentence);
        Assert.Null(ok.Main);
        Assert.Equal([RowAction.OpenFolder, RowAction.OpenSharedFolder, RowAction.Backups, RowAction.Revert], ok.Others);

        // Backups, where section 5.1 lists it: Linked, Not linked, Two shared copies.
        Assert.Contains(RowAction.Backups, For(HealthState.Replaced, HealthReason.FolderHoldsFiles).Others);
        Assert.Contains(RowAction.Backups, For(HealthState.DoubleSeed).Others);

        var ready = For(HealthState.NotApplied, HealthReason.NotLinkedYet);
        Assert.Equal((RowAction?)RowAction.Apply, ready.Main);
        Assert.Contains(RowAction.StopUsingHere, ready.Others);

        // Stop using here is offered only when Ready to link: the core refuses it while applied.
        Assert.All(
            Enum.GetValues<HealthState>().Where(s => s != HealthState.NotApplied),
            s => Assert.DoesNotContain(RowAction.StopUsingHere, For(s).Others));

        Assert.Equal((RowAction?)RowAction.Apply, For(HealthState.Replaced, HealthReason.FolderHoldsFiles).Main);
        Assert.Equal((RowAction?)RowAction.Apply, For(HealthState.Replaced, HealthReason.FolderEmpty).Main);
        Assert.Equal([RowAction.Apply], For(HealthState.Replaced, HealthReason.FolderMissing).Others);
        Assert.Equal((RowAction?)RowAction.OpenFolderParent, For(HealthState.Replaced, HealthReason.NotAFolder).Main);
        Assert.Equal((RowAction?)RowAction.CheckAgain, For(HealthState.Broken, HealthReason.SharedFolderMissing).Main);
        Assert.Equal((RowAction?)RowAction.UseThisCopy, For(HealthState.DoubleSeed).Main);
        Assert.Equal((RowAction?)RowAction.OpenFolder, For(HealthState.ForeignLink).Main);

        // A folder added by hand has no definition: its rows open the hand form instead of Add app (section 8).
        Assert.Equal((RowAction?)RowAction.AddHere, For(HealthState.NotApplied, HealthReason.NoSaveFolder).Main);
        Assert.Equal((RowAction?)RowAction.ChooseFolderAgain, For(HealthState.Replaced, HealthReason.FolderMissing).Main);
        var defined = Mapping("ppsspp.savedata", "PPSSPP", HealthState.Replaced, HealthReason.FolderMissing) with { HasDefinition = true };
        Assert.Equal((RowAction?)RowAction.FindAppAgain, AppsPage.Row(defined).Main);
        foreach (var action in Enum.GetValues<RowAction>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AppsPage.Label(action)));
        }
    }

    [Fact]
    public void Apps_needing_attention_come_first_and_those_not_on_this_device_last_and_apart()
    {
        var status = new OverallStatus(OverallCondition.NeedsAttention, null,
        [
            Mapping("cemu.save", "Cemu", HealthState.Ok),
            Mapping("ryujinx.saves", "Ryujinx", HealthState.NotApplied, HealthReason.NoSaveFolder),
            Mapping("eden.saves", "Eden", HealthState.Ok),
            Mapping("eden.nand", "Eden", HealthState.Replaced, HealthReason.FolderEmpty),
            Mapping("ppsspp.savedata", "PPSSPP", HealthState.NotApplied, HealthReason.NotLinkedYet),
        ]);

        var page = AppsPage.From(status);
        Assert.Equal(["Eden", "Cemu", "PPSSPP"], page.Groups.Select(g => g.AppName));
        Assert.Equal(["nand", "saves"], page.Groups[0].Rows.Select(r => r.Mapping.Role));
        Assert.Equal(["ryujinx.saves"], page.NotOnThisDevice.Select(r => r.Mapping.MappingId));
        Assert.Equal(["eden.nand", "ppsspp.savedata"], page.ForApplyAll.Select(r => r.Mapping.MappingId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_mapping_set_up_elsewhere_names_those_devices_and_a_foreign_link_its_target()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        var deck = new CliDevice(sandbox, "deck");
        pc.DeliverTo(deck, metadataOnly: true);
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck");
        pc.DeliverTo(deck, metadataOnly: true);

        var notHere = AppsPage.From(StatusCheck.Run(new LocalStore(deck.LocalFolder))).NotOnThisDevice;
        Assert.Equal("Set up on pc, not here.", Assert.Single(notHere).Sentence);

        // A link SaveTether did not make, at pc's save folder.
        var elsewhere = sandbox.Dir("elsewhere");
        Sandbox.Link(pc.Source, elsewhere);
        var row = AppsPage.From(StatusCheck.Run(new LocalStore(pc.LocalFolder))).Groups.Single().Rows.Single();
        Assert.Equal(HealthState.ForeignLink, row.Mapping.Health.State);
        Assert.Equal(elsewhere, row.Mapping.LinkTarget);
        Assert.Contains(elsewhere, row.Sentence, StringComparison.Ordinal);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Apply_all_plans_what_needs_no_choice_and_sets_the_rest_aside()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        var empty = Path.Combine(pc.AppFolder, "STATES");
        pc.Ok("map", "add", "ppsspp.states", "--target", "ppsspp/STATES", "--source", empty);
        var local = new LocalStore(pc.LocalFolder);

        var (session, _) = OperationSession.Begin(local, "apply all", new NoProcesses(), null);
        using (session)
        {
            var (plans, needYou) = session!.PlanApplyAll([Id, "ppsspp.states"]);
            Assert.Equal(["ppsspp.states"], plans.Select(p => p.MappingId));
            Assert.Equal(RefusalKind.ChoiceNeeded, Assert.Single(needYou).Kind);
            Assert.Equal(Id, needYou[0].MappingId);
            Assert.True(AppsPage.NeedsFirstDeviceChoice(needYou[0]));

            foreach (var plan in plans)
            {
                Assert.Equal(Core.Execution.ExecutionStatus.Completed, session.Run(plan, null).Status);
            }
        }

        Assert.StartsWith("1 linked. 1 not linked, each needs a choice or a fix first: PPSSPP savedata.", AppsPage.ApplyAllSummary(1, ["PPSSPP savedata"]), StringComparison.Ordinal);
        Assert.Equal("2 linked.", AppsPage.ApplyAllSummary(2, []));
        sandbox.AssertCanaryIntact();
    }

    [Theory]
    [InlineData(SetupChoice.Copy)]
    [InlineData(SetupChoice.NoCopy)]
    public void Apply_all_applies_one_answer_to_every_mapping_with_saves_only_on_this_device(SetupChoice choice)
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        var local = new LocalStore(pc.LocalFolder);

        var (session, _) = OperationSession.Begin(local, "apply all", new NoProcesses(), null);
        using (session)
        {
            var (first, waiting) = session!.PlanApplyAll([Id]);
            Assert.Empty(first);
            Assert.True(AppsPage.NeedsFirstDeviceChoice(Assert.Single(waiting)));

            var (plans, stillWaiting) = session.PlanApplyAll(waiting.Select(r => r.MappingId), choice);

            Assert.Empty(stillWaiting);
            var plan = Assert.Single(plans);
            Assert.Equal(choice, plan.Choice);
            Assert.Equal(Core.Execution.ExecutionStatus.Completed, session.Run(plan, null).Status);
            Assert.Equal(choice == SetupChoice.Copy, File.Exists(Path.Combine(plan.Target, "GAME1", "save.bin")));
        }

        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_first_device_question_names_every_mapping_and_both_answers()
    {
        var text = AppsPage.FirstDeviceText(["Eden saves", "Cemu saves"]);

        Assert.Contains("Eden saves\nCemu saves", text, StringComparison.Ordinal);
        Assert.Contains(AppsPage.FirstDeviceShare, text, StringComparison.Ordinal);
        Assert.Contains(AppsPage.FirstDeviceStartEmpty, text, StringComparison.Ordinal);
        Assert.Equal("2 apps have saves only on this device", AppsPage.FirstDeviceTitle(2, "Eden saves"));
        Assert.Equal("Eden saves has saves only on this device", AppsPage.FirstDeviceTitle(1, "Eden saves"));
    }

    [Fact]
    public void An_app_from_the_imported_set_is_shown_by_its_name_not_its_id()
    {
        var catalog = new Core.Definitions.DefinitionCatalog(null);
        var id = catalog.AvailableIds().First(i => catalog.Find(i)!.Source == Core.Definitions.DefinitionSource.Imported);
        var name = catalog.Find(id)!.Definition!.Name;

        var (shown, defined) = AppNames.Resolve(catalog, id);

        Assert.Equal(name, shown);
        Assert.False(defined);
        Assert.True(AppNames.Resolve(catalog, "cemu").Defined);
        Assert.Equal(("no-such-game", false), AppNames.Resolve(catalog, "no-such-game"));
    }

    [Fact]
    public void A_status_line_stays_long_enough_to_read()
    {
        Assert.Equal(Pacing.StatusMinimum, Pacing.StatusWait(TimeSpan.Zero));
        Assert.Equal(Pacing.StatusMinimum - TimeSpan.FromMilliseconds(200), Pacing.StatusWait(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(TimeSpan.Zero, Pacing.StatusWait(Pacing.StatusMinimum));
        Assert.Equal(TimeSpan.Zero, Pacing.StatusWait(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Stop_using_here_forgets_this_devices_folder_and_is_refused_while_linked()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var local = new LocalStore(pc.LocalFolder);

        var (linked, _) = OperationSession.Begin(local, "stop using", new NoProcesses(), null);
        using (linked)
        {
            Assert.Equal(StoreProblem.Conflict, Assert.Throws<StoreException>(() => linked!.StopUsing(Id)).Problem);
        }

        pc.Ok("revert", Id);
        var (ready, _) = OperationSession.Begin(local, "stop using", new NoProcesses(), null);
        using (ready)
        {
            ready!.StopUsing(Id);
        }

        var row = Assert.Single(AppsPage.From(StatusCheck.Run(local)).NotOnThisDevice);
        Assert.Equal(HealthReason.NoSaveFolder, row.Mapping.Health.Reason);
        Assert.Equal("No device has a folder set for it.", row.Sentence);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        sandbox.AssertCanaryIntact();
    }

    private static MappingState Mapping(string id, string app, HealthState state, HealthReason? reason = null) =>
        new(id, app, id.Split('.')[1], StateWords.For(state, reason), new MappingHealth(id, state, "/s", "/t", new Note("d"), reason));

    private sealed class NoProcesses : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => [];
    }
}
