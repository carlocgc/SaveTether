using SaveTether.Core.Detection;
using SaveTether.Core.Definitions;
using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Resolution;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 8 of the desktop app spec: Add app, driven through its view model against fixture devices.</summary>
public class AddAppFlowTests
{
    private static (CliDevice Deck, AddAppFlow Flow) NewDevice(Sandbox sandbox)
    {
        var deck = new CliDevice(sandbox, "deck");
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--create-root");
        AppAddCliTests.ShareDefinition(deck, "testapp", AppAddCliTests.TestApp);
        var environment = new ResolverEnvironment { Os = TargetOs.Linux, Home = deck.Home, UserName = "deck" };
        return (deck, new AddAppFlow(new LocalStore(deck.LocalFolder), environment));
    }

    private static bool MappingExists(CliDevice device, string id) =>
        File.Exists(Path.Combine(device.SyncRoot, "savetether", ".savetether", "mappings", id + ".json"));

    /// <summary>Page 2 for an app with more than one install to pick from: the install chosen, as the owner would.</summary>
    private static AddAppView StartAt(AddAppFlow flow, string appId, string installId)
    {
        var view = flow.Start(appId);
        return view.Page == AddAppPage.ChooseInstall ? flow.ChooseInstall(installId) : view;
    }

    [Fact]
    public void Page_1_lists_the_catalog_by_name_filters_as_typed_and_labels_each_app()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        AppAddCliTests.ShareDefinition(deck, "broken", AppAddCliTests.TestApp.Replace("\"emulator\"", "\"toaster\"", StringComparison.Ordinal).Replace("testapp", "broken", StringComparison.Ordinal));

        var apps = flow.Catalog();
        Assert.Equal(apps.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase), apps.Select(a => a.Name));
        Assert.Contains(apps, a => a is { Id: "cemu", Source: DefinitionSource.BuiltIn, IsValid: true });
        Assert.Contains(apps, a => a is { Id: "testapp", Name: "Test App", Source: DefinitionSource.Shared });
        Assert.Contains(apps, a => a is { Id: "broken", IsValid: false });
        Assert.Equal(["testapp"], AddAppFlow.Filter(apps, " test ").Select(a => a.Id));
        Assert.Equal(apps, AddAppFlow.Filter(apps, ""));

        Assert.Equal(CatalogStatus.NotFound, flow.Detect("testapp"));
        Assert.Equal(CatalogStatus.Invalid, flow.Detect("broken"));
        Directory.CreateDirectory(Path.Combine(deck.Home, "native", "data"));
        Assert.Equal(CatalogStatus.Found, flow.Detect("testapp"));
        deck.Ok("app", "add", "testapp");
        Assert.Equal(CatalogStatus.Added, flow.Detect("testapp"));

        // "Stop using here" on every folder of the app: no longer Added, though the device file still
        // remembers how it was found (found by the owner with Cemu).
        deck.Ok("map", "remove", "testapp.saves");
        Assert.Equal(CatalogStatus.Added, flow.Detect("testapp"));
        deck.Ok("map", "remove", "testapp.states");
        Assert.Equal(CatalogStatus.Found, flow.Detect("testapp"));
        foreach (var status in Enum.GetValues<CatalogStatus>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AddAppFlow.Label(status)));
        }
    }

    [Fact]
    public void An_added_app_opens_only_for_a_folder_it_has_not_added_and_Add_writes_only_the_new_one()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var data = Path.Combine(deck.Home, "native", "data");
        CliDevice.Write(data, "saves/slot1.sav", "12345");
        deck.Ok("app", "add", "testapp");

        // Page 1: shots was not added (it is off by default), so the app still has a folder to add.
        var row = Assert.Single(flow.AddedApps());
        Assert.True(row.MoreToAdd);

        // Page 2: the added folders show ticked and locked; Add is off until a new folder is ticked.
        var view = StartAt(flow, "testapp", "native");
        Assert.True(view.Roles.Single(r => r.RoleId == "saves").AlreadyAdded);
        Assert.True(view.Roles.Single(r => r.RoleId == "states").AlreadyAdded);
        var shots = view.Roles.Single(r => r.RoleId == "shots");
        Assert.False(shots.AlreadyAdded);
        Assert.False(shots.Ticked);
        Assert.False(view.CanAdd);

        // Unticking an added folder changes nothing: Add never removes one.
        Assert.Equal(view, flow.SetRole("saves", ticked: false));

        var ticked = flow.SetRole("shots", ticked: true);
        Assert.True(ticked.CanAdd);
        var result = flow.Add(ticked);

        Assert.Equal(["testapp.shots"], result.Added);
        Assert.True(MappingExists(deck, "testapp.saves"));
        Assert.True(MappingExists(deck, "testapp.shots"));

        // Now every folder is added: the app is listed but has nothing left to open.
        Assert.False(Assert.Single(flow.AddedApps()).MoreToAdd);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_plan_shows_the_install_and_each_role_and_Add_writes_only_the_roles_ticked()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var data = Path.Combine(deck.Home, "native", "data");
        CliDevice.Write(data, "saves/slot1.sav", "12345");

        var view = StartAt(flow, "testapp", "native");

        Assert.Equal(AddAppPage.Plan, view.Page);
        Assert.Equal("Add Test App", view.Title);
        Assert.Equal($"Install: native, found at {data}.", view.Found);
        Assert.Equal(["A quirk the owner should read."], view.Quirks);
        Assert.Equal(["saves", "states", "shots"], view.Roles.Select(r => r.RoleId));
        Assert.Equal([true, true, false], view.Roles.Select(r => r.Ticked));
        Assert.Equal("Off by default: screenshots and recordings, which can be large.", view.Roles[2].Unticked);
        Assert.Equal("1 file, 5 bytes", AddAppFlow.SizeOf(view.Roles[0].Planned!));
        Assert.Equal("not there yet", AddAppFlow.SizeOf(view.Roles[1].Planned!));
        Assert.True(view.CanAdd);

        // Ticking a role plans it too; unticking every role turns Add off.
        view = flow.SetRole("shots", ticked: true);
        Assert.Equal([true, true, true], view.Roles.Select(r => r.Ticked));
        view = flow.SetRole("states", ticked: false);
        Assert.Equal(["testapp.saves", "testapp.shots"], view.Plan!.Roles.Select(r => r.MappingId));
        flow.SetRole("saves", ticked: false);
        var off = flow.SetRole("shots", ticked: false);
        Assert.False(off.CanAdd);
        Assert.All(off.Roles, r => Assert.False(r.Ticked));
        view = flow.SetRole("saves", ticked: true);
        Assert.Equal(["testapp.saves"], view.Plan!.Roles.Select(r => r.MappingId));

        var result = flow.Add(view);

        Assert.True(result.Succeeded);
        Assert.Equal(["testapp.saves"], result.Added);
        Assert.True(MappingExists(deck, "testapp.saves"));
        Assert.False(MappingExists(deck, "testapp.states"));
        Assert.Equal("Test App added. Apply it now?", AddAppFlow.AddedQuestion(view.AppName));

        // The row it leaves is Ready to link, with Apply.
        var row = AppsPage.Row(StatusCheck.Run(new LocalStore(deck.LocalFolder)).Mappings.Single());
        Assert.Equal(RowAction.Apply, row.Main);
        sandbox.AssertCanaryIntact();
    }

    private static string DeviceFileText(CliDevice device) =>
        File.ReadAllText(Path.Combine(device.SyncRoot, "savetether", ".savetether", "devices", "deck.json")).Replace(@"\\", @"\", StringComparison.Ordinal);

    [Fact]
    public void A_default_off_folder_that_moved_can_be_ticked_and_added_again()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var first = Path.Combine(deck.Home, "first");
        CliDevice.Write(first, "app.cfg", "config");
        CliDevice.Write(first, "saves/slot1.sav", "1");
        CliDevice.Write(first, "shots/a.png", "2");
        deck.Ok("app", "add", "testapp", "--install-dir", first, "--role", "saves", "--role", "shots");
        Directory.Delete(first, recursive: true);

        var second = Path.Combine(deck.Home, "second");
        CliDevice.Write(second, "app.cfg", "config");
        CliDevice.Write(second, "shots/a.png", "2");
        flow.Start("testapp");
        var view = flow.SetLocation(LocationField.InstallFolder, second);

        // shots is off by default, so the plan leaves it out; it moved, so it is not "already added" and can be ticked.
        var shots = view.Roles.Single(r => r.RoleId == "shots");
        Assert.False(shots.AlreadyAdded);
        Assert.False(shots.Ticked);
        view = flow.SetRole("shots", ticked: true);
        Assert.True(view.CanAdd);

        Assert.True(flow.Add(view).Succeeded);
        Assert.Contains(Path.Combine(second, "shots"), DeviceFileText(deck), StringComparison.Ordinal);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void Add_shows_the_page_again_when_a_locked_folder_was_removed_while_it_was_open()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        CliDevice.Write(Path.Combine(deck.Home, "native", "data"), "saves/slot1.sav", "12345");
        deck.Ok("app", "add", "testapp");

        var view = StartAt(flow, "testapp", "native");
        view = flow.SetRole("shots", ticked: true);
        Assert.True(view.CanAdd);
        Assert.True(view.Roles.Single(r => r.RoleId == "saves").AlreadyAdded);

        // The folder it locked is removed on this device (its shared mapping stays) before Add is pressed.
        deck.Ok("map", "remove", "testapp.saves");
        var result = flow.Add(view);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Changed);
        Assert.False(result.Changed!.Roles.Single(r => r.RoleId == "saves").AlreadyAdded);
        Assert.DoesNotContain("testapp.saves", DeviceFileText(deck), StringComparison.Ordinal);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_added_text_does_not_say_nothing_is_linked_when_some_folders_already_are()
    {
        // Found by the owner with Cemu: adding an app already added and applied said "Nothing is linked yet".
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        CliDevice.Write(Path.Combine(deck.Home, "native", "data"), "saves/slot1.sav", "12345");
        deck.Ok("app", "add", "testapp");
        deck.Ok("apply", "testapp.saves", "--copy");

        // Every folder of the app is added now, so the page has nothing new to add; a linked folder says so.
        var view = StartAt(flow, "testapp", "native");
        Assert.Equal("already a link", AddAppFlow.SizeOf(view.Roles.Single(r => r.RoleId == "saves").Planned!));
        Assert.False(view.CanAdd);
        Assert.Throws<InvalidOperationException>(() => flow.Add(view));

        var text = AddAppFlow.AddedText(["testapp.saves", "testapp.states"], ["testapp.saves"]);
        Assert.DoesNotContain("Nothing is linked", text, StringComparison.Ordinal);
        Assert.Contains("Already linked here: testapp.saves.", text, StringComparison.Ordinal);
        Assert.Contains("Apply links testapp.states", text, StringComparison.Ordinal);

        // With every folder linked, there is nothing to apply.
        Assert.Equal(
            "Added testapp.saves, testapp.states. Already linked here: testapp.saves, testapp.states. There is nothing to apply; the Apps page shows whether each link is healthy.",
            AddAppFlow.AddedText(["testapp.saves", "testapp.states"], ["testapp.saves", "testapp.states"]));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void An_app_with_a_known_and_a_portable_install_opens_on_a_page_to_choose_between_them()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var portable = Path.Combine(deck.Home, "portable");
        var native = Path.Combine(deck.Home, "native", "data");
        CliDevice.Write(portable, "app.cfg", "config");
        Directory.CreateDirectory(native);

        // The installs listed for this system come first: the one found, and the one the owner gives a folder for.
        var view = flow.Start("testapp");
        Assert.Equal(AddAppPage.ChooseInstall, view.Page);
        Assert.Equal(["native", "portable"], view.Candidates.Select(c => c.Id));
        Assert.Equal(Path.Combine(deck.Home, "native", "data"), view.InstallPaths["native"]);
        Assert.Equal("<install folder>/saves", view.InstallPaths["portable"]);
        Assert.Null(flow.Back(view.Page));

        // A known install goes to its plan with no folder to give.
        var plan = flow.ChooseInstall("native");
        Assert.Equal(AddAppPage.Plan, plan.Page);
        Assert.Empty(plan.Fields);
        Assert.Equal(AddAppPage.ChooseInstall, flow.Back(plan.Page)!.Page);

        // The portable one asks for its folder, with what to select under it, then plans.
        var folder = flow.ChooseInstall("portable");
        Assert.Equal(AddAppPage.NeedsLocation, folder.Page);
        Assert.Equal("Where is Test App installed?", folder.Title);
        Assert.Equal([(LocationField.InstallFolder, (string?)null)], folder.Fields);
        Assert.Equal("Select the folder containing \"saves\".", folder.InstallFolderHint);
        Assert.Equal(AddAppPage.ChooseInstall, flow.Back(folder.Page)!.Page);

        plan = flow.SetLocation(LocationField.InstallFolder, portable);
        Assert.Equal(AddAppPage.Plan, plan.Page);
        Assert.Equal(Path.Combine(portable, "saves"), plan.Plan!.Roles.Single().Source);
        Assert.True(flow.Add(plan).Succeeded);
        Assert.False(MappingExists(deck, "testapp.states"));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_install_used_last_time_does_not_skip_the_page_and_its_folder_can_be_changed()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var first = Path.Combine(deck.Home, "first");
        var copy = Path.Combine(deck.Home, "copy");
        CliDevice.Write(first, "app.cfg", "config");
        CliDevice.Write(copy, "app.cfg", "config");
        Directory.CreateDirectory(Path.Combine(deck.Home, "native", "data"));

        // The portable install was used last time, and both are found: the page is still there (found on the owner's Cemu).
        flow.Start("testapp");
        flow.ChooseInstall("portable");
        var view = flow.SetLocation(LocationField.InstallFolder, first);
        Assert.True(flow.Add(view).Succeeded);

        view = flow.Start("testapp");
        Assert.Equal(AddAppPage.ChooseInstall, view.Page);

        // The folder given before is filled in, and a copy can be given instead though the first still exists.
        view = flow.ChooseInstall("portable");
        Assert.Equal([(LocationField.InstallFolder, (string?)first)], view.Fields);
        view = flow.SetLocation(LocationField.InstallFolder, copy);
        Assert.Equal(AddAppPage.Plan, view.Page);
        Assert.Equal(Path.Combine(copy, "saves"), view.Plan!.Roles.Single().Source);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void The_install_folder_hint_names_the_path_to_the_shared_folder_inside_it()
    {
        var cemu = DefinitionLoader.LoadFile(Path.Combine(SaveTether.Tests.Definitions.DefinitionLoaderTests.FindRepositoryRoot(), "definitions", "cemu.jsonc")).Definition!;

        Assert.Equal(
            "Select the folder containing \"mlc01/usr/save\".",
            AddAppFlow.InstallFolderHintFor(cemu, cemu.Installs.Where(i => i.Id == "win-portable")));

        // An install that does not use the install folder has no hint.
        Assert.Null(AddAppFlow.InstallFolderHintFor(cemu, cemu.Installs.Where(i => i.Id == "linux-native")));
    }

    [Fact]
    public void Not_found_offers_only_the_fields_that_would_help_and_a_relative_path_is_refused()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);

        var view = flow.Start("testapp");
        Assert.Equal("No install of Test App was found.", view.Title);
        Assert.Contains(view.Sentences, s => s.StartsWith("native: No folder at", StringComparison.Ordinal));

        // An install that needs a value is said in words, not with the core's token (found on the Deck).
        Assert.Contains("portable: can only be looked for once you give its location below.", view.Sentences);
        Assert.DoesNotContain(view.Sentences, s => s.Contains("{install_dir}", StringComparison.Ordinal));
        Assert.Equal([LocationField.InstallFolder], view.Fields.Select(f => f.Field));

        view = flow.SetLocation(LocationField.InstallFolder, "relative/folder");
        Assert.Contains("'relative/folder' is not a full path. Give the whole path, or choose the folder.", view.Sentences);

        var portable = Path.Combine(deck.Home, "portable");
        CliDevice.Write(portable, "app.cfg", "config");
        Assert.Equal(AddAppPage.Plan, flow.SetLocation(LocationField.InstallFolder, portable).Page);

        // The cemu built-in has no value to ask for on Linux: nothing found, and no field.
        Assert.Empty(flow.Start("cemu").Fields);
        Assert.Null(flow.Back());
    }

    [Fact]
    public void A_moved_app_found_again_says_so_and_takes_the_new_location()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var first = Path.Combine(deck.Home, "first");
        CliDevice.Write(first, "app.cfg", "config");
        deck.Ok("app", "add", "testapp", "--install-dir", first);
        Directory.Delete(first, recursive: true);

        var view = flow.Start("testapp");
        Assert.Equal(AddAppPage.NeedsLocation, view.Page);
        Assert.Contains("Test App was not where it was last time, so it may have moved. Give its new location.", view.Sentences);
        Assert.Equal([(LocationField.InstallFolder, first)], view.Fields);

        var second = Path.Combine(deck.Home, "second");
        CliDevice.Write(second, "app.cfg", "config");
        view = flow.SetLocation(LocationField.InstallFolder, second);
        Assert.True(flow.Add(view).Succeeded);
        Assert.Contains(Path.Combine(second, "saves"), File.ReadAllText(Path.Combine(deck.SyncRoot, "savetether", ".savetether", "devices", "deck.json")).Replace(@"\\", @"\", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_conflict_and_a_definition_with_mistakes_are_said_and_nothing_is_written()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var install = Path.Combine(deck.Home, "portable");
        CliDevice.Write(install, "app.cfg", "config");
        deck.Ok("map", "add", "flash.objects", "--target", "flash/objects", "--source", Path.Combine(install, "saves"));

        flow.Start("testapp");
        var view = flow.ChooseInstall("portable");
        view = flow.SetLocation(LocationField.InstallFolder, install);

        Assert.Equal(AddAppPage.Refused, view.Page);
        Assert.Equal(
            $"The folder is already used by flash.objects, at {Path.Combine(install, "saves")}. One folder can be shared by one mapping only.",
            Assert.Single(view.Sentences));
        Assert.Contains("is already the save folder of 'flash.objects'", view.Details, StringComparison.Ordinal);
        Assert.False(MappingExists(deck, "testapp.saves"));

        AppAddCliTests.ShareDefinition(deck, "testapp", AppAddCliTests.TestApp.Replace("\"emulator\"", "\"toaster\"", StringComparison.Ordinal));
        view = flow.Start("testapp");
        Assert.Equal("The definition for testapp has mistakes, so it cannot be used.", view.Title);
        Assert.Contains("testapp.jsonc(5):", view.Details, StringComparison.Ordinal);

        foreach (var kind in Enum.GetValues<ConflictKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AddAppFlow.Conflict(new ConflictFacts(kind, "a.b", "/f", "a/b"), null)));
        }
    }

    [Fact]
    public void Add_plans_again_under_the_lock_and_writes_nothing_if_the_plan_changed_or_the_lock_is_held()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var data = Path.Combine(deck.Home, "native", "data");
        Directory.CreateDirectory(data);
        var view = StartAt(flow, "testapp", "native");

        // The lock is held (here by this process, so its holder is not named): refused, in the app's words.
        using (OperationLock.Acquire(new LocalStore(deck.LocalFolder), "cli", "apply"))
        {
            var refused = flow.Add(view);
            Assert.False(refused.Succeeded);
            Assert.StartsWith("Another SaveTether operation is running", refused.Refused, StringComparison.Ordinal);
        }

        // Something changed behind the page: shown again, not written.
        deck.Ok("map", "add", "other.saves", "--target", "other/saves", "--source", Path.Combine(data, "saves"));
        var changed = flow.Add(view);
        Assert.False(changed.Succeeded);
        Assert.Equal(AddAppFlow.DifferentNow, changed.Changed!.Sentences[0]);
        Assert.Equal(AddAppPage.Refused, changed.Changed.Page);
        Assert.False(MappingExists(deck, "testapp.saves"));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_row_offers_find_again_and_add_through_Add_app_or_the_hand_form()
    {
        static MappingState Mapping(HealthState state, HealthReason reason, bool defined) =>
            new("cemu.save", "Cemu", "save", StateWords.For(state, reason), new MappingHealth("cemu.save", state, "/s", "/t", new Note("d"), reason))
            {
                HasDefinition = defined,
            };

        Assert.Equal(RowAction.FindAppAgain, AppsPage.Row(Mapping(HealthState.Replaced, HealthReason.FolderMissing, true)).Main);
        // Without a definition (a folder added by hand) the same rows open the hand form instead (section 8).
        Assert.Equal(RowAction.ChooseFolderAgain, AppsPage.Row(Mapping(HealthState.Replaced, HealthReason.FolderMissing, false)).Main);
        Assert.Equal(RowAction.AddHere, AppsPage.Row(Mapping(HealthState.NotApplied, HealthReason.NoSaveFolder, true)).Main);
        Assert.Equal(RowAction.AddHere, AppsPage.Row(Mapping(HealthState.NotApplied, HealthReason.NoSaveFolder, false)).Main);
        Assert.Equal("cemu", Mapping(HealthState.NotApplied, HealthReason.NoSaveFolder, true).AppId);

        // Apply all still works only on rows whose main action is Apply.
        var status = new OverallStatus(OverallCondition.NeedsAttention, null, [Mapping(HealthState.NotApplied, HealthReason.NoSaveFolder, true)]);
        Assert.Empty(AppsPage.From(status).ForApplyAll);
    }

    [Fact]
    public void The_found_list_has_apps_on_this_device_newest_save_first_and_search_is_capped()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        // Cemu's native install keeps its saves under the XDG data folder.
        var saves = Directory.CreateDirectory(Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save")).FullName;
        File.WriteAllText(Path.Combine(saves, "a.sav"), "x");

        var found = AddAppFlow.Newest(flow.FoundApps());

        Assert.All(found, f => Assert.NotNull(f.SaveFolder));
        Assert.Equal(found.OrderByDescending(f => f.NewestWrite ?? DateTime.MinValue).Select(f => f.App.Id), found.Select(f => f.App.Id));
        Assert.DoesNotContain(found, f => f.App.Id == "testapp" && !Directory.Exists(f.SaveFolder));

        var all = flow.Search("a");
        Assert.True(all.Shown.Count <= AddAppFlow.MaxSearchRows);
        Assert.True(all.Total >= all.Shown.Count);
        Assert.Equal(all.Total > AddAppFlow.MaxSearchRows, all.HasMore);
        Assert.Contains(flow.Search("test app").Shown, a => a.Id == "testapp");
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_deep_scan_finds_saves_of_an_app_that_is_not_installed_and_is_read_only()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        // The detect folder (ghost/bin) is absent; only the save folder exists.
        AppAddCliTests.ShareDefinition(deck, "ghost", """
            {
              "schema": 1, "id": "ghost", "name": "Ghost", "kind": "game",
              "installs": [{ "id": "native", "os": "linux", "detect": { "dir": "{home}/ghost/bin" }, "base": "{home}/ghost/data" }],
              "roles": [{ "id": "saves", "class": "save", "default": true, "path": "saves", "target": "saves" }],
            }
            """);
        var saves = Path.Combine(deck.Home, "ghost", "data", "saves");
        CliDevice.Write(Path.Combine(deck.Home, "ghost", "data"), "saves/a.sav", "x");

        Assert.DoesNotContain(flow.FoundApps(), f => f.App.Id == "ghost");
        var row = Assert.Single(flow.FoundApps(ScanOptions.Deep()), f => f.App.Id == "ghost");

        Assert.False(row.Installed);
        Assert.Equal(saves, row.SaveFolder);
        sandbox.AssertCanaryIntact();
    }
}
