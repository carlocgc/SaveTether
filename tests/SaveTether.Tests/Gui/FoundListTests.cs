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

    private static FoundApp Found(string id) =>
        new(new CatalogApp(id, id, DefinitionSource.BuiltIn, true), id, "/saves/" + id, null);

    /// <summary>A scan source that yields the given apps, then waits to be released (or cancelled) before it ends.</summary>
    private sealed class HeldScan(params string[] ids)
    {
        public ManualResetEventSlim Yielded { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public IEnumerable<FoundApp> Run(CancellationToken cancel)
        {
            foreach (var id in ids)
            {
                yield return Found(id);
            }

            Yielded.Set();
            Release.Wait(cancel);
        }
    }

    private static string[] Ids(ScanSession session) => [.. session.Found.Select(f => f.App.Id).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_cancelled_rescan_leaves_the_earlier_rows_exactly_as_they_were()
    {
        var tasks = new BackgroundTasks();
        var session = new ScanSession(tasks);
        var first = new HeldScan("old-a", "old-b");
        Assert.True(session.Start(first.Run, "first"));
        first.Yielded.Wait(TimeSpan.FromSeconds(30));
        first.Release.Set();
        WaitUntilIdle(session);
        Assert.Equal(["old-a", "old-b"], Ids(session));
        Assert.False(session.WasCancelled);

        // The rescan has found different apps and is held while they are staged, so the cancel arrives mid-scan.
        var second = new HeldScan("new-c");
        Assert.True(session.Start(second.Run, "second"));
        Assert.True(second.Yielded.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(session.Replacing);
        Assert.Equal(1, session.Pending);
        Assert.Equal(["old-a", "old-b"], Ids(session));

        tasks.CancelCurrent();
        WaitUntilIdle(session);

        Assert.False(session.Replacing);
        Assert.Equal(0, session.Pending);
        Assert.True(session.WasCancelled);
        Assert.Equal(["old-a", "old-b"], Ids(session));
    }

    [Fact]
    public void A_finished_rescan_publishes_its_rows_in_place_of_the_earlier_ones()
    {
        var session = new ScanSession(new BackgroundTasks());
        var first = new HeldScan("old-a", "old-b");
        session.Start(first.Run, "first");
        first.Release.Set();
        WaitUntilIdle(session);

        var second = new HeldScan("new-c", "old-b");
        session.Start(second.Run, "second");
        Assert.True(second.Yielded.Wait(TimeSpan.FromSeconds(30)));
        Assert.Equal(["old-a", "old-b"], Ids(session));

        second.Release.Set();
        WaitUntilIdle(session);

        Assert.Equal(["new-c", "old-b"], Ids(session));
        Assert.False(session.WasCancelled);
        Assert.Equal(0, session.Pending);
    }

    [Fact]
    public void A_failed_rescan_drops_its_rows_too()
    {
        var session = new ScanSession(new BackgroundTasks());
        var first = new HeldScan("old-a");
        session.Start(first.Run, "first");
        first.Release.Set();
        WaitUntilIdle(session);

        session.Start(_ => FailingScan(), "second");
        WaitUntilIdle(session);

        Assert.Equal(["old-a"], Ids(session));
        Assert.Equal(BackgroundTaskState.Failed, session.Tasks.Current?.State);

        static IEnumerable<FoundApp> FailingScan()
        {
            yield return Found("new-c");
            throw new IOException("unreadable");
        }
    }

    [Fact]
    public void Page_1_labels_an_imported_steam_game_found_only_when_steam_has_it_installed_as_the_scan_does()
    {
        using var sandbox = new Sandbox();
        var (deck, flow) = NewDevice(sandbox);
        var imported = AppAddCliTests.TestApp.Replace("\"quirks\"", "\"tier\": \"imported\", \"store_ids\": { \"steam\": 424242 }, \"quirks\"", StringComparison.Ordinal);
        AppAddCliTests.ShareDefinition(deck, "testapp", imported);
        // The game's own folder is here, as it is after an uninstall; Steam is here and has no manifest for the game.
        Directory.CreateDirectory(Path.Combine(deck.Home, "native", "data"));
        Directory.CreateDirectory(Path.Combine(deck.Home, ".local", "share", "Steam", "steamapps"));

        Assert.Equal(CatalogStatus.NotFound, flow.Detect("testapp"));

        File.WriteAllText(Path.Combine(deck.Home, ".local", "share", "Steam", "steamapps", "appmanifest_424242.acf"), "x");

        Assert.Equal(CatalogStatus.Found, flow.Detect("testapp"));
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
