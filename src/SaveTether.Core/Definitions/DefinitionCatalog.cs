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
}

/// <summary>A definition found by ID, or why none could be used.</summary>
public sealed record CatalogEntry(AppDefinition? Definition, DefinitionSource Source, string Location, IReadOnlyList<DefinitionError> Errors)
{
    public bool IsValid => Definition is not null;
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

    private readonly string? _sharedFolder;

    /// <param name="sharedFolder">The shared definitions folder, or null when there is none (no sync root yet).</param>
    public DefinitionCatalog(string? sharedFolder)
    {
        _sharedFolder = sharedFolder;
    }

    /// <summary>IDs of the definitions built into this SaveTether, sorted.</summary>
    public static IReadOnlyList<string> BuiltInIds { get; } = typeof(DefinitionCatalog).Assembly
        .GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(AppDefinition.FileExtension, StringComparison.Ordinal))
        .Select(name => name[ResourcePrefix.Length..^AppDefinition.FileExtension.Length])
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>Every ID that can be added: built-in and shared, sorted, each once.</summary>
    public IReadOnlyList<string> AvailableIds()
    {
        var ids = new SortedSet<string>(BuiltInIds, StringComparer.Ordinal);
        if (_sharedFolder is not null && Directory.Exists(_sharedFolder))
        {
            foreach (var file in Directory.EnumerateFiles(_sharedFolder, "*" + AppDefinition.FileExtension))
            {
                ids.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return [.. ids];
    }

    /// <summary>The definition with this ID: the shared one if there is a file for it, else the built-in one; null when neither exists.</summary>
    public CatalogEntry? Find(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        var fileName = appId + AppDefinition.FileExtension;

        if (_sharedFolder is not null)
        {
            var shared = Path.Combine(_sharedFolder, fileName);
            if (File.Exists(shared))
            {
                var loaded = DefinitionLoader.LoadFile(shared);
                return new CatalogEntry(loaded.Definition, DefinitionSource.Shared, shared, loaded.Errors);
            }
        }

        using var stream = OpenBuiltIn(appId);
        if (stream is null)
        {
            return null;
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
