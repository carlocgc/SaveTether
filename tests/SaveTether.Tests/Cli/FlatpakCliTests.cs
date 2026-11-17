using SaveTether.Cli;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Planning;
using SaveTether.Tests.FileSystem;
using SaveTether.Tests.Planning;

namespace SaveTether.Tests.Cli;

/// <summary>The Flatpak precheck, and the grant SaveTether may make, through "apply" and "revert".</summary>
public class FlatpakCliTests
{
    private const string AppId = "org.ppsspp.PPSSPP";

    /// <summary>A device whose PPSSPP save folder is inside the Flatpak app's folder, with one save in it.</summary>
    private static (CliDevice Deck, FakeFlatpak Flatpak, string Source) FlatpakDevice(Sandbox sandbox, string? filesystems, string role = "SAVEDATA")
    {
        var deck = new CliDevice(sandbox, "deck");
        var flatpak = new FakeFlatpak(AppId, filesystems);
        deck.Flatpak = new FlatpakCheck(flatpak, deck.Home, _ => null);
        var source = Source(deck, role);
        CliDevice.Write(source, "ULUS10000/DATA.BIN", "save");
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--create-root");
        deck.Ok("map", "add", CliDevice.MappingId, "--target", "ppsspp/" + role, "--source", source, "--process", "PPSSPPSDL");
        return (deck, flatpak, source);
    }

    private static string Source(CliDevice deck, string role) => Path.Combine(deck.Home, ".var", "app", AppId, "config", "ppsspp", "PSP", role);

    private static string DataRoot(CliDevice deck) => Path.Combine(deck.SyncRoot, "savetether");

    private static bool IsLinked(string path) => SafeFs.Probe(path) is EntryKind.Symlink or EntryKind.Junction;

    // ---- Refusing ----

    [Fact]
    public void Without_agreement_apply_is_refused_with_both_ways_to_allow_it_and_nothing_is_granted()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, source) = FlatpakDevice(sandbox, "host:ro");

        var refusal = deck.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy", "--json").Operation().GetProperty("refusal");

        var message = refusal.GetProperty("failures").EnumerateArray()
            .Single(f => f.GetProperty("kind").GetString() == "sandbox-cannot-reach").GetProperty("message").GetString();
        Assert.Contains("--grant-sandbox", message, StringComparison.Ordinal);
        Assert.Contains($"flatpak override --user --filesystem={FlatpakCheck.Quote(DataRoot(deck))} {AppId}", message, StringComparison.Ordinal);
        Assert.Empty(flatpak.OverrideCalls);
        Assert.False(IsLinked(source));
        Assert.Equal("save", CliDevice.FilesIn(source)["ULUS10000/DATA.BIN"]);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void A_dry_run_never_grants()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, _) = FlatpakDevice(sandbox, "host:ro");

        deck.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy", "--grant-sandbox", "--dry-run");

        Assert.Empty(flatpak.OverrideCalls);
    }

    [Fact]
    public void No_grant_is_offered_while_something_else_also_stops_apply()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, _) = FlatpakDevice(sandbox, "host:ro");
        deck.Running.Add("PPSSPPSDL");

        var refusal = deck.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy", "--grant-sandbox", "--json").Operation().GetProperty("refusal");

        Assert.Contains(refusal.GetProperty("failures").EnumerateArray(), f => f.GetProperty("kind").GetString() == "app-running");
        Assert.Empty(flatpak.OverrideCalls);
    }

    // ---- Granting ----

    [Fact]
    public void Grant_sandbox_allows_only_the_savetether_folder_then_applies()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, source) = FlatpakDevice(sandbox, "host:ro");

        var operation = deck.Ok("apply", CliDevice.MappingId, "--copy", "--grant-sandbox", "--json").Operation();

        Assert.Equal(["--filesystem=" + DataRoot(deck)], flatpak.OverrideCalls);
        Assert.Contains($"Allowed the Flatpak app {AppId} to write '{DataRoot(deck)}'.", operation.GetProperty("messages")[0].GetString(), StringComparison.Ordinal);
        Assert.True(IsLinked(source));
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void At_a_terminal_the_grant_is_asked_for_and_a_no_changes_nothing()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, source) = FlatpakDevice(sandbox, "host:ro");

        deck.Typing = new StringReader("no\n");
        var declined = deck.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy");
        Assert.Contains("Allow it?", declined.Out, StringComparison.Ordinal);
        Assert.Empty(flatpak.OverrideCalls);
        Assert.False(IsLinked(source));

        deck.Typing = new StringReader("yes\nyes\n");
        deck.Ok("apply", CliDevice.MappingId, "--copy");
        Assert.Single(flatpak.OverrideCalls);
        Assert.True(IsLinked(source));
    }

    [Fact]
    public void A_failed_grant_is_reported_and_nothing_is_linked()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, source) = FlatpakDevice(sandbox, "host:ro");
        flatpak.OverrideFails = true;

        var run = deck.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy", "--grant-sandbox");

        Assert.Contains("did not succeed", run.Err, StringComparison.Ordinal);
        Assert.False(IsLinked(source));
    }

    [Fact]
    public void Unreadable_permissions_do_not_block_apply_but_are_noted()
    {
        using var sandbox = new Sandbox();
        var (deck, _, source) = FlatpakDevice(sandbox, null);

        var run = deck.Ok("apply", CliDevice.MappingId, "--copy");

        Assert.Contains("its permissions could not be read", run.Out, StringComparison.Ordinal);
        Assert.True(IsLinked(source));
    }

    // ---- Taking the grant back ----

    [Fact]
    public void Revert_offers_to_take_the_grant_back_and_leaves_other_overrides_alone()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, source) = FlatpakDevice(sandbox, "host:ro");
        flatpak.Overrides.Add("xdg-run/gamescope-0:ro");
        deck.Ok("apply", CliDevice.MappingId, "--copy", "--grant-sandbox");

        deck.Typing = new StringReader("yes\n");
        var run = deck.Ok("revert", CliDevice.MappingId);

        Assert.Contains("Take it back?", run.Out, StringComparison.Ordinal);
        Assert.Equal("--nofilesystem=" + DataRoot(deck), flatpak.OverrideCalls[^1]);
        Assert.Contains("xdg-run/gamescope-0:ro", flatpak.Overrides);
        Assert.False(IsLinked(source));
    }

    [Fact]
    public void Without_a_person_revert_only_says_how_to_take_the_grant_back()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, _) = FlatpakDevice(sandbox, "host:ro");
        deck.Ok("apply", CliDevice.MappingId, "--copy", "--grant-sandbox");

        var operation = deck.Ok("revert", CliDevice.MappingId, "--json").Operation();

        Assert.Contains(
            $"flatpak override --user --nofilesystem={FlatpakCheck.Quote(DataRoot(deck))} {AppId}",
            operation.GetProperty("messages").EnumerateArray().Last().GetString(),
            StringComparison.Ordinal);
        Assert.Single(flatpak.OverrideCalls);
    }

    [Fact]
    public void The_grant_is_not_offered_back_while_another_mapping_of_that_app_is_applied()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, _) = FlatpakDevice(sandbox, "host:ro");
        var states = Source(deck, "PPSSPP_STATE");
        CliDevice.Write(states, "state.ppst", "state");
        deck.Ok("map", "add", "ppsspp.states", "--target", "ppsspp/PPSSPP_STATE", "--source", states);
        deck.Ok("apply", CliDevice.MappingId, "--copy", "--grant-sandbox");
        deck.Ok("apply", "ppsspp.states", "--copy");

        var operation = deck.Ok("revert", CliDevice.MappingId, "--json").Operation();

        Assert.DoesNotContain(operation.GetProperty("messages").EnumerateArray(), m => m.GetString()!.Contains("nofilesystem", StringComparison.Ordinal));
        Assert.Single(flatpak.OverrideCalls);
    }

    [Fact]
    public void A_broader_grant_savetether_did_not_make_is_never_offered_back()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, _) = FlatpakDevice(sandbox, "host:ro");
        flatpak.Overrides.Add("host");
        deck.Ok("apply", CliDevice.MappingId, "--copy");

        var operation = deck.Ok("revert", CliDevice.MappingId, "--json").Operation();

        Assert.DoesNotContain(operation.GetProperty("messages").EnumerateArray(), m => m.GetString()!.Contains("nofilesystem", StringComparison.Ordinal));
        Assert.Empty(flatpak.OverrideCalls);
    }
}
