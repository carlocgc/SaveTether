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
        // The state word beside it already says Linked, so the sentence says what the folder is.
        Assert.Equal("Link", ok.Sentence);
        Assert.Equal("Symlink", AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.Ok) with { LinkKind = "Symlink" }).Sentence);
        Assert.Equal("Junction", AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.Ok) with { LinkKind = "Junction" }).Sentence);
        Assert.Null(ok.Main);
        Assert.Equal([RowAction.OpenFolder, RowAction.OpenSharedFolder, RowAction.Backups, RowAction.Revert], ok.Others);

        // Backups, where section 5.1 lists it: Linked, Not linked, Two shared copies.
        Assert.Contains(RowAction.Backups, For(HealthState.Replaced, HealthReason.FolderHoldsFiles).Others);
        Assert.Contains(RowAction.Backups, For(HealthState.DoubleSeed).Others);

        var ready = For(HealthState.NotApplied, HealthReason.NotLinkedYet);
        Assert.Equal("Original", ready.Sentence);
        Assert.Equal((RowAction?)RowAction.Apply, ready.Main);
        Assert.Equal([RowAction.OpenFolder, RowAction.Remove], ready.Others);

        // Remove is offered only when Ready to link: the core refuses it while applied.
        Assert.All(
            Enum.GetValues<HealthState>().Where(s => s != HealthState.NotApplied),
            s => Assert.DoesNotContain(RowAction.Remove, For(s).Others));

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
        Assert.Equal("1 device has a link.", Assert.Single(notHere).Sentence);

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

        Assert.StartsWith("1 linked. Not linked, each needs a choice or a fix first: PPSSPP savedata.", AppsPage.ApplyAllSummary(1, ["PPSSPP savedata"]), StringComparison.Ordinal);
        Assert.Equal("2 linked.", AppsPage.ApplyAllSummary(2, []));
        sandbox.AssertCanaryIntact();
    }

    private static (CliDevice Pc, string States, string Extra) MixedDevice(Sandbox sandbox)
    {
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");

        // A second first-device mapping, and a third whose shared location already holds saves.
        var states = Path.Combine(pc.AppFolder, "STATES");
        CliDevice.Write(states, "state1.bin", "two");
        pc.Ok("map", "add", "ppsspp.states", "--target", "ppsspp/STATES", "--source", states);
        var extra = Path.Combine(pc.AppFolder, "EXTRA");
        CliDevice.Write(extra, "extra.bin", "three");
        pc.Ok("map", "add", "ppsspp.extra", "--target", "ppsspp/EXTRA", "--source", extra);
        CliDevice.Write(Path.Combine(pc.SyncRoot, "savetether", "ppsspp", "EXTRA"), "arrived.bin", "from another device");
        return (pc, states, extra);
    }

    [Theory]
    [InlineData(SetupChoice.Copy)]
    [InlineData(SetupChoice.NoCopy)]
    public void Apply_all_applies_one_answer_to_every_first_device_mapping_and_leaves_the_rest(SetupChoice choice)
    {
        using var sandbox = new Sandbox();
        var (pc, _, _) = MixedDevice(sandbox);
        var all = new[] { Id, "ppsspp.states", "ppsspp.extra" };

        var (session, _) = OperationSession.Begin(new LocalStore(pc.LocalFolder), "apply all", new NoProcesses(), null);
        using (session)
        {
            var (first, waiting) = session!.PlanApplyAll(all);
            Assert.Empty(first);
            Assert.Equal(all, waiting.Select(r => r.MappingId));
            var batch = waiting.Where(AppsPage.NeedsFirstDeviceChoice).Select(r => r.MappingId).ToList();
            Assert.Equal([Id, "ppsspp.states"], batch);
            Assert.Equal(Classification.Guard, waiting[2].Classification);

            var (plans, stillWaiting) = session.PlanApplyAll(batch, choice);

            Assert.Empty(stillWaiting);
            Assert.Equal(batch, plans.Select(p => p.MappingId));
            Assert.All(plans, p => Assert.Equal((choice, Classification.FirstDevice), (p.Choice, p.Classification)));
            foreach (var plan in plans)
            {
                Assert.Equal(Core.Execution.ExecutionStatus.Completed, session.Run(plan, null).Status);
            }

            Assert.Equal(choice == SetupChoice.Copy, File.Exists(Path.Combine(pc.Target, "GAME1", "save.bin")));
            Assert.Equal(choice == SetupChoice.Copy, File.Exists(Path.Combine(pc.SyncRoot, "savetether", "ppsspp", "STATES", "state1.bin")));

            // The mapping with saves on both sides was never part of the answer and still waits for its own.
            Assert.Equal(RefusalKind.ChoiceNeeded, session.PlanApply("ppsspp.extra", new ApplyRequest()).Refusal!.Kind);
        }

        sandbox.AssertCanaryIntact();
    }

    [Theory]
    [InlineData(SetupChoice.Copy)]
    [InlineData(SetupChoice.NoCopy)]
    public void A_mapping_that_stops_being_a_first_setup_while_the_question_is_open_is_not_given_the_answer(SetupChoice choice)
    {
        using var sandbox = new Sandbox();
        var (pc, _, _) = MixedDevice(sandbox);

        var (session, _) = OperationSession.Begin(new LocalStore(pc.LocalFolder), "apply all", new NoProcesses(), null);
        using (session)
        {
            var (_, waiting) = session!.PlanApplyAll([Id, "ppsspp.states"]);
            var batch = waiting.Where(AppsPage.NeedsFirstDeviceChoice).Select(r => r.MappingId).ToList();
            Assert.Equal(2, batch.Count);

            // Sync delivers saves to the shared location of one of them before the answer is given.
            CliDevice.Write(Path.Combine(pc.SyncRoot, "savetether", "ppsspp", "STATES"), "arrived.bin", "from another device");

            var (plans, stillWaiting) = session.PlanApplyAll(batch, choice);

            Assert.Equal([Id], plans.Select(p => p.MappingId));
            Assert.Equal(choice, plans[0].Choice);
            var refused = Assert.Single(stillWaiting);
            Assert.Equal("ppsspp.states", refused.MappingId);
            Assert.Equal(RefusalKind.ChoiceNeeded, refused.Kind);
            Assert.Equal(Classification.Guard, refused.Classification);
        }
    }

    [Fact]
    public void The_first_device_question_names_every_mapping_and_both_answers()
    {
        var text = AppsPage.FirstDeviceText(["Eden saves", "Cemu saves"], others: false);

        Assert.Contains("Eden saves\nCemu saves", text, StringComparison.Ordinal);
        Assert.Contains(AppsPage.FirstDeviceShare, text, StringComparison.Ordinal);
        Assert.Contains(AppsPage.FirstDeviceStartEmpty, text, StringComparison.Ordinal);
        Assert.DoesNotContain("another device", text, StringComparison.Ordinal);
        Assert.Contains("another device", AppsPage.FirstDeviceText(["Eden saves"], others: true), StringComparison.Ordinal);
        Assert.Equal("2 save folders have data only on this device", AppsPage.FirstDeviceTitle(2, "Eden saves"));
        Assert.Equal("Eden saves has data only on this device", AppsPage.FirstDeviceTitle(1, "Eden saves"));
    }

    [Fact]
    public void An_app_from_the_imported_set_is_shown_by_its_name_not_its_id()
    {
        var catalog = new Core.Definitions.DefinitionCatalog(null);
        var id = catalog.AvailableIds().First(i => catalog.Find(i)!.Source == Core.Definitions.DefinitionSource.Imported);
        var name = catalog.Find(id)!.Definition!.Name;

        var (shown, defined) = AppNames.Resolve(catalog, id);

        Assert.Equal(name, shown);
        Assert.True(defined);

        // A role the imported definition has is defined; one it lacks (a mapping made by hand) is not.
        var role = catalog.Find(id)!.Definition!.Roles[0].Id;
        Assert.True(AppNames.Resolve(catalog, id, role).Defined);
        Assert.False(AppNames.Resolve(catalog, id, "no-such-role-by-hand").Defined);

        // The same role with the shared target the definition gives is defined; one whose target a hand-made mapping set
        // differently is not (Add app would refuse it with a different target).
        var roleDefinition = catalog.Find(id)!.Definition!.Roles[0];
        Assert.True(AppNames.Resolve(catalog, id, role, () => roleDefinition.MappingTargetFor(id)).Defined);
        Assert.False(AppNames.Resolve(catalog, id, role, () => "custom/other-target").Defined);
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
    public void A_row_no_device_has_a_folder_for_offers_a_red_Delete_and_one_another_device_has_does_not()
    {
        var unused = AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.NotApplied, HealthReason.NoSaveFolder));
        Assert.Equal((RowAction?)RowAction.Delete, unused.Destructive);
        Assert.Equal((RowAction?)RowAction.AddHere, unused.Main);
        Assert.Equal("Delete", AppsPage.Label(RowAction.Delete));

        var elsewhere = AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.NotApplied, HealthReason.NoSaveFolder) with { SetUpOn = ["pc"] });
        Assert.Null(elsewhere.Destructive);

        Assert.All(
            Enum.GetValues<HealthState>().Where(s => s != HealthState.NotApplied),
            s => Assert.Null(AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", s)).Destructive));
        Assert.Null(AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.NotApplied, HealthReason.NotLinkedYet)).Destructive);
    }

    [Fact]
    public void A_row_other_devices_have_says_how_many_and_the_names_are_the_hover_text()
    {
        Assert.Equal("No device links this role.", AppsPage.SetUpOnSentence([]));
        Assert.Equal("1 device has a link.", AppsPage.SetUpOnSentence(["pc"]));
        Assert.Equal("12 devices have a link.", AppsPage.SetUpOnSentence(Enumerable.Range(1, 12).Select(i => $"device-{i:00}").ToList()));

        var many = Enumerable.Range(1, 12).Select(i => $"device-{i:00}").ToList();
        var row = AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.NotApplied, HealthReason.NoSaveFolder) with { SetUpOn = many });
        Assert.Equal("12 devices have a link.", row.Sentence);
        Assert.Equal(many, row.Devices);
        Assert.Equal(12, AppsPage.DevicesTip(many).Split('\n').Length);
        Assert.Equal("device-01\ndevice-02", AppsPage.DevicesTip(["device-01", "device-02"]));
        Assert.Null(row.Destructive);

        // A few devices work the same way: how many, with the names on hover.
        var few = AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.NotApplied, HealthReason.NoSaveFolder) with { SetUpOn = ["pc", "deck"] });
        Assert.Equal("2 devices have a link.", few.Sentence);
        Assert.Equal(["pc", "deck"], few.Devices);

        // No device has it: no list, and it can be deleted.
        var none = AppsPage.Row(Mapping("ppsspp.savedata", "PPSSPP", HealthState.NotApplied, HealthReason.NoSaveFolder));
        Assert.Null(none.Devices);
        Assert.Equal((RowAction?)RowAction.Delete, none.Destructive);
    }

    [Fact]
    public void The_Delete_confirmation_asks_about_other_devices_only_when_there_are_some()
    {
        Assert.Equal("Delete PPSSPP savedata from the sync folder?", AppsPage.DeleteTitle("PPSSPP savedata"));
        Assert.Equal("Its shared copy is backed up on this device first.", AppsPage.DeleteText(others: false));
        Assert.Contains("make sure none is still writing to it", AppsPage.DeleteText(others: true), StringComparison.Ordinal);
        Assert.Equal("PPSSPP savedata was deleted from the sync folder. Its shared copy is in Backups.", AppsPage.DeletedText("PPSSPP savedata", hadBackup: true));
        Assert.Equal("PPSSPP savedata was deleted from the sync folder.", AppsPage.DeletedText("PPSSPP savedata", hadBackup: false));
        Assert.Equal("before deleting", BackupsPage.Reason(Core.Backup.BackupReason.PreDelete));
    }

    [Fact]
    public void Delete_removes_a_mapping_nobody_uses_and_the_row_goes()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        pc.Ok("revert", Id);
        pc.Ok("map", "remove", Id);
        var local = new LocalStore(pc.LocalFolder);
        var row = Assert.Single(AppsPage.From(StatusCheck.Run(local)).NotOnThisDevice);
        Assert.Equal((RowAction?)RowAction.Delete, row.Destructive);

        var (session, _) = OperationSession.Begin(local, "map delete", new NoProcesses(), null);
        using (session)
        {
            var described = session!.DescribeMappingDeletion(Id);
            Assert.Equal(1, described.Files);
            var done = session.DeleteMapping(Id);
            Assert.NotNull(done.Backup);
        }

        var status = StatusCheck.Run(local);
        Assert.Empty(AppsPage.From(status).NotOnThisDevice);
        Assert.Empty(status.Mappings);
        Assert.Equal(new Dictionary<string, string> { ["GAME1/save.bin"] = "one" }, pc.LocalFiles());
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Remove_forgets_this_devices_folder_and_is_refused_while_linked()
    {
        using var sandbox = new Sandbox();
        var pc = new CliDevice(sandbox, "pc");
        pc.SetUp(createRoot: true);
        pc.Save("GAME1/save.bin", "one");
        pc.Ok("apply", Id, "--copy");
        var local = new LocalStore(pc.LocalFolder);

        var (linked, _) = OperationSession.Begin(local, "remove", new NoProcesses(), null);
        using (linked)
        {
            Assert.Equal(StoreProblem.Conflict, Assert.Throws<StoreException>(() => linked!.Remove(Id)).Problem);
        }

        pc.Ok("revert", Id);
        var (ready, _) = OperationSession.Begin(local, "remove", new NoProcesses(), null);
        using (ready)
        {
            ready!.Remove(Id);
        }

        var row = Assert.Single(AppsPage.From(StatusCheck.Run(local)).NotOnThisDevice);
        Assert.Equal(HealthReason.NoSaveFolder, row.Mapping.Health.Reason);
        Assert.Equal("No device links this role.", row.Sentence);
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
