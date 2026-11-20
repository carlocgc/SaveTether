using System.Text.Json.Serialization;

namespace SaveTether.Core.Definitions;

/// <summary>Where a definition was read from.</summary>
public enum DefinitionSource
{
    /// <summary>Shipped inside SaveTether, from the repository's definitions folder.</summary>
    [JsonStringEnumMemberName("built-in")]
    BuiltIn,

    /// <summary>The owner's own, in the shared folder: &lt;sync root&gt;/savetether/.savetether/definitions.</summary>
    [JsonStringEnumMemberName("shared")]
    Shared,

    /// <summary>Converted from the Ludusavi manifest and built into SaveTether. Not checked by hand: unverified.</summary>
    [JsonStringEnumMemberName("imported")]
    Imported,
}

/// <summary>A definition found by ID, or why none could be used.</summary>
public sealed record CatalogEntry(AppDefinition? Definition, DefinitionSource Source, string Location, IReadOnlyList<DefinitionError> Errors)
{
    public bool IsValid => Definition is not null;

    /// <summary>False for a definition converted from the Ludusavi manifest; nobody has tried it on a device.</summary>
    public bool IsVerified => Source != DefinitionSource.Imported;
}

/// <summary>The definitions in the imported bundle, by ID, and the lines that could not be read.</summary>
internal sealed record ImportedSet(IReadOnlyDictionary<string, AppDefinition> Definitions, IReadOnlyList<DefinitionError> Errors)
{
    public static ImportedSet Empty { get; } = new(new Dictionary<string, AppDefinition>(), []);

    /// <summary>Reads a bundle. A definition whose ID a built-in one uses is dropped: the verified one wins.</summary>
    public static ImportedSet FromBundle(Stream stream, IReadOnlyCollection<string> verifiedIds)
    {
        var (definitions, errors) = DefinitionLoader.ParseBundle(stream);
        var byId = new Dictionary<string, AppDefinition>(StringComparer.Ordinal);
        var verified = new HashSet<string>(verifiedIds, StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            if (!verified.Contains(definition.Id))
            {
                byId[definition.Id] = definition;
            }
        }

        return new ImportedSet(byId, errors);
    }
}

/// <summary>
/// Finds app definitions: the owner's own in the shared folder first, then those built into
/// SaveTether. A shared file with the same ID replaces the built-in one, so a fix or a new app can
/// be written once and reach every device through the sync tool.
/// </summary>
public sealed class DefinitionCatalog
{
    /// <summary>The folder name below the shared .savetether folder.</summary>
    public const string SharedFolderName = "definitions";

    private const string ResourcePrefix = "definitions/";

    private const string ImportedResource = "definitions/imported/ludusavi.jsonl.gz";

    private static readonly Lazy<ImportedSet> BuiltInImported = new(LoadImported);

    private readonly string? _sharedFolder;
    private readonly Lazy<ImportedSet> _importedSet;

    /// <param name="sharedFolder">The shared definitions folder, or null when there is none (no sync root yet).</param>
    public DefinitionCatalog(string? sharedFolder)
        : this(sharedFolder, BuiltInImported)
    {
    }

    internal DefinitionCatalog(string? sharedFolder, ImportedSet imported)
        : this(sharedFolder, new Lazy<ImportedSet>(imported))
    {
    }

    // Lazy: the bundle takes a moment to read. Callers that can reach it: Find with includeImported
    // (adding an app, app list/search), Search, AvailableIds by default, and the Imported* statics.
    // The status check and the backups list use Find(id, includeImported: false) and never load it.
    private DefinitionCatalog(string? sharedFolder, Lazy<ImportedSet> imported)
    {
        _sharedFolder = sharedFolder;
        _importedSet = imported;
    }

    private ImportedSet _imported => _importedSet.Value;

    /// <summary>Lines of the imported bundle that could not be read, each naming its line. Empty when all loaded.</summary>
    public static IReadOnlyList<DefinitionError> ImportedErrors => BuiltInImported.Value.Errors;

    /// <summary>How many imported definitions this SaveTether has.</summary>
    public static int ImportedCount => BuiltInImported.Value.Definitions.Count;

    private static ImportedSet LoadImported()
    {
        using var stream = typeof(DefinitionCatalog).Assembly.GetManifestResourceStream(ImportedResource);
        return stream is null ? ImportedSet.Empty : ImportedSet.FromBundle(stream, BuiltInIds);
    }

    /// <summary>IDs of the definitions built into this SaveTether, sorted.</summary>
    public static IReadOnlyList<string> BuiltInIds { get; } = typeof(DefinitionCatalog).Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(AppDefinition.FileExtension, StringComparison.Ordinal))
        .Select(name => name[ResourcePrefix.Length..^AppDefinition.FileExtension.Length])
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>Every ID that can be added: shared, built-in and (by default) imported, sorted, each once.</summary>
    public IReadOnlyList<string> AvailableIds(bool includeImported = true)
    {
        var ids = new SortedSet<string>(BuiltInIds, IdComparer);
        if (includeImported)
        {
            ids.UnionWith(_imported.Definitions.Keys);
        }

        ids.UnionWith(SharedFiles().Keys);
        return [.. ids];
    }

    // File names are case-insensitive on Windows, so a shared CEMU.jsonc is the definition for 'cemu' there.
    private static StringComparer IdComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The files in the shared folder by ID, from one directory listing, matched as the host filesystem matches names.</summary>
    private Dictionary<string, string> SharedFiles()
    {
        var ids = new Dictionary<string, string>(IdComparer);
        if (_sharedFolder is not null && Directory.Exists(_sharedFolder))
        {
            foreach (var file in Directory.EnumerateFiles(_sharedFolder, "*" + AppDefinition.FileExtension))
            {
                ids[Path.GetFileNameWithoutExtension(file)] = file;
            }
        }

        return ids;
    }

    /// <summary>
    /// The IDs whose name or ID contains the text (ignoring case), or whose Steam app ID is the text,
    /// sorted. A shared definition is matched by its ID, and by its name once read.
    /// </summary>
    public IReadOnlyList<string> Search(string? text)
    {
        var typed = text?.Trim() ?? string.Empty;
        if (typed.Length == 0)
        {
            return AvailableIds();
        }

        var hits = new List<string>();
        var shared = SharedFiles();
        foreach (var id in AvailableIds())
        {
            if (id.Contains(typed, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(id);
                continue;
            }

            var definition = shared.ContainsKey(id) || BuiltInIds.Contains(id, StringComparer.Ordinal)
                ? FindCore(id, includeImported: true, shared)?.Definition
                : _imported.Definitions.GetValueOrDefault(id);
            if (definition is not null
                && (definition.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)
                    || (definition.StoreIds?.Steam is { } steam && steam.ToString(System.Globalization.CultureInfo.InvariantCulture) == typed)))
            {
                hits.Add(id);
            }
        }

        return hits;
    }

    /// <summary>
    /// The definition with this ID: the shared one if there is a file for it, else the built-in one, else
    /// (unless <paramref name="includeImported"/> is false) the imported one; null when none exists.
    /// The imported bundle is read on the first call that reaches it, so a status check passes false.
    /// </summary>
    public CatalogEntry? Find(string appId, bool includeImported = true) => FindCore(appId, includeImported, null);

    /// <summary>The entries for these IDs (each must exist), probing the shared folder once instead of per ID.</summary>
    public IReadOnlyList<(string Id, CatalogEntry Entry)> FindAll(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var shared = SharedFiles();
        return ids.Select(id => (id, FindCore(id, includeImported: true, shared)!)).ToList();
    }

    private CatalogEntry? FindCore(string appId, bool includeImported, Dictionary<string, string>? knownShared)
    {
        ArgumentNullException.ThrowIfNull(appId);
        var fileName = appId + AppDefinition.FileExtension;

        if (_sharedFolder is not null)
        {
            var shared = Path.Combine(_sharedFolder, fileName);
            if (knownShared is null ? File.Exists(shared) : knownShared.ContainsKey(appId))
            {
                var loaded = DefinitionLoader.LoadFile(shared);
                return new CatalogEntry(loaded.Definition, DefinitionSource.Shared, shared, loaded.Errors);
            }
        }

        using var stream = OpenBuiltIn(appId);
        if (stream is null)
        {
            return includeImported && _imported.Definitions.TryGetValue(appId, out var imported)
                ? new CatalogEntry(imported, DefinitionSource.Imported, "imported " + appId + AppDefinition.FileExtension, [])
                : null;
        }

        using var reader = new StreamReader(stream);
        var builtIn = DefinitionLoader.Parse(reader.ReadToEnd(), fileName);
        return new CatalogEntry(builtIn.Definition, DefinitionSource.BuiltIn, "built-in " + fileName, builtIn.Errors);
    }

    private static Stream? OpenBuiltIn(string appId) =>
        BuiltInIds.Contains(appId, StringComparer.Ordinal)
            ? typeof(DefinitionCatalog).Assembly.GetManifestResourceStream(ResourcePrefix + appId + AppDefinition.FileExtension)
            : null;
}
