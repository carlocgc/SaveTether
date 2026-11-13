using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;

namespace SaveTether.Core.Planning;

/// <summary>One entry in a save folder, as the planner sees it.</summary>
/// <param name="RelativePath">Path below the folder, with forward slashes.</param>
/// <param name="Sha256">
/// Content hash, or null when it was not needed. The reader only hashes a file when the other
/// side has a file at the same path with the same size.
/// </param>
public sealed record FileFact(string RelativePath, EntryKind Kind, long Length, DateTime LastWriteTimeUtc, string? Sha256 = null);

/// <summary>Everything in one save folder. Links are listed, never entered.</summary>
public sealed record TreeFacts(IReadOnlyList<FileFact> Entries)
{
    public static TreeFacts Empty { get; } = new([]);

    /// <summary>
    /// True when the folder holds at least one file or link. Empty subfolders do not count: a
    /// freshly installed app often creates its folder layout before it has any saves.
    /// </summary>
    public bool HasData => Entries.Any(e => e.Kind != EntryKind.Directory);

    public long TotalBytes => Entries.Sum(e => e.Length);

    public DateTime? NewestWriteUtc =>
        Entries.Where(e => e.Kind == EntryKind.File).Select(e => (DateTime?)e.LastWriteTimeUtc).Max();

    /// <summary>The names directly inside the folder, in order.</summary>
    public IReadOnlyList<string> TopLevelNames => EntryNames(1);

    /// <summary>
    /// The units the comparison and "add missing" work in (design 5.13): paths
    /// <paramref name="depth"/> levels down, for example "00050000/101c9500" at depth 2. A file,
    /// a link or an empty folder that sits higher up is an entry of its own; a folder that holds
    /// something is not, since what it holds is.
    /// </summary>
    public IReadOnlyList<string> EntryNames(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        var parents = Entries
            .Where(e => e.RelativePath.Contains('/', StringComparison.Ordinal))
            .Select(e => e.RelativePath[..e.RelativePath.LastIndexOf('/')])
            .ToHashSet(StringComparer.Ordinal);

        return Entries
            .Where(e => SegmentCount(e.RelativePath) >= depth || e.Kind != EntryKind.Directory || !parents.Contains(e.RelativePath))
            .Select(e => EntryOf(e.RelativePath, depth))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public static string TopLevelOf(string relativePath) => EntryOf(relativePath, 1);

    /// <summary>The first <paramref name="depth"/> parts of a path, or all of it when it has fewer.</summary>
    public static string EntryOf(string relativePath, int depth)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var end = -1;
        for (var i = 0; i < depth; i++)
        {
            end = relativePath.IndexOf('/', end + 1);
            if (end < 0)
            {
                return relativePath;
            }
        }

        return relativePath[..end];
    }

    /// <summary>True when <paramref name="relativePath"/> is the entry itself or lies inside it.</summary>
    public static bool IsUnder(string relativePath, string entry, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(entry);
        return relativePath.Length == entry.Length
            ? relativePath.Equals(entry, comparison)
            : relativePath.Length > entry.Length && relativePath[entry.Length] == '/' && relativePath.StartsWith(entry, comparison);
    }

    private static int SegmentCount(string relativePath) => relativePath.Count(c => c == '/') + 1;
}

/// <summary>
/// Everything the planner needs to know to decide what to do for one mapping on this device.
/// The planner only looks at this; it never touches the disk. <see cref="SnapshotReader"/> fills
/// it in from the real system, and tests build it by hand.
/// </summary>
public sealed record Snapshot
{
    public required string MappingId { get; init; }

    public required string DeviceName { get; init; }

    /// <summary>S: the app's save folder on this device, the path that becomes the link.</summary>
    public required string Source { get; init; }

    /// <summary>T: the shared folder for this mapping.</summary>
    public required string Target { get; init; }

    /// <summary>S with links in its parent folders resolved: where it really is.</summary>
    public required string ResolvedSource { get; init; }

    /// <summary>T with every link resolved.</summary>
    public required string ResolvedTarget { get; init; }

    /// <summary>The sync root, resolved.</summary>
    public required string SyncRoot { get; init; }

    /// <summary>&lt;sync root&gt;/savetether, resolved.</summary>
    public required string DataRoot { get; init; }

    /// <summary>The marker folder exists (design 5.3, invariant 5).</summary>
    public bool SyncRootInitialized { get; init; } = true;

    public required EntryKind SourceKind { get; init; }

    /// <summary>When S is a link: where it points, fully resolved.</summary>
    public string? SourceLinkTarget { get; init; }

    public bool SourceParentExists { get; init; } = true;

    /// <summary>S's content when it is a real folder; empty otherwise.</summary>
    public TreeFacts SourceTree { get; init; } = TreeFacts.Empty;

    public required EntryKind TargetKind { get; init; }

    /// <summary>T's content when it is a real folder; empty otherwise.</summary>
    public TreeFacts TargetTree { get; init; } = TreeFacts.Empty;

    /// <summary>How many levels down one game's saves sit: the unit of the comparison and "add missing".</summary>
    public int EntryDepth { get; init; } = 1;

    /// <summary>Seed markers for this mapping that have not been superseded.</summary>
    public IReadOnlyList<SeedMarker> ActiveMarkers { get; init; } = [];

    /// <summary>Files an active marker lists that are not in T. Either not delivered yet, or changed since.</summary>
    public IReadOnlyList<string> MissingSeedFiles { get; init; } = [];

    /// <summary>The process names that were looked for.</summary>
    public IReadOnlyList<string> ProcessNamesChecked { get; init; } = [];

    /// <summary>Those of them that are running.</summary>
    public IReadOnlyList<string> RunningProcesses { get; init; } = [];

    /// <summary>An earlier operation did not finish and must be recovered first.</summary>
    public bool JournalOpen { get; init; }

    public string BackupRoot { get; init; } = string.Empty;

    /// <summary>Null when the backup location is usable; otherwise what is wrong with it.</summary>
    public string? BackupRootProblem { get; init; }

    /// <summary>Free space where backups go, or null when it could not be read.</summary>
    public long? FreeBytesAtBackupRoot { get; init; }

    /// <summary>Free space where T lives, or null when it could not be read.</summary>
    public long? FreeBytesAtTarget { get; init; }

    /// <summary>Free space where S lives, or null when it could not be read. Revert copies T there.</summary>
    public long? FreeBytesAtSource { get; init; }

    /// <summary>False on Windows when T is on a network drive, where a junction cannot point.</summary>
    public bool TargetOnLocalVolume { get; init; } = true;
}
