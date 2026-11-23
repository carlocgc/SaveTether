using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;

namespace SaveTether.Core.Definitions;

/// <summary>
/// Reads app definitions: JSON with comments and trailing commas (design 5.2). Every problem is
/// reported with its file and line, and all of them at once, because these files are written by
/// hand. An unknown field is an error, so a mistyped name is caught instead of ignored.
/// It only reads.
/// </summary>
public static class DefinitionLoader
{
    internal static readonly string[] RootFields = ["schema", "id", "name", "kind", "installs", "roles", "quirks", "tier", "origin", "store_ids", "cloud_saves", "tested_on"];
    internal static readonly string[] StoreIdFields = ["steam"];
    internal static readonly string[] InstallFields = ["id", "os", "detect", "base"];
    internal static readonly string[] DetectFields = ["dir", "file", "flatpak", "steam"];
    internal static readonly string[] BaseFields = ["winpath", "prefix"];
    internal static readonly string[] PrefixFields = ["steam", "steam_shortcut", "lutris", "path"];
    internal static readonly string[] RoleFields = ["id", "class", "default", "path", "target", "entry_depth", "ignore", "notes"];

    internal static readonly Dictionary<string, AppKind> Kinds = new(StringComparer.Ordinal)
    {
        ["emulator"] = AppKind.Emulator, ["game"] = AppKind.Game,
    };

    internal static readonly Dictionary<string, TargetOs> Systems = new(StringComparer.Ordinal)
    {
        ["windows"] = TargetOs.Windows, ["linux"] = TargetOs.Linux,
    };

    internal static readonly Dictionary<string, RoleClass> Classes = new(StringComparer.Ordinal)
    {
        ["save"] = RoleClass.Save, ["system"] = RoleClass.System, ["state"] = RoleClass.State,
        ["cache"] = RoleClass.Cache, ["mod"] = RoleClass.Mod, ["media"] = RoleClass.Media,
    };

    /// <summary>Reads one definition file. The file's name must be its app ID plus ".jsonc".</summary>
    public static DefinitionLoadResult LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = Path.GetFileName(path);
        if (SafeFs.Probe(path) != EntryKind.File)
        {
            return new DefinitionLoadResult(null, [new DefinitionError(name, 1, "the file does not exist")]);
        }

        var result = Parse(File.ReadAllText(path), name);
        var expected = result.Definition is { } definition ? definition.Id + AppDefinition.FileExtension : null;
        return expected is not null && !string.Equals(name, expected, StringComparison.Ordinal)
            ? new DefinitionLoadResult(null, [new DefinitionError(name, 1, $"the file is for app \"{result.Definition!.Id}\", so it must be named '{expected}'")])
            : result;
    }

    /// <summary>
    /// Reads every "*.jsonc" file directly inside a folder, by name. Returns the definitions that
    /// are valid and the errors of those that are not: one bad file does not hide the others.
    /// </summary>
    public static (IReadOnlyList<AppDefinition> Definitions, IReadOnlyList<DefinitionError> Errors) LoadFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var definitions = new List<AppDefinition>();
        var errors = new List<DefinitionError>();
        if (SafeFs.Probe(folder) != EntryKind.Directory)
        {
            return (definitions, errors);
        }

        var files = Directory.GetFiles(folder, "*" + AppDefinition.FileExtension)
            .Where(file => !Path.GetFileName(file).StartsWith('.'))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var result = LoadFile(file);
            errors.AddRange(result.Errors);
            if (result.Definition is { } definition)
            {
                definitions.Add(definition);
            }
        }

        return (definitions, errors);
    }

    /// <summary>The name that errors from <see cref="ParseBundle"/> give as their file.</summary>
    public const string BundleName = "bundle";

    internal static readonly Dictionary<string, DefinitionTier> Tiers = new(StringComparer.Ordinal)
    {
        ["verified"] = DefinitionTier.Verified, ["imported"] = DefinitionTier.Imported,
    };

    /// <summary>The stores whose copies of a game can have cloud saves, as "cloud_saves" names them.</summary>
    internal static readonly string[] CloudStores = ["steam", "gog", "epic", "xbox", "ubisoft", "ea"];

    /// <summary>
    /// Reads a bundle: gzip-compressed JSON Lines, one definition per line. A bad line is reported
    /// with its line number in the bundle and the other lines still load. Blank lines are skipped.
    /// </summary>
    public static (IReadOnlyList<AppDefinition> Definitions, IReadOnlyList<DefinitionError> Errors) ParseBundle(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var definitions = new List<AppDefinition>();
        var errors = new List<DefinitionError>();
        var lineNumber = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // A cut-off gzip stream usually ends quietly instead of throwing, so the length the
            // file trailer promises is checked against what came out.
            using var raw = new MemoryStream();
            stream.CopyTo(raw);
            var rawBytes = raw.GetBuffer().AsSpan(0, (int)raw.Length);
            using var text = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(new MemoryStream(raw.GetBuffer(), 0, (int)raw.Length), System.IO.Compression.CompressionMode.Decompress))
            {
                gzip.CopyTo(text);
            }

            if (rawBytes.Length > 0 && (rawBytes.Length < 18 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(rawBytes[^4..]) != (uint)text.Length))
            {
                errors.Add(new DefinitionError(BundleName, 1, "the bundle ends early: it is cut off or damaged"));
                return (definitions, errors);
            }

            text.Position = 0;
            using var reader = new StreamReader(text, new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true));
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var result = Parse(line, BundleName);
                errors.AddRange(result.Errors.Select(e => new DefinitionError(BundleName, lineNumber, e.Message)));
                if (result.Definition is not { } definition)
                {
                    continue;
                }

                if (ids.Add(definition.Id))
                {
                    definitions.Add(definition);
                }
                else
                {
                    errors.Add(new DefinitionError(BundleName, lineNumber, $"another line already has the id \"{definition.Id}\""));
                }
            }
        }
        catch (InvalidDataException e)
        {
            errors.Add(new DefinitionError(BundleName, lineNumber + 1, $"the bundle is not readable gzip: {e.Message}"));
        }
        catch (System.Text.DecoderFallbackException)
        {
            errors.Add(new DefinitionError(BundleName, lineNumber + 1, "the bundle is not valid UTF-8"));
        }

        return (definitions, errors);
    }

    /// <summary>Reads a definition from text. <paramref name="fileName"/> is only used to label errors.</summary>
    public static DefinitionLoadResult Parse(string text, string fileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        JsonTreeNode root;
        try
        {
            root = JsonTree.Parse(text);
        }
        catch (JsonTreeException e)
        {
            return new DefinitionLoadResult(null, [new DefinitionError(fileName, e.Line, e.Message)]);
        }

        var reader = new Reader(fileName);
        var definition = reader.ReadDefinition(root);
        return reader.Errors.Count == 0
            ? new DefinitionLoadResult(definition, [])
            : new DefinitionLoadResult(null, reader.Errors.OrderBy(e => e.Line).ToList());
    }

    /// <summary>Walks the tree, collecting every problem. A value that cannot be read becomes null and reading goes on.</summary>
    private sealed class Reader(string file)
    {
        public List<DefinitionError> Errors { get; } = [];

        public AppDefinition? ReadDefinition(JsonTreeNode root)
        {
            if (!IsObject(root, "a definition", RootFields))
            {
                return null;
            }

            var schema = Integer(Required(root, "schema"), "\"schema\"");
            if (schema > AppDefinition.CurrentSchema)
            {
                // A newer format may mean different things by the same fields. Say only this.
                Errors.Clear();
                Fail(root.Line, $"this definition is in format {schema}, and this SaveTether reads up to format {AppDefinition.CurrentSchema}. Update SaveTether");
                return null;
            }

            if (schema < 1)
            {
                Fail(Field(root, "schema")!.Line, "\"schema\" must be 1 or more");
            }

            var id = Name(Required(root, "id"), "\"id\"");
            var name = Text(Required(root, "name"), "\"name\"");
            var kind = OneOf(Required(root, "kind"), "\"kind\"", Kinds);

            var installs = List(Required(root, "installs"), "\"installs\"", mustHaveItems: true).Select(ReadInstall).ToList();
            RequireDistinct(installs.Select(i => (i?.Id, i?.Line ?? 0)), "install");
            var installIds = installs.Select(i => i?.Id).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

            var roles = List(Required(root, "roles"), "\"roles\"", mustHaveItems: true).Select(node => ReadRole(node, installIds)).ToList();
            RequireDistinct(roles.Select(r => (r?.Role.Id, r?.Line ?? 0)), "role");
            RequireDistinctTargets(roles);

            var quirks = Strings(Field(root, "quirks"), "\"quirks\"");
            var tier = Field(root, "tier") is { } tierNode ? OneOf(tierNode, "\"tier\"", Tiers) ?? DefinitionTier.Verified : DefinitionTier.Verified;
            var origin = Field(root, "origin") is { } originNode ? Text(originNode, "\"origin\"") : null;
            var storeIds = ReadStoreIds(Field(root, "store_ids"));
            var cloudSaves = ReadCloudSaves(Field(root, "cloud_saves"));
            var testedOn = Strings(Field(root, "tested_on"), "\"tested_on\"");

            return Errors.Count > 0
                ? null
                : new AppDefinition(
                    (int)schema!.Value, id!, name!, kind!.Value,
                    installs.Select(i => i!.Value.Install).ToList(), roles.Select(r => r!.Value.Role).ToList(), quirks,
                    tier, origin, storeIds, cloudSaves, testedOn);
        }

        private StoreIds? ReadStoreIds(JsonTreeNode? node)
        {
            if (node is null || !IsObject(node, "\"store_ids\"", StoreIdFields))
            {
                return null;
            }

            var steam = Field(node, "steam") is { } steamNode ? PositiveInteger(steamNode, "\"steam\"") : null;
            return steam is null ? null : new StoreIds(steam);
        }

        private List<string> ReadCloudSaves(JsonTreeNode? node)
        {
            var stores = new List<string>();
            if (node is null)
            {
                return stores;
            }

            var items = List(node, "\"cloud_saves\"", mustHaveItems: false);
            if (node.Items is { Count: 0 })
            {
                Fail(node.Line, "\"cloud_saves\" is empty: leave it out when no store keeps saves in the cloud");
            }

            foreach (var item in items)
            {
                if (OneOfKey(item, "an entry of \"cloud_saves\"", CloudStores) is not { } store)
                {
                    continue;
                }

                if (stores.Contains(store))
                {
                    Fail(item.Line, $"\"cloud_saves\" names \"{store}\" twice");
                }
                else
                {
                    stores.Add(store);
                }
            }

            return stores;
        }

        private (InstallDefinition Install, string Id, int Line)? ReadInstall(JsonTreeNode node)
        {
            if (!IsObject(node, "an install", InstallFields))
            {
                return null;
            }

            var id = Name(Required(node, "id"), "an install's \"id\"");
            var os = ReadOs(Required(node, "os"));
            var detect = ReadDetect(Required(node, "detect"));
            var location = ReadBase(Required(node, "base"));
            return id is null || os is null || detect is null || location is null
                ? id is null ? null : (new InstallDefinition(id, [], new DetectRule(), new BaseLocation()), id, node.Line)
                : (new InstallDefinition(id, os, detect, location), id, node.Line);
        }

        private List<TargetOs>? ReadOs(JsonTreeNode? node)
        {
            if (node is null)
            {
                return null;
            }

            // One system as text, or several as a list.
            var items = node.Kind == NodeKind.Array ? node.Items! : [node];
            if (items.Count == 0)
            {
                Fail(node.Line, "\"os\" must name at least one of \"windows\" and \"linux\"");
                return null;
            }

            var systems = items.Select(item => OneOf(item, "\"os\"", Systems)).ToList();
            if (systems.Any(s => s is null))
            {
                return null;
            }

            if (systems.Distinct().Count() != systems.Count)
            {
                Fail(node.Line, "\"os\" names the same system twice");
            }

            return systems.Select(s => s!.Value).ToList();
        }

        private DetectRule? ReadDetect(JsonTreeNode? node)
        {
            if (node is null || !IsObject(node, "\"detect\"", DetectFields))
            {
                return null;
            }

            if (node.Members!.Count != 1)
            {
                Fail(node.Line, "\"detect\" must have exactly one of \"dir\", \"file\", \"flatpak\" and \"steam\"");
                return null;
            }

            var only = node.Members[0];
            if (!DetectFields.Contains(only.Name, StringComparer.Ordinal))
            {
                return null; // Already reported as an unknown field.
            }

            return only.Name switch
            {
                "dir" => Text(only.Value, "\"dir\"") is { } dir ? new DetectRule(Dir: dir) : null,
                "file" => Text(only.Value, "\"file\"") is { } path ? new DetectRule(File: path) : null,
                "flatpak" => Text(only.Value, "\"flatpak\"") is { } flatpak ? new DetectRule(Flatpak: flatpak) : null,
                _ => PositiveInteger(only.Value, "\"steam\"") is { } steam ? new DetectRule(Steam: steam) : null,
            };
        }

        private BaseLocation? ReadBase(JsonTreeNode? node)
        {
            if (node is null)
            {
                return null;
            }

            if (node.Kind == NodeKind.String)
            {
                return Text(node, "\"base\"") is { } path ? new BaseLocation(Path: path) : null;
            }

            if (node.Kind != NodeKind.Object)
            {
                Fail(node.Line, $"\"base\" must be a path, or an object with \"winpath\" and, optionally, \"prefix\", but is {node.Describe()}");
                return null;
            }

            if (!IsObject(node, "\"base\"", BaseFields))
            {
                return null;
            }

            var winPath = Text(Required(node, "winpath"), "\"winpath\"");
            var prefixNode = Field(node, "prefix");
            if (prefixNode is null)
            {
                // No prefix kind: the prefix set for the device is used (design 5.2).
                return winPath is null ? null : new BaseLocation(WinPath: winPath);
            }

            if (!IsObject(prefixNode, "\"prefix\"", PrefixFields))
            {
                return null;
            }

            if (prefixNode.Members!.Count != 1)
            {
                Fail(prefixNode.Line, "\"prefix\" must have exactly one of \"steam\", \"steam_shortcut\", \"lutris\" and \"path\"");
                return null;
            }

            var only = prefixNode.Members[0];
            if (!PrefixFields.Contains(only.Name, StringComparer.Ordinal))
            {
                return null; // Already reported as an unknown field.
            }

            var prefix = only.Name switch
            {
                "steam" => PositiveInteger(only.Value, "\"steam\"") is { } steam ? new PrefixRule(Steam: steam) : null,
                "steam_shortcut" => Text(only.Value, "\"steam_shortcut\"") is { } shortcut ? new PrefixRule(SteamShortcut: shortcut) : null,
                "lutris" => Text(only.Value, "\"lutris\"") is { } lutris ? new PrefixRule(Lutris: lutris) : null,
                _ => Text(only.Value, "\"path\"") is { } path ? new PrefixRule(Path: path) : null,
            };
            return winPath is null || prefix is null ? null : new BaseLocation(WinPath: winPath, Prefix: prefix);
        }

        private (RoleDefinition Role, int Line)? ReadRole(JsonTreeNode node, List<string> installIds)
        {
            if (!IsObject(node, "a role", RoleFields))
            {
                return null;
            }

            var id = Name(Required(node, "id"), "a role's \"id\"");
            var roleClass = OneOf(Required(node, "class"), "\"class\"", Classes);
            var isDefault = Boolean(Required(node, "default"), "\"default\"");
            var paths = ReadPaths(Required(node, "path"), installIds);
            var target = ReadTarget(Required(node, "target"));

            var depth = 1L;
            if (Field(node, "entry_depth") is { } depthNode)
            {
                depth = Integer(depthNode, "\"entry_depth\"") ?? 1;
                if (depth is < 1 or > MappingFile.MaxEntryDepth)
                {
                    Fail(depthNode.Line, $"\"entry_depth\" must be from 1 to {MappingFile.MaxEntryDepth}");
                }
            }

            var ignore = Strings(Field(node, "ignore"), "\"ignore\"");
            var notes = Field(node, "notes") is { } notesNode ? Text(notesNode, "\"notes\"") : null;

            return id is null || roleClass is null || isDefault is null || paths is null || target is null
                ? id is null ? null : (new RoleDefinition(id, RoleClass.Save, false, new Dictionary<string, string>(), target ?? string.Empty, 1, [], null), node.Line)
                : (new RoleDefinition(id, roleClass.Value, isDefault.Value, paths, target, (int)depth, ignore, notes), node.Line);
        }

        /// <summary>One path for every install, or a path per install ID when installs differ.</summary>
        private Dictionary<string, string>? ReadPaths(JsonTreeNode? node, List<string> installIds)
        {
            if (node is null)
            {
                return null;
            }

            if (node.Kind == NodeKind.String)
            {
                return RelativePath(node, "\"path\"") is { } path ? installIds.ToDictionary(id => id, _ => path, StringComparer.Ordinal) : null;
            }

            if (node.Kind != NodeKind.Object)
            {
                Fail(node.Line, $"\"path\" must be a path, or an object with a path per install, but is {node.Describe()}");
                return null;
            }

            if (node.Members!.Count == 0)
            {
                Fail(node.Line, "\"path\" names no install");
                return null;
            }

            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var ok = true;
            foreach (var member in node.Members)
            {
                if (!installIds.Contains(member.Name, StringComparer.Ordinal))
                {
                    Fail(member.Line, $"\"path\" names the install \"{member.Name}\", which this definition does not have");
                    ok = false;
                }
                else if (RelativePath(member.Value, $"the path for \"{member.Name}\"") is { } path)
                {
                    paths[member.Name] = path;
                }
                else
                {
                    ok = false;
                }
            }

            return ok ? paths : null;
        }

        /// <summary>
        /// A role's target is one folder name. With the app ID in front it becomes the mapping's
        /// target, which must be exactly "app/folder" (design 5.12), so the same rules apply.
        /// </summary>
        private string? ReadTarget(JsonTreeNode? node)
        {
            var target = Text(node, "\"target\"");
            if (target is null)
            {
                return null;
            }

            // Borrow the mapping rule by trying the target under a stand-in app name.
            if (target.Contains('/', StringComparison.Ordinal) || Identifiers.DescribeTargetProblem("app.role", "app/" + target) is not null)
            {
                Fail(
                    node!.Line,
                    $"\"target\" must be one folder name, without '/' and not starting with '.', that Windows also accepts, but is \"{target}\"");
                return null;
            }

            return target;
        }

        private void RequireDistinct(IEnumerable<(string? Id, int Line)> items, string what)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (id, line) in items)
            {
                if (id is not null && !seen.Add(id))
                {
                    Fail(line, $"two {what}s have the id \"{id}\"");
                }
            }
        }

        private void RequireDistinctTargets(List<(RoleDefinition Role, int Line)?> roles)
        {
            // Ignoring case: on Windows two targets differing only by case are one folder.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var role in roles)
            {
                if (role is { Role.Target.Length: > 0 } entry && !seen.Add(entry.Role.Target))
                {
                    Fail(entry.Line, $"two roles share the target \"{entry.Role.Target}\" (letter case is ignored)");
                }
            }
        }

        private bool IsObject(JsonTreeNode node, string what, string[] known)
        {
            if (node.Kind != NodeKind.Object)
            {
                Fail(node.Line, $"{what} must be an object, but is {node.Describe()}");
                return false;
            }

            foreach (var member in node.Members!)
            {
                if (!known.Contains(member.Name, StringComparer.Ordinal))
                {
                    Fail(member.Line, $"unknown field \"{member.Name}\". Known here: {string.Join(", ", known)}");
                }
            }

            return true;
        }

        private static JsonTreeNode? Field(JsonTreeNode parent, string name) =>
            parent.Members!.FirstOrDefault(m => m.Name == name)?.Value;

        private JsonTreeNode? Required(JsonTreeNode parent, string name)
        {
            var node = Field(parent, name);
            if (node is null)
            {
                Fail(parent.Line, $"\"{name}\" is missing");
            }

            return node;
        }

        private string? Text(JsonTreeNode? node, string what)
        {
            if (node is null)
            {
                return null;
            }

            if (node.Kind != NodeKind.String)
            {
                Fail(node.Line, $"{what} must be text, but is {node.Describe()}");
                return null;
            }

            if (string.IsNullOrWhiteSpace(node.Text))
            {
                Fail(node.Line, $"{what} is empty");
                return null;
            }

            return node.Text;
        }

        /// <summary>An ID: it becomes part of a mapping ID and of file names, so the same rule as those.</summary>
        private string? Name(JsonTreeNode? node, string what)
        {
            var text = Text(node, what);
            if (text is not null && (text.Contains('.', StringComparison.Ordinal) || !Identifiers.IsValidMappingId(text + ".x")))
            {
                Fail(node!.Line, $"{what} must be lowercase letters, digits, '-' and '_', starting with a letter or digit, but is \"{text}\"");
                return null;
            }

            return text;
        }

        private string? RelativePath(JsonTreeNode? node, string what)
        {
            var text = Text(node, what);
            if (text is null)
            {
                return null;
            }

            var parts = text.Split('/');
            var bad = text.Contains('\\', StringComparison.Ordinal) ? "it must use '/', not '\\'"
                : text.Contains(':', StringComparison.Ordinal) || text.StartsWith('/') ? "it must be relative to the install's base, not absolute"
                : parts.Any(part => part is ".." or ".") ? "it must not contain '.' or '..' parts"
                : parts.Any(part => part.Length == 0) ? "it has an empty part"
                : null;
            if (bad is not null)
            {
                Fail(node!.Line, $"{what} is \"{text}\": {bad}");
                return null;
            }

            return text;
        }

        private long? Integer(JsonTreeNode? node, string what)
        {
            if (node is null)
            {
                return null;
            }

            if (node.AsInteger is not { } value)
            {
                Fail(node.Line, $"{what} must be a whole number, but is {(node.Kind == NodeKind.Number ? node.Text : node.Describe())}");
                return null;
            }

            return value;
        }

        private long? PositiveInteger(JsonTreeNode node, string what)
        {
            var value = Integer(node, what);
            if (value is <= 0)
            {
                Fail(node.Line, $"{what} must be greater than 0");
                return null;
            }

            return value;
        }

        private bool? Boolean(JsonTreeNode? node, string what)
        {
            if (node is null)
            {
                return null;
            }

            if (node.Kind != NodeKind.Boolean)
            {
                Fail(node.Line, $"{what} must be true or false, but is {node.Describe()}");
                return null;
            }

            return node.Flag;
        }

        private T? OneOf<T>(JsonTreeNode? node, string what, Dictionary<string, T> allowed)
            where T : struct
        {
            var text = OneOfKey(node, what, allowed.Keys);
            return text is null ? null : allowed[text];
        }

        private string? OneOfKey(JsonTreeNode? node, string what, IEnumerable<string> allowed)
        {
            var text = Text(node, what);
            if (text is null)
            {
                return null;
            }

            if (!allowed.Contains(text, StringComparer.Ordinal))
            {
                Fail(node!.Line, $"{what} must be one of {string.Join(", ", allowed.Select(k => $"\"{k}\""))}, but is \"{text}\"");
                return null;
            }

            return text;
        }

        private List<JsonTreeNode> List(JsonTreeNode? node, string what, bool mustHaveItems)
        {
            if (node is null)
            {
                return [];
            }

            if (node.Kind != NodeKind.Array)
            {
                Fail(node.Line, $"{what} must be a list, but is {node.Describe()}");
                return [];
            }

            if (mustHaveItems && node.Items!.Count == 0)
            {
                Fail(node.Line, $"{what} is empty: a definition needs at least one");
            }

            return node.Items!.ToList();
        }

        private List<string> Strings(JsonTreeNode? node, string what) =>
            List(node, what, mustHaveItems: false).Select(item => Text(item, $"an entry of {what}")).OfType<string>().ToList();

        private void Fail(int line, string message) => Errors.Add(new DefinitionError(file, line, message));
    }
}
