using System.Text.Json;
using System.Text.RegularExpressions;
using SaveTether.Core.Definitions;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Definitions;

public partial class DefinitionLoaderTests
{
    /// <summary>A small valid definition. Line numbers in the tests below count from its first line.</summary>
    private const string Valid = """
        {
          // A comment, which plain JSON would not allow.
          "schema": 1,
          "id": "cemu",
          "name": "Cemu",
          "kind": "emulator",
          "installs": [
            { "id": "win", "os": "windows", "detect": { "dir": "{install_dir}/mlc01" }, "base": "{install_dir}/mlc01" },
            { "id": "linux", "os": "linux", "detect": { "dir": "{xdg_data}/Cemu/mlc01" }, "base": "{xdg_data}/Cemu/mlc01" },
          ],
          "roles": [
            { "id": "save", "class": "save", "default": true, "path": "usr/save", "target": "save", "entry_depth": 2 },
          ],
          "quirks": ["Link usr/save, not mlc01."],
        }
        """;

    [GeneratedRegex(@"```jsonc\r?\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsoncBlock();

    [Fact]
    public void Every_definition_shown_in_the_design_loads()
    {
        var design = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "design", "save-redirection-design.md"));
        var blocks = JsoncBlock().Matches(design).Select(m => m.Groups[1].Value).ToList();

        var loaded = blocks.Select((text, i) => DefinitionLoader.Parse(text, $"design-example-{i + 1}.jsonc")).ToList();

        Assert.All(loaded, result => Assert.True(result.IsValid, string.Join("\n", result.Errors)));

        // The abbreviated shape (its id is the word "string"), then the five worked examples.
        Assert.Equal(
            ["string", "cemu", "eden", "ppsspp", "retroarch", "final-fantasy-ix"],
            loaded.Select(result => result.Definition!.Id));
    }

    [Fact]
    public void A_definition_is_read_into_installs_and_roles()
    {
        var definition = Load(Valid);

        Assert.Equal("cemu", definition.Id);
        Assert.Equal("Cemu", definition.Name);
        Assert.Equal(AppKind.Emulator, definition.Kind);
        Assert.Equal(["win", "linux"], definition.Installs.Select(i => i.Id));
        Assert.Equal([TargetOs.Windows], definition.Installs[0].Os);
        Assert.Equal("{install_dir}/mlc01", definition.Installs[0].Detect.Dir);
        Assert.Equal("{xdg_data}/Cemu/mlc01", definition.Installs[1].Base.Path);

        var role = Assert.Single(definition.Roles);
        Assert.Equal(RoleClass.Save, role.Class);
        Assert.True(role.Default);
        Assert.Equal(2, role.EntryDepth);
        Assert.Equal(new Dictionary<string, string> { ["win"] = "usr/save", ["linux"] = "usr/save" }, role.Paths);
        Assert.Equal("cemu.save", role.MappingIdFor(definition.Id));
        Assert.Equal("cemu/save", role.MappingTargetFor(definition.Id));
        Assert.Equal(["Link usr/save, not mlc01."], definition.Quirks);
    }

    [Fact]
    public void The_optional_and_either_or_forms_are_all_read()
    {
        var definition = Load("""
            {
              "schema": 1, "id": "ff9", "name": "FF IX", "kind": "game",
              "installs": [
                { "id": "steam", "os": ["windows", "linux"], "detect": { "steam": 377840 },
                  "base": { "winpath": "{locallow}/SquareEnix", "prefix": { "steam": 377840 } } },
                { "id": "gog", "os": "windows", "detect": { "file": "{install_dir}/FF9.exe" }, "base": "{documents}/FF9" },
                { "id": "flatpak", "os": "linux", "detect": { "flatpak": "org.example.FF9" },
                  "base": { "winpath": "{documents}/FF9", "prefix": { "lutris": "ff9" } } }
              ],
              "roles": [
                { "id": "saves", "class": "save", "default": true, "target": "saves",
                  "path": { "steam": "FINAL FANTASY IX", "gog": "Saves" },
                  "ignore": ["*.tmp"], "notes": "Only two of the three installs have this." },
                { "id": "shots", "class": "media", "default": false, "path": "Screens", "target": "screens" }
              ]
            }
            """);

        Assert.Equal([TargetOs.Windows, TargetOs.Linux], definition.Installs[0].Os);
        Assert.Equal(377840, definition.Installs[0].Detect.Steam);
        Assert.Equal("{locallow}/SquareEnix", definition.Installs[0].Base.WinPath);
        Assert.Equal(377840, definition.Installs[0].Base.Prefix!.Steam);
        Assert.Null(definition.Installs[0].Base.Path);
        Assert.Equal("{install_dir}/FF9.exe", definition.Installs[1].Detect.File);
        Assert.Equal("org.example.FF9", definition.Installs[2].Detect.Flatpak);
        Assert.Equal("ff9", definition.Installs[2].Base.Prefix!.Lutris);

        // A path per install: an install that is not named does not have the role.
        Assert.Equal(new Dictionary<string, string> { ["steam"] = "FINAL FANTASY IX", ["gog"] = "Saves" }, definition.Roles[0].Paths);
        Assert.Equal(["*.tmp"], definition.Roles[0].Ignore);
        Assert.Equal("Only two of the three installs have this.", definition.Roles[0].Notes);
        Assert.Equal(1, definition.Roles[1].EntryDepth);
        Assert.Equal(3, definition.Roles[1].Paths.Count);
        Assert.Empty(definition.Quirks);
    }

    [Theory]
    // Wrong or missing values, each on the line it is on in the Valid text.
    [InlineData("\"schema\": 1,", "\"schema\": \"1\",", 3, "\"schema\" must be a whole number")]
    [InlineData("\"schema\": 1,", "\"schema\": 0,", 3, "\"schema\" must be 1 or more")]
    [InlineData("\"schema\": 1,", "", 1, "\"schema\" is missing")]
    [InlineData("\"id\": \"cemu\",", "\"id\": \"Cemu\",", 4, "\"id\" must be lowercase letters")]
    [InlineData("\"id\": \"cemu\",", "\"id\": \"cemu.wiiu\",", 4, "\"id\" must be lowercase letters")]
    [InlineData("\"name\": \"Cemu\",", "\"name\": \"  \",", 5, "\"name\" is empty")]
    [InlineData("\"kind\": \"emulator\",", "\"kind\": \"console\",", 6, "\"kind\" must be one of \"emulator\", \"game\", but is \"console\"")]
    [InlineData("\"kind\": \"emulator\",", "\"kind\": true,", 6, "\"kind\" must be text, but is true or false")]
    [InlineData("\"kind\": \"emulator\",", "\"kind\": \"emulator\", \"homepage\": \"x\",", 6, "unknown field \"homepage\"")]
    [InlineData("\"os\": \"windows\"", "\"os\": \"macos\"", 8, "\"os\" must be one of \"windows\", \"linux\"")]
    [InlineData("\"os\": \"windows\"", "\"os\": []", 8, "\"os\" must name at least one")]
    [InlineData("\"os\": \"windows\"", "\"os\": [\"linux\", \"linux\"]", 8, "names the same system twice")]
    [InlineData("\"id\": \"linux\"", "\"id\": \"win\"", 9, "two installs have the id \"win\"")]
    [InlineData("{ \"dir\": \"{install_dir}/mlc01\" }", "{ \"dir\": \"a\", \"file\": \"b\" }", 8, "\"detect\" must have exactly one of")]
    [InlineData("{ \"dir\": \"{install_dir}/mlc01\" }", "{ }", 8, "\"detect\" must have exactly one of")]
    [InlineData("{ \"dir\": \"{install_dir}/mlc01\" }", "{ \"steam\": 0 }", 8, "\"steam\" must be greater than 0")]
    [InlineData("{ \"dir\": \"{install_dir}/mlc01\" }", "{ \"steam\": 1.5 }", 8, "\"steam\" must be a whole number, but is 1.5")]
    [InlineData("{ \"dir\": \"{install_dir}/mlc01\" }", "{ \"registry\": \"x\" }", 8, "unknown field \"registry\"")]
    [InlineData("\"base\": \"{install_dir}/mlc01\" }", "\"base\": 7 }", 8, "\"base\" must be a path, or an object")]
    [InlineData("\"base\": \"{install_dir}/mlc01\" }", "\"base\": { \"prefix\": { \"steam\": 1 } } }", 8, "\"winpath\" is missing")]
    [InlineData("\"base\": \"{install_dir}/mlc01\" }", "\"base\": { \"winpath\": \"x\", \"prefix\": 3 } }", 8, "\"prefix\" must be an object")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"tier\": \"official\",", 14, "\"tier\" must be one of \"verified\", \"imported\", but is \"official\"")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"origin\": \" \",", 14, "\"origin\" is empty")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"store_ids\": { \"steam\": 0 },", 14, "\"steam\" must be greater than 0")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"store_ids\": { \"gog\": 5 },", 14, "unknown field \"gog\"")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"store_ids\": [1],", 14, "\"store_ids\" must be an object")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"cloud_saves\": [],", 14, "\"cloud_saves\" is empty: leave it out")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"cloud_saves\": [\"itch\"],", 14, "must be one of \"steam\", \"gog\", \"epic\", \"xbox\", \"ubisoft\", \"ea\", but is \"itch\"")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"cloud_saves\": [\"gog\", \"gog\"],", 14, "names \"gog\" twice")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"cloud_saves\": \"steam\",", 14, "\"cloud_saves\" must be a list")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"tested_on\": [4],", 14, "an entry of \"tested_on\" must be text")]
    [InlineData("\"base\": \"{install_dir}/mlc01\" }", "\"base\": { \"winpath\": \"x\", \"prefix\": { \"steam\": 1, \"lutris\": \"y\" } } }", 8, "\"prefix\" must have exactly one of")]
    [InlineData("\"base\": \"{install_dir}/mlc01\" }", "}", 8, "\"base\" is missing")]
    [InlineData("\"class\": \"save\"", "\"class\": \"saves\"", 12, "\"class\" must be one of")]
    [InlineData("\"default\": true", "\"default\": \"yes\"", 12, "\"default\" must be true or false, but is text")]
    [InlineData("\"path\": \"usr/save\"", "\"path\": \"/usr/save\"", 12, "it must be relative to the install's base")]
    [InlineData("\"path\": \"usr/save\"", "\"path\": \"C:/usr/save\"", 12, "it must be relative to the install's base")]
    [InlineData("\"path\": \"usr/save\"", "\"path\": \"usr\\\\save\"", 12, "it must use '/'")]
    [InlineData("\"path\": \"usr/save\"", "\"path\": \"usr/../save\"", 12, "it must not contain '.' or '..' parts")]
    [InlineData("\"path\": \"usr/save\"", "\"path\": \"usr//save\"", 12, "it has an empty part")]
    [InlineData("\"path\": \"usr/save\"", "\"path\": { \"mac\": \"usr/save\" }", 12, "names the install \"mac\", which this definition does not have")]
    [InlineData("\"path\": \"usr/save\"", "\"path\": { }", 12, "\"path\" names no install")]
    [InlineData("\"target\": \"save\"", "\"target\": \"usr/save\"", 12, "\"target\" must be one folder name")]
    [InlineData("\"target\": \"save\"", "\"target\": \".savetether\"", 12, "\"target\" must be one folder name")]
    [InlineData("\"target\": \"save\"", "\"target\": \"CON\"", 12, "\"target\" must be one folder name")]
    [InlineData("\"entry_depth\": 2", "\"entry_depth\": 0", 12, "\"entry_depth\" must be from 1 to 8")]
    [InlineData("\"entry_depth\": 2", "\"entry_depth\": 9", 12, "\"entry_depth\" must be from 1 to 8")]
    [InlineData("\"entry_depth\": 2", "\"entry_depth\": 2, \"mechanism\": \"link\"", 12, "unknown field \"mechanism\"")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"quirks\": \"one\",", 14, "\"quirks\" must be a list, but is text")]
    [InlineData("\"quirks\": [\"Link usr/save, not mlc01.\"],", "\"quirks\": [3],", 14, "an entry of \"quirks\" must be text")]
    public void A_mistake_is_reported_with_its_line_and_a_message_that_names_it(string find, string replace, int line, string message)
    {
        Assert.Contains(find, Valid, StringComparison.Ordinal);
        var result = DefinitionLoader.Parse(Valid.Replace(find, replace, StringComparison.Ordinal), "cemu.jsonc");

        Assert.Null(result.Definition);
        var error = Assert.Single(result.Errors);
        Assert.Equal("cemu.jsonc", error.File);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(line, error.Line);
        Assert.StartsWith($"cemu.jsonc({line}): ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_optional_root_fields_are_read_and_default_to_verified_and_empty()
    {
        var plain = Load(Valid);
        Assert.Equal(DefinitionTier.Verified, plain.Tier);
        Assert.Null(plain.Origin);
        Assert.Null(plain.StoreIds);
        Assert.Empty(plain.CloudSaves);
        Assert.Empty(plain.TestedOn);

        var full = Load(Valid.Replace(
            "\"quirks\": [\"Link usr/save, not mlc01.\"],",
            "\"tier\": \"imported\", \"origin\": \"ludusavi\", \"store_ids\": { \"steam\": 377840 },\n  \"cloud_saves\": [\"steam\", \"epic\"], \"tested_on\": [\"Steam Deck\", \"Windows 11\"],",
            StringComparison.Ordinal));
        Assert.Equal(DefinitionTier.Imported, full.Tier);
        Assert.Equal("ludusavi", full.Origin);
        Assert.Equal(377840, full.StoreIds!.Steam);
        Assert.Equal(["steam", "epic"], full.CloudSaves);
        Assert.Equal(["Steam Deck", "Windows 11"], full.TestedOn);

        Assert.Null(Load(Valid.Replace("\"quirks\"", "\"store_ids\": { }, \"quirks\"", StringComparison.Ordinal)).StoreIds);
    }

    [Fact]
    public void A_winpath_may_leave_the_prefix_out()
    {
        var definition = Load("""
            {
              "schema": 1, "id": "vn", "name": "A Novel", "kind": "game",
              "installs": [ { "id": "win", "os": ["windows", "linux"], "detect": { "dir": "{install_dir}" },
                              "base": { "winpath": "{appdata}/Novel" } } ],
              "roles": [ { "id": "saves", "class": "save", "default": true, "path": "saves", "target": "saves" } ]
            }
            """);

        var location = definition.Installs[0].Base;
        Assert.Equal("{appdata}/Novel", location.WinPath);
        Assert.Null(location.Prefix);
    }

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

    private static string Line(string id, string extra = "") =>
        "{\"schema\":1,\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"kind\":\"game\",\"tier\":\"imported\",\"origin\":\"ludusavi\"," + extra +
        "\"installs\":[{\"id\":\"win\",\"os\":\"windows\",\"detect\":{\"dir\":\"{appdata}/x\"},\"base\":\"{appdata}/x\"}]," +
        "\"roles\":[{\"id\":\"saves\",\"class\":\"save\",\"default\":true,\"path\":\"s\",\"target\":\"saves\"}]}";

    [Fact]
    public void A_bundle_gives_one_definition_per_line()
    {
        using var stream = Bundle(Line("alpha"), "", Line("beta", "\"store_ids\":{\"steam\":7},"));

        var (definitions, errors) = DefinitionLoader.ParseBundle(stream);

        Assert.Empty(errors);
        Assert.Equal(["alpha", "beta"], definitions.Select(d => d.Id));
        Assert.All(definitions, d => Assert.Equal(DefinitionTier.Imported, d.Tier));
        Assert.Equal(7, definitions[1].StoreIds!.Steam);
    }

    [Fact]
    public void A_bad_line_in_a_bundle_is_reported_by_its_line_and_the_others_still_load()
    {
        using var stream = Bundle(Line("alpha"), Line("Bad Id"), "not json", Line("alpha"), Line("gamma"));

        var (definitions, errors) = DefinitionLoader.ParseBundle(stream);

        Assert.Equal(["alpha", "gamma"], definitions.Select(d => d.Id));
        Assert.Equal([2, 3, 4], errors.Select(e => e.Line));
        Assert.All(errors, e => Assert.Equal("bundle", e.File));
        Assert.Contains("\"id\" must be lowercase letters", errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("another line already has the id \"alpha\"", errors[2].Message, StringComparison.Ordinal);
        Assert.StartsWith("bundle(2): ", errors[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_bundle_is_reported_once()
    {
        using var whole = Bundle(Line("alpha"), Line("beta"), Line("gamma"));
        var bytes = whole.ToArray();

        foreach (var keep in new[] { bytes.Length - 1, bytes.Length - 9, bytes.Length / 2 })
        {
            using var cut = new MemoryStream(bytes[..keep]);
            var (_, errors) = DefinitionLoader.ParseBundle(cut);
            Assert.Contains(errors, e => e.Message.Contains("ends early", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_bundle_with_invalid_utf8_is_an_error_not_a_garbled_definition()
    {
        var bytes = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(bytes, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(Line("alpha") + "\n"));
            gzip.Write([0xFF, 0xFE, (byte)'\n']);
        }

        bytes.Position = 0;
        var (_, errors) = DefinitionLoader.ParseBundle(bytes);

        Assert.Contains(errors, e => e.Message.Contains("not valid UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_bundle_has_nothing_and_no_errors_and_text_that_is_not_gzip_is_one_error()
    {
        using var empty = Bundle();
        var (none, noErrors) = DefinitionLoader.ParseBundle(empty);
        Assert.Empty(none);
        Assert.Empty(noErrors);

        using var notGzip = new MemoryStream("plain text, not a bundle"u8.ToArray());
        var (nothing, errors) = DefinitionLoader.ParseBundle(notGzip);
        Assert.Empty(nothing);
        Assert.Contains("not readable gzip", Assert.Single(errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lists_that_must_have_something_in_them_say_so()
    {
        static string Only(string text) => Assert.Single(DefinitionLoader.Parse(text, "x.jsonc").Errors).Message;

        Assert.Contains(
            "\"installs\" is empty",
            Only("""{ "schema": 1, "id": "x", "name": "X", "kind": "game", "installs": [], "roles": [ { "id": "s", "class": "save", "default": true, "path": "a", "target": "s" } ] }"""),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"roles\" is empty",
            Only("""{ "schema": 1, "id": "x", "name": "X", "kind": "game", "installs": [ { "id": "w", "os": "windows", "detect": { "dir": "a" }, "base": "a" } ], "roles": [] }"""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Two_roles_may_not_share_an_id_or_a_target_even_in_different_case()
    {
        var twoRoles = Valid.Replace(
            "{ \"id\": \"save\", \"class\": \"save\", \"default\": true, \"path\": \"usr/save\", \"target\": \"save\", \"entry_depth\": 2 },",
            "{ \"id\": \"save\", \"class\": \"save\", \"default\": true, \"path\": \"usr/save\", \"target\": \"save\" },\n    ROLE2",
            StringComparison.Ordinal);
        string[] Errors(string role2) =>
            DefinitionLoader.Parse(twoRoles.Replace("ROLE2", role2, StringComparison.Ordinal), "cemu.jsonc").Errors.Select(e => e.ToString()).ToArray();

        Assert.Equal(
            ["cemu.jsonc(13): two roles have the id \"save\""],
            Errors("{ \"id\": \"save\", \"class\": \"state\", \"default\": false, \"path\": \"usr/states\", \"target\": \"states\" }"));
        Assert.Equal(
            ["cemu.jsonc(13): two roles share the target \"SAVE\" (letter case is ignored)"],
            Errors("{ \"id\": \"other\", \"class\": \"state\", \"default\": false, \"path\": \"usr/states\", \"target\": \"SAVE\" }"));
        Assert.Empty(Errors("{ \"id\": \"other\", \"class\": \"state\", \"default\": false, \"path\": \"usr/states\", \"target\": \"states\" }"));
    }

    [Fact]
    public void Every_mistake_in_a_file_is_reported_at_once_in_line_order()
    {
        var text = Valid
            .Replace("\"kind\": \"emulator\",", "\"kind\": \"console\",", StringComparison.Ordinal)
            .Replace("\"name\": \"Cemu\",", "\"nmae\": \"Cemu\",", StringComparison.Ordinal)
            .Replace("\"target\": \"save\"", "\"target\": \"a/b\"", StringComparison.Ordinal)
            .Replace("\"os\": \"linux\"", "\"os\": \"bsd\"", StringComparison.Ordinal);

        var result = DefinitionLoader.Parse(text, "cemu.jsonc");

        Assert.Null(result.Definition);
        Assert.Equal([1, 5, 6, 9, 12], result.Errors.Select(e => e.Line));
        Assert.Contains("\"name\" is missing", result.Errors[0].Message, StringComparison.Ordinal);
        Assert.Contains("unknown field \"nmae\"", result.Errors[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", 1, "the file is empty")]
    [InlineData("   \n  ", 1, "the file is empty")]
    [InlineData("[1, 2]", 1, "a definition must be an object, but is a list")]
    [InlineData("{\n  \"schema\": 1,\n  \"id\": \"x\"\n  \"name\": \"X\"\n}", 4, "not valid JSON here")]
    [InlineData("{\n  \"schema\": 1,\n  \"id\": \"x\",\n", 4, "not valid JSON here")]
    [InlineData("{ \"schema\": 1 }\n{ }", 2, "there is more after the end")]
    [InlineData("{\n  \"schema\": 1,\n  \"schema\": 1\n}", 3, "the field \"schema\" is given twice")]
    public void Text_that_is_not_a_definition_at_all_is_refused_with_the_line(string text, int line, string message)
    {
        var error = Assert.Single(DefinitionLoader.Parse(text, "x.jsonc").Errors);

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public void A_definition_in_a_newer_format_is_refused_with_only_that_said()
    {
        var newer = Valid
            .Replace("\"schema\": 1,", "\"schema\": 2,", StringComparison.Ordinal)
            .Replace("\"kind\": \"emulator\",", "\"kind\": \"emulator\", \"added_later\": [1],", StringComparison.Ordinal);

        var error = Assert.Single(DefinitionLoader.Parse(newer, "cemu.jsonc").Errors);

        Assert.Contains("format 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("Update SaveTether", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_must_be_named_after_its_app_and_a_folder_gives_the_good_ones_and_the_reasons_for_the_bad()
    {
        using var sandbox = new Sandbox();
        sandbox.FileAt(Valid, "definitions", "cemu.jsonc");
        sandbox.FileAt(Valid, "definitions", "wiiu.jsonc");
        sandbox.FileAt(Valid.Replace("\"cemu\"", "\"eden\"", StringComparison.Ordinal).Replace("\"Cemu\"", "42", StringComparison.Ordinal), "definitions", "eden.jsonc");
        sandbox.FileAt(Valid, "definitions", ".cemu.jsonc.tmp.jsonc");
        sandbox.FileAt("{ }", "definitions", "definition.schema.json");
        sandbox.FileAt(Valid, "definitions", "nested", "cemu.jsonc");

        Assert.True(DefinitionLoader.LoadFile(sandbox.At("definitions", "cemu.jsonc")).IsValid);
        Assert.Contains("does not exist", Assert.Single(DefinitionLoader.LoadFile(sandbox.At("definitions", "none.jsonc")).Errors).Message, StringComparison.Ordinal);

        var (definitions, errors) = DefinitionLoader.LoadFolder(sandbox.At("definitions"));

        Assert.Equal(["cemu"], definitions.Select(d => d.Id));
        Assert.Equal(
            [
                "eden.jsonc(5): \"name\" must be text, but is a number",
                "wiiu.jsonc(1): the file is for app \"cemu\", so it must be named 'cemu.jsonc'",
            ],
            errors.Select(e => e.ToString()));

        var (none, noErrors) = DefinitionLoader.LoadFolder(sandbox.At("no-such-folder"));
        Assert.Empty(none);
        Assert.Empty(noErrors);
    }

    /// <summary>
    /// The shipped Cemu definition names the portable, Documents, Flatpak and native installs; the Documents and
    /// Flatpak ones come from the Ludusavi manifest.
    /// </summary>
    [Fact]
    public void The_shipped_cemu_definition_has_its_five_installs()
    {
        var definition = DefinitionLoader.LoadFile(Path.Combine(FindRepositoryRoot(), "definitions", "cemu.jsonc")).Definition!;

        Assert.Equal(["win-portable", "win-documents", "linux-flatpak", "linux-native", "linux-portable"], definition.Installs.Select(i => i.Id));
        Assert.Equal("{install_dir}/portable/mlc01", definition.Installs.Single(i => i.Id == "linux-portable").Base.Path);
        Assert.Equal("{documents}/mlc01", definition.Installs.Single(i => i.Id == "win-documents").Base.Path);
        Assert.Equal("{flatpak:info.cemu.Cemu}/data/Cemu/mlc01", definition.Installs.Single(i => i.Id == "linux-flatpak").Base.Path);
        Assert.All(definition.Roles, r => Assert.True(definition.Installs.All(i => r.Paths.ContainsKey(i.Id))));
    }

    /// <summary>
    /// definitions/definition.schema.json is for editors, and is written by hand beside the loader.
    /// This keeps the two from drifting apart on what fields and values exist.
    /// </summary>
    [Fact]
    public void The_json_schema_file_names_the_same_fields_and_values_as_the_loader()
    {
        var path = Path.Combine(FindRepositoryRoot(), "definitions", "definition.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(path));
        var root = schema.RootElement;
        var defs = root.GetProperty("$defs");
        var install = defs.GetProperty("install");
        var role = defs.GetProperty("role");
        var baseObject = install.GetProperty("properties").GetProperty("base").GetProperty("oneOf")[1];

        static string[] Fields(JsonElement objectSchema) =>
            objectSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        static string[] Values(JsonElement enumSchema) =>
            enumSchema.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).ToArray();

        Assert.Equal(DefinitionLoader.RootFields, Fields(root));
        Assert.Equal(DefinitionLoader.InstallFields, Fields(install));
        Assert.Equal(DefinitionLoader.DetectFields, Fields(install.GetProperty("properties").GetProperty("detect")));
        Assert.Equal(DefinitionLoader.BaseFields, Fields(baseObject));
        Assert.Equal(DefinitionLoader.PrefixFields, Fields(baseObject.GetProperty("properties").GetProperty("prefix")));
        Assert.Equal(DefinitionLoader.RoleFields, Fields(role));
        Assert.Equal(DefinitionLoader.StoreIdFields, Fields(root.GetProperty("properties").GetProperty("store_ids")));
        Assert.Equal(["winpath"], baseObject.GetProperty("required").EnumerateArray().Select(v => v.GetString()!));

        Assert.Equal(DefinitionLoader.Kinds.Keys, Values(root.GetProperty("properties").GetProperty("kind")));
        Assert.Equal(DefinitionLoader.Systems.Keys, Values(defs.GetProperty("os")));
        Assert.Equal(DefinitionLoader.Classes.Keys, Values(role.GetProperty("properties").GetProperty("class")));
        Assert.Equal(DefinitionLoader.Tiers.Keys, Values(root.GetProperty("properties").GetProperty("tier")));
        Assert.Equal(DefinitionLoader.CloudStores, root.GetProperty("properties").GetProperty("cloud_saves").GetProperty("items").GetProperty("enum").EnumerateArray().Select(v => v.GetString()!));

        Assert.Equal(AppDefinition.CurrentSchema, root.GetProperty("properties").GetProperty("schema").GetProperty("const").GetInt32());
        Assert.Equal(8, role.GetProperty("properties").GetProperty("entry_depth").GetProperty("maximum").GetInt32());
        Assert.All(new[] { root, install, role, baseObject }, o => Assert.False(o.GetProperty("additionalProperties").GetBoolean()));
    }

    private static AppDefinition Load(string text)
    {
        var result = DefinitionLoader.Parse(text, "test.jsonc");
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        return result.Definition!;
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaveTether.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find SaveTether.slnx above the test folder.");
    }
}
