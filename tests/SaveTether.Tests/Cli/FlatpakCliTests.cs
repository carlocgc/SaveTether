using SaveTether.Cli;
using SaveTether.Core.Planning;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>The Flatpak precheck through "apply", with the app's permissions supplied by the test.</summary>
public class FlatpakCliTests
{
    private const string AppId = "org.ppsspp.PPSSPP";

    private sealed class FakeFlatpak(string? filesystems) : IFlatpakInfo
    {
        public string? Filesystems { get; set; } = filesystems;

        public string? ShowPermissions(string appId) =>
            appId == AppId && Filesystems is { } value ? $"[Context]\nshared=network;\nfilesystems={value}\n" : null;
    }

    /// <summary>A device whose PPSSPP save folder is inside the Flatpak app's folder, with one save in it.</summary>
    private static (CliDevice Deck, FakeFlatpak Flatpak, string Source) FlatpakDevice(Sandbox sandbox, string? filesystems)
    {
        var deck = new CliDevice(sandbox, "deck");
        var flatpak = new FakeFlatpak(filesystems);
        deck.Flatpak = new FlatpakCheck(flatpak, deck.Home, _ => null);
        var source = Path.Combine(deck.Home, ".var", "app", AppId, "config", "ppsspp", "PSP", "SAVEDATA");
        CliDevice.Write(source, "ULUS10000/DATA.BIN", "save");
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--create-root");
        deck.Ok("map", "add", CliDevice.MappingId, "--target", "ppsspp/SAVEDATA", "--source", source);
        return (deck, flatpak, source);
    }

    [Fact]
    public void Apply_is_refused_with_the_override_command_when_the_sandbox_cannot_write_the_shared_folder()
    {
        using var sandbox = new Sandbox();
        var (deck, _, source) = FlatpakDevice(sandbox, "host:ro;");

        var refusal = deck.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy", "--json").Operation().GetProperty("refusal");

        var failure = refusal.GetProperty("failures").EnumerateArray().Single(f => f.GetProperty("kind").GetString() == "sandbox-cannot-reach");
        var dataRoot = Path.Combine(deck.SyncRoot, "savetether");
        Assert.Contains($"flatpak override --user --filesystem={FlatpakCheck.Quote(dataRoot)} {AppId}", failure.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(deck.IsLinked);
        Assert.Equal("save", CliDevice.FilesIn(source)["ULUS10000/DATA.BIN"]);
        sandbox.AssertCanaryIntact();
    }

    [Fact]
    public void After_the_override_apply_goes_ahead()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, source) = FlatpakDevice(sandbox, "host:ro;");
        deck.Expect(CliApp.ExitRefused, "apply", CliDevice.MappingId, "--copy");

        flatpak.Filesystems = $"host:ro;{Path.Combine(deck.SyncRoot, "savetether")};";
        deck.Ok("apply", CliDevice.MappingId, "--copy");

        Assert.True(CliDeviceIsLinked(source));
    }

    [Fact]
    public void Unreadable_permissions_do_not_block_apply_but_are_noted()
    {
        using var sandbox = new Sandbox();
        var (deck, _, source) = FlatpakDevice(sandbox, null);

        var run = deck.Ok("apply", CliDevice.MappingId, "--copy");

        Assert.Contains("its permissions could not be read", run.Out, StringComparison.Ordinal);
        Assert.True(CliDeviceIsLinked(source));
    }

    [Fact]
    public void Revert_does_not_check_the_sandbox()
    {
        using var sandbox = new Sandbox();
        var (deck, flatpak, source) = FlatpakDevice(sandbox, "home;");
        flatpak.Filesystems = $"{Path.Combine(deck.SyncRoot, "savetether")};";
        deck.Ok("apply", CliDevice.MappingId, "--copy");

        flatpak.Filesystems = "host:ro;";
        deck.Ok("revert", CliDevice.MappingId);

        Assert.False(CliDeviceIsLinked(source));
    }

    private static bool CliDeviceIsLinked(string path) =>
        SaveTether.Core.FileSystem.SafeFs.Probe(path) is SaveTether.Core.FileSystem.EntryKind.Symlink or SaveTether.Core.FileSystem.EntryKind.Junction;
}
