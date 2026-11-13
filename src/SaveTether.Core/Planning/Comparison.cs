using System.Text.Json.Serialization;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Planning;

/// <summary>Where a top-level entry (usually one game's saves) exists, and whether the copies agree.</summary>
public enum EntryPresence
{
    [JsonStringEnumMemberName("only-local")]
    OnlyLocal,

    [JsonStringEnumMemberName("only-shared")]
    OnlyShared,

    [JsonStringEnumMemberName("identical")]
    Identical,

    [JsonStringEnumMemberName("different")]
    Different,
}

/// <summary>How one file differs between this device and the shared copy.</summary>
public enum FileDifferenceKind
{
    [JsonStringEnumMemberName("only-local")]
    OnlyLocal,

    [JsonStringEnumMemberName("only-shared")]
    OnlyShared,

    [JsonStringEnumMemberName("different")]
    Different,
}

/// <summary>One differing file. Sizes are null on the side that does not have it.</summary>
public sealed record FileDifference(string Path, FileDifferenceKind Kind, long? LocalSize, long? SharedSize);

/// <summary>One top-level entry and the files inside it that differ.</summary>
public sealed record EntryComparison(string Name, EntryPresence Presence, IReadOnlyList<FileDifference> Files);

/// <summary>
/// This device's saves against the shared copy (design 5.4): grouped by top-level entry, then by
/// file, with the newest modification time on each side so the owner can see which is more recent.
/// </summary>
public sealed record Comparison(
    IReadOnlyList<EntryComparison> Entries, DateTime? LocalNewestUtc, DateTime? SharedNewestUtc)
{
    /// <summary>Top-level entries this device has that the shared copy lacks: what "add missing" would copy.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> OnlyLocalEntries =>
        Entries.Where(e => e.Presence == EntryPresence.OnlyLocal).Select(e => e.Name).ToList();

    /// <param name="entryDepth">How many levels down one unit sits (design 5.13). 1 means the top-level entries.</param>
    public static Comparison Build(TreeFacts local, TreeFacts shared, int entryDepth = 1)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(shared);

        var localByPath = local.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
        var sharedByPath = shared.Entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        // "Has it" is judged without regard to case: on Windows two names differing only by
        // case are one folder, so adding the local one beside the shared one would collide.
        var localEntries = local.EntryNames(entryDepth);
        var sharedEntries = shared.EntryNames(entryDepth);

        // A folder that is empty on one side and holds entries on the other is not an entry
        // itself: the entries inside it are what is compared.
        var all = localEntries.Union(sharedEntries, StringComparer.Ordinal).ToList();
        var names = all
            .Where(name => !all.Any(other => other.Length > name.Length && TreeFacts.IsUnder(other, name)))
            .Order(StringComparer.Ordinal);

        var entries = new List<EntryComparison>();
        foreach (var name in names)
        {
            var differences = new List<FileDifference>();
            foreach (var path in PathsUnder(name, localByPath.Keys).Union(PathsUnder(name, sharedByPath.Keys), StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                localByPath.TryGetValue(path, out var mine);
                sharedByPath.TryGetValue(path, out var theirs);
                if (Differs(mine, theirs) is { } kind)
                {
                    differences.Add(new FileDifference(path, kind, SizeOf(mine), SizeOf(theirs)));
                }
            }

            var presence =
                !Has(shared, name) ? EntryPresence.OnlyLocal
                : !Has(local, name) ? EntryPresence.OnlyShared
                : differences.Count == 0 ? EntryPresence.Identical
                : EntryPresence.Different;
            entries.Add(new EntryComparison(name, presence, differences));
        }

        return new Comparison(entries, local.NewestWriteUtc, shared.NewestWriteUtc);
    }

    /// <summary>
    /// True when the side has something where the entry would go: the entry itself, anything inside
    /// it, or a file sitting where one of its parent folders would be.
    /// </summary>
    private static bool Has(TreeFacts side, string entry) => side.Entries.Any(e =>
        TreeFacts.IsUnder(e.RelativePath, entry, StringComparison.OrdinalIgnoreCase)
        || (e.Kind != EntryKind.Directory && TreeFacts.IsUnder(entry, e.RelativePath, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> PathsUnder(string entry, IEnumerable<string> paths) =>
        paths.Where(p => TreeFacts.IsUnder(p, entry));

    private static long? SizeOf(FileFact? fact) => fact is { Kind: EntryKind.File } ? fact.Length : null;

    private static FileDifferenceKind? Differs(FileFact? mine, FileFact? theirs)
    {
        // Folders are not differences in themselves; only what is in them.
        var hasMine = mine is not null && mine.Kind != EntryKind.Directory;
        var hasTheirs = theirs is not null && theirs.Kind != EntryKind.Directory;
        if (!hasMine && !hasTheirs)
        {
            return null;
        }

        if (!hasTheirs)
        {
            return FileDifferenceKind.OnlyLocal;
        }

        if (!hasMine)
        {
            return FileDifferenceKind.OnlyShared;
        }

        var same = mine!.Kind == theirs!.Kind
            && mine.Length == theirs.Length
            && mine.Sha256 is not null
            && string.Equals(mine.Sha256, theirs.Sha256, StringComparison.Ordinal);
        return same ? null : FileDifferenceKind.Different;
    }
}
