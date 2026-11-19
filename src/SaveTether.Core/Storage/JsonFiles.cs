using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;

namespace SaveTether.Core.Storage;

/// <summary>Why a SaveTether file could not be read or written.</summary>
public enum StoreProblem
{
    /// <summary>The file or folder is not there.</summary>
    [JsonStringEnumMemberName("not-found")]
    NotFound,

    /// <summary>The file is not valid: broken JSON, a missing or unknown field, or a value that breaks a rule.</summary>
    [JsonStringEnumMemberName("invalid")]
    Invalid,

    /// <summary>The file was written by a newer SaveTether. It is left exactly as it is.</summary>
    [JsonStringEnumMemberName("schema-too-new")]
    SchemaTooNew,

    /// <summary>The sync root does not hold SaveTether's marker folder.</summary>
    [JsonStringEnumMemberName("not-initialized")]
    NotInitialized,

    /// <summary>The change contradicts what is already recorded, such as a different target for an existing mapping.</summary>
    [JsonStringEnumMemberName("conflict")]
    Conflict,

    /// <summary>The device name already belongs to another install.</summary>
    [JsonStringEnumMemberName("name-in-use")]
    NameInUse,

    /// <summary>Another SaveTether process holds the operation lock (<see cref="OperationLock"/>).</summary>
    [JsonStringEnumMemberName("operation-running")]
    OperationRunning,

    /// <summary>
    /// A new sync root was given while mappings on this device are still linked into the old one.
    /// <see cref="StoreException.Next"/> holds a Revert step for each.
    /// </summary>
    [JsonStringEnumMemberName("mappings-applied")]
    MappingsApplied,
}

/// <summary>A SaveTether file could not be read or written. <see cref="Problem"/> says why.</summary>
public sealed class StoreException : IOException
{
    public StoreException()
    {
    }

    public StoreException(string message)
        : base(message)
    {
    }

    public StoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StoreException(StoreProblem problem, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problem = problem;
    }

    /// <param name="message">What is wrong, in words for any front end; <see cref="Exception.Message"/> is its text.</param>
    public StoreException(StoreProblem problem, Note message, Exception? innerException = null)
        : base(message?.Text, innerException)
    {
        ArgumentNullException.ThrowIfNull(message);
        Problem = problem;
        Next = message.Next;
    }

    /// <param name="conflict">The facts behind a <see cref="StoreProblem.Conflict"/>.</param>
    public StoreException(Note message, ConflictFacts conflict)
        : this(StoreProblem.Conflict, message)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        Conflict = conflict;
    }

    public StoreProblem Problem { get; } = StoreProblem.Invalid;

    /// <summary>What the owner can do about it. Each front end words it itself.</summary>
    public IReadOnlyList<NextStep> Next { get; } = [];

    /// <summary>For <see cref="StoreProblem.Conflict"/>: what is in the way, as fields. Null for other problems.</summary>
    public ConflictFacts? Conflict { get; }
}

/// <summary>
/// Reads and writes SaveTether's JSON files under one policy:
/// a file with a newer schema is refused and never rewritten; a file with a known schema must
/// have every required field and no unknown ones, so a typo in a hand-edited file is an error
/// rather than a silently ignored setting.
/// </summary>
internal static class JsonFiles
{
    public static T Read<T>(string path, JsonTypeInfo<T> typeInfo, int currentSchema)
        where T : class
    {
        if (SafeFs.Probe(path) != EntryKind.File)
        {
            throw new StoreException(StoreProblem.NotFound, $"The file '{path}' does not exist.");
        }

        var text = File.ReadAllText(path);
        RequireKnownSchema(path, text, currentSchema);

        try
        {
            return JsonSerializer.Deserialize(text, typeInfo)
                ?? throw new StoreException(StoreProblem.Invalid, $"The file '{path}' is empty.");
        }
        catch (JsonException e)
        {
            throw new StoreException(StoreProblem.Invalid, $"The file '{path}' is not valid: {e.Message}", e);
        }
    }

    public static T? TryRead<T>(string path, JsonTypeInfo<T> typeInfo, int currentSchema)
        where T : class =>
        SafeFs.Probe(path) == EntryKind.File ? Read(path, typeInfo, currentSchema) : null;

    public static void Write<T>(string path, T value, JsonTypeInfo<T> typeInfo, int currentSchema)
        where T : class
    {
        // Never replace a file a newer SaveTether wrote: this version would drop what it does not know.
        if (SafeFs.Probe(path) == EntryKind.File)
        {
            RequireKnownSchema(path, File.ReadAllText(path), currentSchema, tolerateBrokenJson: true);
        }

        // The folder may be missing when the shared folder was set up only in part, or a sync tool skipped an empty folder.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SafeFs.WriteFileAtomically(path, JsonSerializer.Serialize(value, typeInfo) + "\n");
    }

    /// <summary>JSON files in a folder, by name, leaving out hidden and temporary files.</summary>
    public static IReadOnlyList<string> ListJson(string folder)
    {
        if (SafeFs.Probe(folder) != EntryKind.Directory)
        {
            return [];
        }

        return Directory.GetFiles(folder, "*.json")
            .Where(file => !Path.GetFileName(file).StartsWith('.'))
            .Where(file => file.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static void RequireKnownSchema(string path, string text, int currentSchema, bool tolerateBrokenJson = false)
    {
        int schema;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schema", out var element)
                || !element.TryGetInt32(out schema))
            {
                throw new JsonException("it has no whole-number \"schema\" field.");
            }
        }
        catch (JsonException e)
        {
            if (tolerateBrokenJson)
            {
                return;
            }

            throw new StoreException(StoreProblem.Invalid, $"The file '{path}' is not valid: {e.Message}", e);
        }

        if (schema > currentSchema)
        {
            throw new StoreException(
                StoreProblem.SchemaTooNew,
                $"The file '{path}' was written by a newer SaveTether (format {schema}; this version reads up to {currentSchema}). " +
                "Update SaveTether on this device. The file has not been changed.");
        }

        if (schema < 1)
        {
            throw new StoreException(StoreProblem.Invalid, $"The file '{path}' is not valid: its \"schema\" is {schema}.");
        }
    }
}
