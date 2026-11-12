namespace SaveTether.Core.FileSystem;

/// <summary>
/// The only code in SaveTether allowed to delete, rename or create links (design 5.3, invariant 1).
/// Every operation looks at an entry without following it first, and never descends a link.
/// A test scans the source tree to keep those calls confined to this folder.
/// </summary>
public sealed class SafeFs
{
    private readonly string? _protectedRoot;

    /// <param name="protectedRoot">
    /// The sync root. Deleting inside it is refused unless explicitly confirmed, and links and
    /// directory renames are never allowed there. Null means nothing is protected.
    /// </param>
    public SafeFs(string? protectedRoot = null)
    {
        _protectedRoot = protectedRoot is null ? null : PathRules.ResolveAll(protectedRoot);
    }

    /// <summary>Says what is at <paramref name="path"/> without following a link.</summary>
    public static EntryKind Probe(string path) => ProbeWithAttributes(path).Kind;

    /// <summary>The absolute target of the link at <paramref name="path"/>. The target need not exist.</summary>
    public static string ReadLinkTarget(string path)
    {
        var (kind, _) = ProbeWithAttributes(path);
        if (kind is not (EntryKind.Symlink or EntryKind.Junction))
        {
            throw new SafeFsException($"Not a link: '{path}' is {Describe(kind)}.");
        }

        var raw = ReadRawLinkTarget(path);
        var parent = Path.GetDirectoryName(PathRules.Normalize(path)) ?? string.Empty;
        return PathRules.Normalize(Path.Combine(parent, raw));
    }

    /// <summary>
    /// Makes <paramref name="link"/> a directory link to <paramref name="target"/>: a junction on
    /// Windows (no privilege needed), a symlink elsewhere. Verifies the result before returning.
    /// </summary>
    public void CreateDirectoryLink(string link, string target)
    {
        var linkPath = RequireAbsolute(link, nameof(link));
        var targetPath = RequireAbsolute(target, nameof(target));

        RefuseInsideProtectedRoot(linkPath, "create a link");
        if (Probe(linkPath) != EntryKind.Missing)
        {
            throw new SafeFsException($"Cannot create a link at '{linkPath}': something is already there.");
        }

        var parent = Path.GetDirectoryName(linkPath);
        if (parent is null || Probe(PathRules.ResolveAll(parent)) != EntryKind.Directory)
        {
            throw new SafeFsException($"Cannot create a link at '{linkPath}': its parent folder does not exist.");
        }

        if (Probe(PathRules.ResolveAll(targetPath)) != EntryKind.Directory)
        {
            throw new SafeFsException($"Cannot link to '{targetPath}': it is not an existing folder.");
        }

        if (OperatingSystem.IsWindows())
        {
            if (targetPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new SafeFsException($"Cannot link to '{targetPath}': a junction needs a target on a local drive.");
            }

            Directory.CreateDirectory(linkPath);
            try
            {
                WindowsReparse.SetJunction(linkPath, targetPath);
            }
            catch
            {
                Directory.Delete(linkPath, recursive: false);
                throw;
            }
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }

        var expectedKind = OperatingSystem.IsWindows() ? EntryKind.Junction : EntryKind.Symlink;
        if (Probe(linkPath) != expectedKind || !PathRules.AreSame(ReadLinkTarget(linkPath), targetPath))
        {
            throw new SafeFsException($"The link at '{linkPath}' did not verify after it was created.");
        }
    }

    /// <summary>Removes the link at <paramref name="path"/> and nothing else. Refuses anything that is not a link.</summary>
    public void RemoveLink(string path)
    {
        var linkPath = RequireAbsolute(path, nameof(path));
        var (kind, attributes) = ProbeWithAttributes(linkPath);
        if (kind is not (EntryKind.Symlink or EntryKind.Junction))
        {
            throw new SafeFsException($"Refusing to remove '{linkPath}' as a link: it is {Describe(kind)}.");
        }

        RefuseInsideProtectedRoot(linkPath, "remove a link");
        Unlink(linkPath, attributes);
    }

    /// <summary>
    /// Deletes the tree at <paramref name="path"/>. Links found inside are removed, never entered,
    /// so nothing outside the tree is touched. A missing path is not an error.
    /// </summary>
    public void DeleteTree(string path, SharedDataDeletion sharedData = SharedDataDeletion.Forbidden)
    {
        var root = RequireAbsolute(path, nameof(path));
        if (sharedData != SharedDataDeletion.ExplicitlyConfirmed)
        {
            RefuseInsideProtectedRoot(root, "delete");
        }

        if (_protectedRoot is not null && PathRules.IsSameOrInside(_protectedRoot, PathRules.ResolveParents(root)))
        {
            throw new SafeFsException($"Refusing to delete '{root}': it contains the sync root.");
        }

        DeleteEntry(root);
    }

    /// <summary>Renames a real directory within its parent folder. Atomic on one filesystem.</summary>
    public void RenameDirectory(string from, string to)
    {
        var source = RequireAbsolute(from, nameof(from));
        var destination = RequireAbsolute(to, nameof(to));

        if (!PathRules.AreSame(Path.GetDirectoryName(source) ?? string.Empty, Path.GetDirectoryName(destination) ?? string.Empty))
        {
            throw new SafeFsException($"Refusing to rename '{source}' to '{destination}': they are in different folders.");
        }

        var kind = Probe(source);
        if (kind != EntryKind.Directory)
        {
            throw new SafeFsException($"Refusing to rename '{source}': it is {Describe(kind)}, not a folder.");
        }

        if (Probe(destination) != EntryKind.Missing)
        {
            throw new SafeFsException($"Refusing to rename to '{destination}': something is already there.");
        }

        RefuseInsideProtectedRoot(source, "rename a folder");
        Directory.Move(source, destination);
    }

    /// <summary>
    /// Copies the real directory <paramref name="source"/> to the new path <paramref name="destination"/>.
    /// Links inside the source are handled per <paramref name="links"/> and never followed.
    /// </summary>
    public static CopyResult CopyTree(string source, string destination, LinkCopyPolicy links = LinkCopyPolicy.Recreate)
    {
        var from = RequireAbsolute(source, nameof(source));
        var to = RequireAbsolute(destination, nameof(destination));

        var kind = Probe(from);
        if (kind != EntryKind.Directory)
        {
            throw new SafeFsException($"Cannot copy '{from}': it is {Describe(kind)}, not a folder.");
        }

        if (Probe(to) != EntryKind.Missing)
        {
            throw new SafeFsException($"Cannot copy to '{to}': something is already there.");
        }

        var resolvedFrom = PathRules.ResolveParents(from);
        var resolvedTo = PathRules.ResolveParents(to);
        if (PathRules.IsSameOrInside(resolvedTo, resolvedFrom) || PathRules.IsSameOrInside(resolvedFrom, resolvedTo))
        {
            throw new SafeFsException($"Cannot copy '{from}' to '{to}': one contains the other.");
        }

        var found = new List<LinkRecord>();
        var fileCount = 0;
        long byteCount = 0;
        CopyDirectory(from, to, string.Empty, links, found, ref fileCount, ref byteCount);
        return new CopyResult(fileCount, byteCount, found);
    }

    /// <summary>
    /// Lists everything below <paramref name="root"/> in a stable order. Links are listed, never entered.
    /// </summary>
    public static IEnumerable<TreeEntry> EnumerateTree(string root)
    {
        var start = PathRules.Normalize(root);
        var kind = Probe(start);
        if (kind != EntryKind.Directory)
        {
            throw new SafeFsException($"Cannot list '{start}': it is {Describe(kind)}, not a folder.");
        }

        return Walk(start, string.Empty);
    }

    /// <summary>The ending of the temporary files <see cref="WriteFileAtomically"/> uses. Readers skip them.</summary>
    public const string TemporarySuffix = ".tmp";

    /// <summary>
    /// Writes a small file so that readers, and a sync tool watching the folder, only ever see the
    /// old content or the new, never half of it. The content goes to a hidden temporary file in
    /// the same folder, which is then renamed over <paramref name="path"/>.
    /// </summary>
    public static void WriteFileAtomically(string path, string content)
    {
        var destination = RequireAbsolute(path, nameof(path));
        ArgumentNullException.ThrowIfNull(content);

        var kind = Probe(destination);
        if (kind is not (EntryKind.Missing or EntryKind.File))
        {
            throw new SafeFsException($"Cannot write the file '{destination}': {Describe(kind)} is in the way.");
        }

        var folder = Path.GetDirectoryName(destination)
            ?? throw new SafeFsException($"Cannot write the file '{destination}': it has no parent folder.");
        var temporary = Path.Combine(
            folder, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}{TemporarySuffix}");

        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            if (Probe(temporary) == EntryKind.File)
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    private static IEnumerable<TreeEntry> Walk(string directory, string relative)
    {
        foreach (var path in SortedEntries(directory))
        {
            var entryRelative = relative.Length == 0 ? Path.GetFileName(path) : relative + "/" + Path.GetFileName(path);
            var kind = Probe(path);
            if (kind == EntryKind.File)
            {
                var info = new FileInfo(path);
                yield return new TreeEntry(entryRelative, kind, info.Length, info.LastWriteTimeUtc);
                continue;
            }

            yield return new TreeEntry(entryRelative, kind, 0, default);
            if (kind == EntryKind.Directory)
            {
                foreach (var child in Walk(path, entryRelative))
                {
                    yield return child;
                }
            }
        }
    }

    private static void CopyDirectory(
        string from, string to, string relative, LinkCopyPolicy policy,
        List<LinkRecord> found, ref int fileCount, ref long byteCount)
    {
        Directory.CreateDirectory(to);
        foreach (var path in SortedEntries(from))
        {
            var name = Path.GetFileName(path);
            var entryRelative = relative.Length == 0 ? name : relative + "/" + name;
            var destination = Path.Combine(to, name);
            var (kind, attributes) = ProbeWithAttributes(path);

            switch (kind)
            {
                case EntryKind.File:
                    File.Copy(path, destination, overwrite: false);
                    File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(path));
                    fileCount++;
                    byteCount += new FileInfo(path).Length;
                    break;

                case EntryKind.Directory:
                    CopyDirectory(path, destination, entryRelative, policy, found, ref fileCount, ref byteCount);
                    break;

                case EntryKind.Symlink or EntryKind.Junction:
                    var target = ReadRawLinkTarget(path);
                    found.Add(new LinkRecord(entryRelative, kind, target));
                    if (policy == LinkCopyPolicy.Recreate)
                    {
                        RecreateLink(destination, kind, attributes, target);
                    }

                    break;

                case EntryKind.Missing:
                    break; // Removed while we were copying. Nothing to copy.

                default:
                    throw new SafeFsException($"Cannot copy '{path}': it is {Describe(kind)}.");
            }
        }
    }

    private static void RecreateLink(string destination, EntryKind kind, FileAttributes attributes, string target)
    {
        try
        {
            if (kind == EntryKind.Junction && OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(destination);
                WindowsReparse.SetJunction(destination, target);
            }
            else if (attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.CreateSymbolicLink(destination, target);
            }
            else
            {
                File.CreateSymbolicLink(destination, target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException && e is not SafeFsException)
        {
            throw new SafeFsException($"Could not recreate the link '{destination}' -> '{target}': {e.Message}", e);
        }
    }

    private static void DeleteEntry(string path)
    {
        var (kind, attributes) = ProbeWithAttributes(path);
        switch (kind)
        {
            case EntryKind.Missing:
                return;

            case EntryKind.Symlink or EntryKind.Junction:
                Unlink(path, attributes);
                return;

            case EntryKind.File:
                ClearReadOnly(path, attributes);
                File.Delete(path);
                return;

            case EntryKind.Directory:
                MakeDirectoryDeletable(path, attributes);
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    DeleteEntry(child);
                }

                Directory.Delete(path, recursive: false);
                return;

            default:
                throw new SafeFsException($"Refusing to delete '{path}': it is {Describe(kind)}.");
        }
    }

    /// <summary>Removes a link itself. Never recursive, so it cannot reach the target's content.</summary>
    private static void Unlink(string path, FileAttributes attributes)
    {
        if (OperatingSystem.IsWindows() && attributes.HasFlag(FileAttributes.Directory))
        {
            Directory.Delete(path, recursive: false);
        }
        else
        {
            File.Delete(path);
        }
    }

    private static void ClearReadOnly(string path, FileAttributes attributes)
    {
        // Windows refuses to delete a read-only file. Elsewhere the file's own mode does not matter.
        if (OperatingSystem.IsWindows() && attributes.HasFlag(FileAttributes.ReadOnly))
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void MakeDirectoryDeletable(string path, FileAttributes attributes)
    {
        if (OperatingSystem.IsWindows())
        {
            ClearReadOnly(path, attributes);
            return;
        }

        // Removing entries needs write and search permission on the directory itself.
        // The path was just probed as a real directory, so this cannot follow a link.
        const UnixFileMode needed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var mode = File.GetUnixFileMode(path);
        if ((mode & needed) != needed)
        {
            File.SetUnixFileMode(path, mode | needed);
        }
    }

    private static (EntryKind Kind, FileAttributes Attributes) ProbeWithAttributes(string path)
    {
        var normalized = PathRules.Normalize(path);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(normalized);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (EntryKind.Missing, default);
        }

        if (!attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return (attributes.HasFlag(FileAttributes.Directory) ? EntryKind.Directory : EntryKind.File, attributes);
        }

        if (!OperatingSystem.IsWindows())
        {
            return (EntryKind.Symlink, attributes);
        }

        var info = WindowsReparse.Read(normalized);
        var kind = info.Tag switch
        {
            WindowsReparse.TagSymlink => EntryKind.Symlink,
            WindowsReparse.TagMountPoint when info.Target is { } target
                && !target.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase) => EntryKind.Junction,
            _ => EntryKind.OtherReparsePoint,
        };
        return (kind, attributes);
    }

    /// <summary>The target exactly as the link stores it. May be relative for a symlink.</summary>
    private static string ReadRawLinkTarget(string path)
    {
        var normalized = PathRules.Normalize(path);
        if (OperatingSystem.IsWindows())
        {
            return WindowsReparse.Read(normalized).Target
                ?? throw new SafeFsException($"Could not read the link target of '{normalized}'.");
        }

        return new FileInfo(normalized).LinkTarget
            ?? throw new SafeFsException($"Could not read the link target of '{normalized}'.");
    }

    private void RefuseInsideProtectedRoot(string path, string action)
    {
        if (_protectedRoot is not null && PathRules.IsSameOrInside(PathRules.ResolveParents(path), _protectedRoot))
        {
            throw new SafeFsException($"Refusing to {action} at '{path}': it is inside the sync root '{_protectedRoot}'.");
        }
    }

    private static string RequireAbsolute(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new SafeFsException($"An absolute path is required, but got '{path}'.");
        }

        return PathRules.Normalize(path);
    }

    private static string[] SortedEntries(string directory)
    {
        var entries = Directory.GetFileSystemEntries(directory);
        Array.Sort(entries, StringComparer.Ordinal);
        return entries;
    }

    private static string Describe(EntryKind kind) => kind switch
    {
        EntryKind.Missing => "missing",
        EntryKind.File => "a file",
        EntryKind.Directory => "a folder",
        EntryKind.Symlink => "a symbolic link",
        EntryKind.Junction => "a junction",
        _ => "a reparse point SaveTether does not handle",
    };
}
