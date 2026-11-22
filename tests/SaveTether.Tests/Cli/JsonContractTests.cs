using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaveTether.Cli;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;
using SaveTether.Tests.Planning;

namespace SaveTether.Tests.Cli;

/// <summary>
/// The <c>--json</c> output is a contract, version 1 (docs/cli-json.md): scripts, agents and the
/// Decky plugin depend on it. Every command's output is reduced to its shape, each property path
/// with its type, and compared with <c>json-shapes.txt</c>, which is the contract's record.
/// A path that disappears or changes type is a breaking change and fails. A new path is allowed,
/// since version 1 may grow, but fails until it is recorded and documented.
/// </summary>
public class JsonContractTests
{
    private const string Id = CliDevice.MappingId;

    /// <summary>Set to 1 to record new paths. It never removes or changes a recorded one.</summary>
    private const string UpdateVariable = "SAVETETHER_UPDATE_SHAPES";

    [Fact]
    public void Json_output_of_every_command_keeps_the_version_1_contract()
    {
        var actual = RecordShapes();
        var path = ContractPath();
        var stored = JsonShape.Parse(File.ReadAllText(path));

        var broken = new List<string>();
        foreach (var (name, lines) in stored.Sections)
        {
            if (!actual.TryGetValue(name, out var now))
            {
                broken.Add($"## {name}: no longer recorded");
                continue;
            }

            broken.AddRange(lines.Where(line => !now.Contains(line)).Select(line => $"## {name}: {line}"));
        }

        var added = actual
            .SelectMany(pair => pair.Value
                .Where(line => !stored.Sections.TryGetValue(pair.Key, out var old) || !old.Contains(line))
                .Select(line => $"## {pair.Key}: {line}"))
            .ToList();

        Assert.True(
            broken.Count == 0,
            "Breaking change to the --json contract, version 1 (docs/cli-json.md). These recorded paths are gone or changed type:\n" +
            string.Join('\n', broken) + "\n" +
            "Version 1 only grows. If a test scenario changed and no output did, edit json-shapes.txt by hand and say why in the PR.");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, JsonShape.Format(stored.Header, actual));
            return;
        }

        Assert.True(
            added.Count == 0,
            "New --json output that is not in the contract yet. Adding is allowed: document it in docs/cli-json.md, " +
            $"then run the tests once with {UpdateVariable}=1 and commit '{path}'. New paths:\n" + string.Join('\n', added));
    }

    [Fact]
    public void Every_recorded_field_is_documented()
    {
        var doc = File.ReadAllText(DocPath());
        var names = JsonShape.Parse(File.ReadAllText(ContractPath())).Sections.Values
            .SelectMany(lines => lines)
            .Select(line => line[..line.IndexOf(": ", StringComparison.Ordinal)])
            .Select(path => path.Replace("[]", string.Empty, StringComparison.Ordinal))
            .Select(path => path[(path.LastIndexOf('.') + 1)..])
            .Where(name => name != "$")
            .ToHashSet(StringComparer.Ordinal);

        var missing = names.Where(name => !doc.Contains($"`{name}`", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Fields in json-shapes.txt that docs/cli-json.md does not name: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_enum_value_the_output_can_hold_is_documented()
    {
        var doc = File.ReadAllText(DocPath());
        var missing = EnumsReachableFrom(typeof(CliOutput))
            .SelectMany(type => ValuesOf(type).Select(value => (type, value)))
            .Where(pair => !doc.Contains($"`{pair.value}`", StringComparison.Ordinal))
            .Select(pair => $"{pair.type.Name}.{pair.value}")
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Values --json can print that docs/cli-json.md does not list. A new value is allowed in version 1, but must be documented: " +
            string.Join(", ", missing));
    }

    [Fact]
    public void The_contract_lists_every_exit_code()
    {
        var doc = File.ReadAllText(DocPath());
        var codes = typeof(CliApp).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.StartsWith("Exit", StringComparison.Ordinal))
            .Select(f => (int)f.GetRawConstantValue()!);
        Assert.All(codes, code => Assert.Contains($"| {code} |", doc, StringComparison.Ordinal));
    }

    private static SortedDictionary<string, SortedSet<string>> RecordShapes()
    {
        using var sandbox = new Sandbox();
        var shapes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void RecordOn(CliDevice device, string name, int exit, params string[] args) =>
            shapes[name] = JsonShape.Of(device.Expect(exit, [.. args, "--json"]).Json());

        var pc = new CliDevice(sandbox, "pc");
        void Record(string name, int exit, params string[] args) => RecordOn(pc, name, exit, args);

        Record("init", 0, "init", "--sync-root", pc.SyncRoot, "--device", "pc", "--create-root");
        Record("map-add", 0, "map", "add", Id, "--target", "ppsspp/SAVEDATA", "--source", pc.Source, "--process", "PPSSPPSDL");
        Record("map-add-conflict", 1, "map", "add", "other.saves", "--target", "other/saves", "--source", pc.Source);
        pc.Save("GAME1/save.bin", "mine");
        pc.Save("GAME2/save.bin", "only-here");
        Record("status", 0, "status");
        Record("apply-refused-choice", 1, "apply", Id);
        pc.Running.Add("PPSSPPSDL");
        Record("apply-refused-precondition", 1, "apply", Id, "--copy");
        pc.Running.Clear();
        Record("apply-dry-run", 0, "apply", Id, "--copy", "--dry-run");
        Record("apply-completed", 0, "apply", Id, "--copy");
        Record("apply-all", 0, "apply", "--all");
        Record("device-list", 0, "device", "list");

        var aside = sandbox.At("pc", "sync-unplugged");
        Directory.Move(pc.SyncRoot, aside);
        Record("status-sync-root-unavailable", 0, "status");
        Directory.Move(aside, pc.SyncRoot);

        pc.Autostart = new Startup.AutostartTests.Fixed("/gone/savetether-gui");
        Record("status-autostart-missing", 0, "status");
        pc.Autostart = null;

        Record("backup-list", 0, "backup", "list");
        Record("revert", 0, "revert", Id);
        CliDevice.Write(pc.Source, "GAME1/save.bin", "changed");
        Record("apply-refused-guard", 1, "apply", Id);
        var backupId = pc.Ok("backup", "list", "--json").Json().GetProperty("backups")[0].GetProperty("id").GetString()!;
        Record("backup-restore", 0, "backup", "restore", backupId);
        Record("backup-delete", 0, "backup", "delete", backupId, "--confirm-delete");
        Record("backup-cleanup", 0, "backup", "delete", "--all", "--confirm-delete");
        Record("recover", 0, "recover");

        // An install laid out by hand, so the recording does not depend on the system: uninstall removes the files
        // and leaves one that is not SaveTether's, which gives both lists entries.
        var installed = Core.Startup.InstallLayout.For(sandbox.Dir("pc", "install-home"), null);
        Directory.CreateDirectory(installed.AppFolder);
        Directory.CreateDirectory(installed.BinFolder);
        File.WriteAllText(installed.AppPath, "app");
        File.WriteAllText(installed.CliPath, "cli");
        File.WriteAllText(Path.Combine(installed.AppFolder, "notes.txt"), "the owner's");
        pc.Install = installed;
        Record("uninstall", 0, "uninstall", "--confirm-uninstall");
        Record("uninstall-not-installed", 1, "uninstall", "--confirm-uninstall");
        pc.Install = null;

        Record("map-remove", 0, "map", "remove", Id);
        Record("refused", 1, "revert", Id);
        using (OperationLock.Acquire(new LocalStore(pc.LocalFolder), "gui", "apply"))
        {
            Record("operation-running", 1, "apply", Id);
        }

        Record("usage-error", 64, "apply");

        // Unused now (this device forgot its folder, and no other device has one): its shared record and folder go.
        Record("map-delete", 0, "map", "delete", Id, "--confirm-delete");

        // "app add" and "app list", on a second device with a shared definition.
        var deck = new CliDevice(sandbox, "deck");
        deck.Ok("init", "--sync-root", deck.SyncRoot, "--device", "deck", "--create-root");
        AppAddCliTests.ShareDefinition(deck, "testapp", AppAddCliTests.TestApp);
        RecordOn(deck, "app-list", 0, "app", "list");
        RecordOn(deck, "app-add-not-installed", 1, "app", "add", "testapp");
        var install = Path.Combine(deck.Home, "portable");
        CliDevice.Write(install, "app.cfg", "config");
        CliDevice.Write(install, "saves/slot1.sav", "save");
        RecordOn(deck, "app-add-dry-run", 0, "app", "add", "testapp", "--install-dir", install, "--dry-run");
        RecordOn(deck, "app-add", 0, "app", "add", "testapp", "--install-dir", install);
        deck.Ok("apply", "testapp.saves", "--copy");
        RecordOn(deck, "app-list-found", 0, "app", "list", "--found", "--search", "testapp");
        var moved = Path.Combine(deck.Home, "moved");
        CliDevice.Write(moved, "app.cfg", "config");
        RecordOn(deck, "app-add-conflict", 1, "app", "add", "testapp", "--install-dir", moved);
        RecordOn(deck, "device-rename", 0, "device", "rename", "deck2");

        // Not enough space cannot be staged on a real drive, so the planner's refusal is
        // written the way the CLI writes it.
        var shortOfSpace = Planner.Plan(Snap.WithLocalSaves() with { FreeBytesAtTarget = 1 }, new ApplyRequest(SetupChoice.Copy));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(shortOfSpace, CliJsonContext.Default.PlanOutcome));
        shapes["planner-refusal-not-enough-space"] = JsonShape.Of(document.RootElement);

        return shapes;
    }

    /// <summary>Every enum type a <c>--json</c> object can hold, found by walking the types it is written from.</summary>
    private static List<Type> EnumsReachableFrom(Type root)
    {
        var seen = new HashSet<Type>();
        var enums = new List<Type>();

        void Visit(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!seen.Add(type) || type == typeof(Note) || type == typeof(string))
            {
                return;
            }

            if (type.IsEnum)
            {
                enums.Add(type);
                return;
            }

            if (type.IsArray)
            {
                Visit(type.GetElementType()!);
                return;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Visit(argument);
                }

                return;
            }

            if (type.Namespace?.StartsWith("SaveTether", StringComparison.Ordinal) != true)
            {
                return;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                {
                    continue;
                }

                Visit(property.PropertyType);
            }
        }

        Visit(root);
        Assert.Contains(typeof(PrecheckKind), enums);
        return enums;
    }

    private static IEnumerable<string> ValuesOf(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? f.Name);

    private static string ContractPath() => Path.Combine(JsonShape.RepositoryRoot(), "tests", "SaveTether.Tests", "Cli", "json-shapes.txt");

    private static string DocPath() => Path.Combine(JsonShape.RepositoryRoot(), "docs", "cli-json.md");
}

/// <summary>Reduces a JSON document to its property paths and value types, and reads and writes the stored record.</summary>
internal static class JsonShape
{
    public static SortedSet<string> Of(JsonElement root)
    {
        var lines = new SortedSet<string>(StringComparer.Ordinal);
        Walk(root, "$", lines);
        return lines;
    }

    /// <summary>The header (lines before the first section) and each "## name" section's lines.</summary>
    public static (string Header, SortedDictionary<string, SortedSet<string>> Sections) Parse(string text)
    {
        var header = new List<string>();
        var sections = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        SortedSet<string>? current = null;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = sections[line[3..]] = new SortedSet<string>(StringComparer.Ordinal);
            }
            else if (current is not null && line.Length > 0)
            {
                current.Add(line);
            }
            else if (current is null)
            {
                header.Add(line);
            }
        }

        var top = string.Join('\n', header).Trim('\n');
        return (top.Length == 0 ? string.Empty : top + "\n\n", sections);
    }

    public static string Format(string header, SortedDictionary<string, SortedSet<string>> sections) =>
        header + string.Join("\n\n", sections.Select(pair => $"## {pair.Key}\n{string.Join('\n', pair.Value)}")) + "\n";

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaveTether.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find SaveTether.slnx above the test folder.");
    }

    private static void Walk(JsonElement element, string path, SortedSet<string> lines)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                lines.Add($"{path}: object");
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, $"{path}.{property.Name}", lines);
                }

                break;

            case JsonValueKind.Array:
                lines.Add($"{path}: array");
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, path + "[]", lines);
                }

                break;

            case JsonValueKind.True or JsonValueKind.False:
                lines.Add($"{path}: boolean");
                break;

            default:
                lines.Add($"{path}: {element.ValueKind.ToString().ToLowerInvariant()}");
                break;
        }
    }
}
