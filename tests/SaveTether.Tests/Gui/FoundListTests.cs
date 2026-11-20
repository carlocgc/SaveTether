using SaveTether.Core.Definitions;
using SaveTether.Core.Detection;
using SaveTether.Core.Resolution;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.Cli;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Phase 3.5, T5: page 1's found list, the added apps, the scan session, and page 2's two notes.</summary>
public class FoundListTests
{
    private static (CliDevice Deck, AddAppFlow Flow) NewDevice(Sandbox sandbox)
    {
        var deck = new CliDevice(sandbox, "deck");
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--create-root");
        AppAddCliTests.ShareDefinition(deck, "testapp", AppAddCliTests.TestApp);
        var environment = new ResolverEnvironment { Os = TargetOs.Linux, Home = deck.Home, UserName = "deck" };
        return (deck, new AddAppFlow(new LocalStore(deck.LocalFolder), environment));
    }

    private static void WaitUntilIdle(ScanSession session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (session.Tasks.IsRunning)
        {
            Assert.True(DateTime.UtcNow < deadline, "the scan did not end");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Last_played_names_the_newest_save_or_says_there_is_none()
    {
        Assert.Equal("No save files yet", AddAppFlow.LastPlayed(null));
        Assert.Equal("Last played 2026-03-04", AddAppFlow.LastPlayed(new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Added_apps_are_those_with_a_folder_on_this_device_in_name_order()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        Assert.Empty(flow.AddedApps());

        CliDevice.Write(Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save"), "a.sav", "x");
        deck.Ok("app", "add", "cemu");
        var install = Path.Combine(deck.Home, "portable");
        CliDevice.Write(install, "app.cfg", "config");
        CliDevice.Write(install, "saves/slot1.sav", "save");
        deck.Ok("app", "add", "testapp", "--install-dir", install);

        var added = flow.AddedApps();

        Assert.Equal(["cemu", "testapp"], added.Select(a => a.Id));
        Assert.Equal(added.Select(a => a.Name).Order(StringComparer.OrdinalIgnoreCase), added.Select(a => a.Name));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_scan_session_keeps_what_it_found_after_the_scan_ends_and_starts_the_first_scan_once()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        CliDevice.Write(Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save"), "a.sav", "x");
        var session = new ScanSession(new BackgroundTasks());
        Assert.False(session.HasRun);

        Assert.True(session.Start(flow, ScanOptions.Default));
        WaitUntilIdle(session);

        Assert.True(session.HasRun);
        Assert.Contains(session.Found, f => f.App.Id == "cemu");
        Assert.Null(session.Tasks.Current);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_second_scan_is_refused_while_one_runs()
    {
        using var sandbox = new Sandbox();
        var (_, flow) = NewDevice(sandbox);
        var tasks = new BackgroundTasks();
        var session = new ScanSession(tasks);
        using var running = tasks.TryStart("Scanning");

        Assert.False(session.Start(flow, ScanOptions.Default));
        Assert.Equal(BackgroundTasks.ScanRunningReason, tasks.BlockedReason);
        Assert.False(session.HasRun);
    }

    [Fact]
    public void A_cancelled_scan_ends_and_a_new_scan_replaces_its_rows()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        CliDevice.Write(Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save"), "a.sav", "x");
        var session = new ScanSession(new BackgroundTasks());
        Assert.True(session.Start(flow, ScanOptions.Default));
        session.Tasks.CancelCurrent();
        WaitUntilIdle(session);

        Assert.True(session.Start(flow, ScanOptions.Default));
        WaitUntilIdle(session);

        Assert.Single(session.Found, f => f.App.Id == "cemu");
    }

    [Fact]
    public void Page_2_of_an_imported_app_is_marked_unverified_and_a_built_in_one_is_not()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        CliDevice.Write(Path.Combine(deck.Home, ".local", "share", "AAAAXY"), "save.json", "{}");
        CliDevice.Write(Path.Combine(deck.Home, ".local", "share", "Cemu", "mlc01", "usr", "save"), "a.sav", "x");

        var imported = flow.Start("aaaaxy");
        var builtIn = flow.Start("cemu");

        Assert.Equal(AddAppPage.Plan, imported.Page);
        Assert.True(imported.Unverified);
        Assert.False(builtIn.Unverified);
        Assert.Contains("has not been tried by hand", AddAppFlow.UnverifiedNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_2_names_the_stores_whose_copies_keep_saves_in_the_cloud()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var install = Path.Combine(deck.Home, "portable");
        CliDevice.Write(install, "app.cfg", "config");
        CliDevice.Write(install, "saves/slot1.sav", "save");
        flow.Start("testapp");
        Assert.Null(flow.SetLocation(LocationField.InstallFolder, install).CloudNote);
        AppAddCliTests.ShareDefinition(deck, "testapp", AppAddCliTests.TestApp.Replace("\"quirks\"", "\"cloud_saves\": [\"steam\", \"gog\"], \"quirks\"", StringComparison.Ordinal));

        flow.Start("testapp");
        var view = flow.SetLocation(LocationField.InstallFolder, install);

        Assert.Equal(AddAppPage.Plan, view.Page);
        Assert.Contains("Steam, GOG copy", view.CloudNote, StringComparison.Ordinal);
        Assert.Contains("another store or launcher", view.CloudNote, StringComparison.Ordinal);
    }
}
