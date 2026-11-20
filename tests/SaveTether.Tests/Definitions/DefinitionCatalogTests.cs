using System.Diagnostics;
using SaveTether.Core.Definitions;
using SaveTether.Tests.FileSystem;
using Xunit.Abstractions;

namespace SaveTether.Tests.Definitions;

public sealed class DefinitionCatalogTests(ITestOutputHelper output)
{
    private static MemoryStream Bundle(params string[] lines)
    {
        var bytes = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(bytes, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new StreamWriter(gzip, new System.Text.UTF8Encoding(false)))
        {
            foreach (var line in lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }
        }

        bytes.Position = 0;
        return bytes;
    }

    private static string Line(string id, string name, string extra = "") =>
        "{\"schema\":1,\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"kind\":\"game\",\"tier\":\"imported\",\"origin\":\"ludusavi\"," + extra +
        "\"installs\":[{\"id\":\"win\",\"os\":\"windows\",\"detect\":{\"dir\":\"{appdata}/x\"},\"base\":\"{appdata}/x\"}]," +
        "\"roles\":[{\"id\":\"saves\",\"class\":\"save\",\"default\":true,\"path\":\"s\",\"target\":\"saves\"}]}";

    private static DefinitionCatalog Catalog(string? shared, params string[] lines)
    {
        using var stream = Bundle(lines);
        return new DefinitionCatalog(shared, ImportedSet.FromBundle(stream, DefinitionCatalog.BuiltInIds));
    }

    [Fact]
    public void An_imported_definition_is_found_and_marked_unverified()
    {
        var entry = Catalog(null, Line("alpha", "Alpha Quest")).Find("alpha")!;

        Assert.True(entry.IsValid);
        Assert.Equal(DefinitionSource.Imported, entry.Source);
        Assert.False(entry.IsVerified);
        Assert.Null(new DefinitionCatalog(null).Find("no-such-game-at-all"));
    }

    [Fact]
    public void The_status_lookup_skips_imported_definitions_so_a_manual_mapping_keeps_its_folder_action()
    {
        var catalog = Catalog(null, Line("alpha", "Alpha Quest"));

        Assert.Null(catalog.Find("alpha", includeImported: false));
        Assert.NotNull(catalog.Find("alpha"));
        Assert.NotNull(catalog.Find("cemu", includeImported: false));
    }

    [Fact]
    public void A_verified_built_in_definition_wins_over_an_imported_one_with_the_same_id()
    {
        var catalog = Catalog(null, Line("cemu", "Cemu (imported)"), Line("alpha", "Alpha"));

        var entry = catalog.Find("cemu")!;

        Assert.Equal(DefinitionSource.BuiltIn, entry.Source);
        Assert.True(entry.IsVerified);
        Assert.NotEqual("Cemu (imported)", entry.Definition!.Name);
        Assert.Equal(1, catalog.AvailableIds().Count(id => id == "cemu"));
    }

    [Fact]
    public void A_shared_definition_replaces_an_imported_one_and_is_verified()
    {
        using var sandbox = new Sandbox();
        var folder = sandbox.Dir("defs");
        File.WriteAllText(Path.Combine(folder, "alpha.jsonc"), Line("alpha", "Alpha (mine)").Replace(",\"tier\":\"imported\",\"origin\":\"ludusavi\"", string.Empty, StringComparison.Ordinal));

        var entry = Catalog(folder, Line("alpha", "Alpha (imported)")).Find("alpha")!;

        Assert.Equal(DefinitionSource.Shared, entry.Source);
        Assert.True(entry.IsVerified);
        Assert.Equal("Alpha (mine)", entry.Definition!.Name);
    }

    [Fact]
    public void A_broken_line_is_reported_and_the_rest_load()
    {
        using var stream = Bundle(Line("alpha", "Alpha"), "not json", Line("beta", "Beta"));

        var set = ImportedSet.FromBundle(stream, []);

        Assert.Equal(["alpha", "beta"], set.Definitions.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([2], set.Errors.Select(e => e.Line));
    }

    [Fact]
    public void Search_finds_a_game_by_name_id_or_steam_app_id()
    {
        var catalog = Catalog(null,
            Line("alpha", "Alpha Quest"),
            Line("beta", "Beta", "\"store_ids\":{\"steam\":123},"),
            Line("gamma", "Gamma"));

        Assert.Equal(["alpha"], catalog.Search("quest"));
        Assert.Equal(["gamma"], catalog.Search("GAMMA"));
        Assert.Equal(["beta"], catalog.Search("123"));
        Assert.Contains("cemu", catalog.Search("cemu"));
        Assert.Empty(catalog.Search("zzzzzz"));
        Assert.Equal(catalog.AvailableIds(), catalog.Search("  "));
    }

    [Fact]
    public void The_real_bundle_loads_without_errors_and_its_load_time_is_logged()
    {
        var clock = Stopwatch.StartNew();
        var count = DefinitionCatalog.ImportedCount;
        clock.Stop();

        output.WriteLine($"Imported bundle: {count} definitions in {clock.ElapsedMilliseconds} ms (first use in this process).");
        Assert.Empty(DefinitionCatalog.ImportedErrors);
        Assert.True(count > 1000, "the imported bundle should hold thousands of definitions");
        Assert.DoesNotContain("cemu", new DefinitionCatalog(null).AvailableIds().Where(id => new DefinitionCatalog(null).Find(id)!.Source == DefinitionSource.Imported));
    }
}
