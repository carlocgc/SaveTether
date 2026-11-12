using System.Text.Json;
using System.Text.Json.Serialization;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Core.Execution;

/// <summary>How far one step of an unfinished operation got.</summary>
public enum StepStatus
{
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>The step was about to run. It may have run fully, partly, or not at all.</summary>
    [JsonStringEnumMemberName("started")]
    Started,

    [JsonStringEnumMemberName("done")]
    Done,
}

/// <param name="BackupId">For a backup step that finished: the backup it took.</param>
public sealed record JournalStep(StepStatus Status, string? BackupId = null);

/// <summary>
/// Local, never synced: journal.json. It exists only while an operation is running, and holds
/// the whole plan plus how far each step got, so a crash can be recovered from the file alone.
/// </summary>
public sealed record JournalFile
{
    public const int CurrentSchema = 1;

    public required int Schema { get; init; }

    public required string Id { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required string DeviceName { get; init; }

    public required Plan Plan { get; init; }

    /// <summary>One entry per step of <see cref="Plan"/>, in the same order.</summary>
    public required IReadOnlyList<JournalStep> Steps { get; init; }

    /// <summary>The backups taken so far, in order.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> BackupIds => Steps.Select(s => s.BackupId).OfType<string>().ToList();
}

/// <summary>
/// The record of the operation in progress (design 5.3). Each step is written as intent before
/// it runs and as done after, and every write is flushed to disk. At most one operation is open.
/// </summary>
public sealed class Journal
{
    public const string FileName = "journal.json";

    private readonly string _path;

    public Journal(string localFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localFolder);
        _path = Path.Combine(PathRules.Normalize(localFolder), FileName);
    }

    public static Journal For(LocalStore local)
    {
        ArgumentNullException.ThrowIfNull(local);
        return new Journal(local.Folder);
    }

    /// <summary>True while an operation is unfinished. Everything except recovery must then refuse.</summary>
    public bool IsOpen => SafeFs.Probe(_path) != EntryKind.Missing;

    /// <summary>The unfinished operation, or null when there is none.</summary>
    public JournalFile? TryRead()
    {
        var entry = JsonFiles.TryRead(_path, ExecutionJsonContext.Default.JournalFile, JournalFile.CurrentSchema);
        if (entry is not null && (entry.Plan is null || entry.Steps is null || entry.Steps.Count != entry.Plan.Steps.Count))
        {
            throw new StoreException(StoreProblem.Invalid, $"The file '{_path}' is not valid: its steps do not match its plan.");
        }

        return entry;
    }

    internal JournalFile Begin(Plan plan, string deviceName, DateTimeOffset now)
    {
        if (IsOpen)
        {
            throw new ExecutionException(Planner.UnfinishedMessage);
        }

        var entry = new JournalFile
        {
            Schema = JournalFile.CurrentSchema,
            Id = Guid.NewGuid().ToString("N"),
            StartedUtc = now,
            DeviceName = deviceName,
            Plan = plan,
            Steps = plan.Steps.Select(_ => new JournalStep(StepStatus.Pending)).ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Write(entry);
        return entry;
    }

    internal JournalFile Mark(JournalFile entry, int index, StepStatus status, string? backupId = null)
    {
        var steps = entry.Steps.ToList();
        steps[index] = new JournalStep(status, backupId);
        var updated = entry with { Steps = steps };
        Write(updated);
        return updated;
    }

    internal void Close() => new SafeFs().DeleteTree(_path);

    private void Write(JournalFile entry) => SafeFs.WriteFileAtomically(
        _path, JsonSerializer.Serialize(entry, ExecutionJsonContext.Default.JournalFile) + "\n", flushToDisk: true);
}

/// <summary>An operation could not be carried out as planned.</summary>
public sealed class ExecutionException : IOException
{
    public ExecutionException()
    {
    }

    public ExecutionException(string message)
        : base(message)
    {
    }

    public ExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(JournalFile))]
internal sealed partial class ExecutionJsonContext : JsonSerializerContext;
