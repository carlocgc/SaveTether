namespace SaveTether.Core.FileSystem;

/// <summary>What is at a path, judged without following a link.</summary>
public enum EntryKind
{
    /// <summary>Nothing is there.</summary>
    Missing,

    /// <summary>An ordinary file.</summary>
    File,

    /// <summary>An ordinary directory.</summary>
    Directory,

    /// <summary>A symbolic link to a file or directory. It may be broken.</summary>
    Symlink,

    /// <summary>A Windows directory junction.</summary>
    Junction,

    /// <summary>
    /// Any other Windows reparse point, such as a cloud placeholder or a volume mount point.
    /// SaveTether never deletes, copies or descends one.
    /// </summary>
    OtherReparsePoint,
}

/// <summary>Whether a delete may touch save data inside the protected (sync) root.</summary>
public enum SharedDataDeletion
{
    /// <summary>The default. Deleting inside the protected root is refused.</summary>
    Forbidden,

    /// <summary>The owner explicitly confirmed replacing shared data (design 5.3, invariant 2).</summary>
    ExplicitlyConfirmed,
}

/// <summary>What <see cref="SafeFs.CopyTree"/> does with a link found inside the source.</summary>
public enum LinkCopyPolicy
{
    /// <summary>Create an equivalent link in the copy. Fails if the platform refuses.</summary>
    Recreate,

    /// <summary>Do not copy the link; only report it.</summary>
    RecordOnly,
}

/// <summary>One entry found by <see cref="SafeFs.EnumerateTree"/>.</summary>
/// <param name="RelativePath">Path below the tree root, with forward slashes on every platform.</param>
/// <param name="Kind">What the entry is. Links are reported, never entered.</param>
/// <param name="Length">Size in bytes for a file; 0 otherwise.</param>
/// <param name="LastWriteTimeUtc">Modification time for a file; default otherwise.</param>
public sealed record TreeEntry(string RelativePath, EntryKind Kind, long Length, DateTime LastWriteTimeUtc);

/// <summary>A link met while copying a tree.</summary>
/// <param name="RelativePath">Path below the tree root, with forward slashes.</param>
/// <param name="Kind">Symlink or junction.</param>
/// <param name="Target">The target exactly as the link stores it; may be relative.</param>
public sealed record LinkRecord(string RelativePath, EntryKind Kind, string Target);

/// <summary>The outcome of <see cref="SafeFs.CopyTree"/>.</summary>
public sealed record CopyResult(int FileCount, long ByteCount, IReadOnlyList<LinkRecord> Links);

/// <summary>A filesystem operation that SaveTether refused or could not complete safely.</summary>
public sealed class SafeFsException : IOException
{
    public SafeFsException()
    {
    }

    public SafeFsException(string message)
        : base(message)
    {
    }

    public SafeFsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
