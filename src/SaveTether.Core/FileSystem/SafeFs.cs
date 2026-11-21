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

    /// <summary>
    /// Creates a link again from a backup's record of it: the same kind, with the target exactly
    /// as it was stored (which may be relative). Never inside the sync root. Creating a symbolic
    /// link on Windows needs a privilege, so this can fail there; the caller decides what that means.
    /// </summary>
    public void RecreateRecordedLink(string link, EntryKind kind, string rawTarget)
    {
        var linkPath = RequireAbsolute(link, nameof(link));
        ArgumentException.ThrowIfNullOrWhiteSpace(rawTarget);
        if (kind is not (EntryKind.Symlink or EntryKind.Junction))
        {
            throw new SafeFsException($"Cannot recreate '{linkPath}' as a link: {Describe(kind)} is not a kind of link.");
        }

        RefuseInsideProtectedRoot(linkPath, "create a link");
        if (Probe(linkPath) != EntryKind.Missing)
        {
            throw new SafeFsException($"Cannot create a link at '{linkPath}': something is already there.");
        }

        var parent = Path.GetDirectoryName(linkPath);
        if (parent is null || Probe(parent) != EntryKind.Directory)
        {
            throw new SafeFsException($"Cannot create a link at '{linkPath}': its parent is not a real folder.");
        }

        // Windows needs to know whether a symbolic link is to a folder. Judge by what the target is now.
        var target = Path.GetFullPath(Path.Combine(parent, rawTarget));
        var toFolder = kind == EntryKind.Junction || Probe(PathRules.ResolveAll(target)) == EntryKind.Directory;
        RecreateLink(linkPath, kind, toFolder ? FileAttributes.Directory : FileAttributes.Normal, rawTarget);
    }

    /// <summary>
    /// Puts the regular file <paramref name="source"/> in place of <paramref name="destination"/>, which must be a
    /// regular file or nothing: a link or a folder there is refused, never followed. An old file is renamed aside
    /// first, which Windows allows even for a program that is running, and removed when it can be; one still in use
    /// is left as "&lt;name&gt;.old-&lt;id&gt;" for <see cref="RemoveFile"/> later. Used to install SaveTether's own programs.
    /// </summary>
    public void ReplaceFile(string source, string destination)
    {
        var from = RequireAbsolute(source, nameof(source));
        var to = RequireAbsolute(destination, nameof(destination));
        if (Probe(from) != EntryKind.File)
        {
            throw new SafeFsException($"Cannot put '{from}' in place: it is not a file.");
        }

        var existing = Probe(to);
        if (existing is not (EntryKind.Missing or EntryKind.File))
        {
            throw new SafeFsException($"Refusing to replace '{to}': it is {Describe(existing)}, not a file.");
        }

        RefuseInsideProtectedRoot(to, "replace a file");
        string? aside = null;
        if (existing == EntryKind.File)
        {
            aside = $"{to}.old-{Guid.NewGuid():N}";
            File.Move(to, aside);
        }

        File.Move(from, to);
        if (aside is not null)
        {
            try
            {
                File.Delete(aside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still running (Windows): removed at a later install.
            }
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
    /// Removes a Unix domain socket's file, such as one left by a process that was killed. A missing path
    /// is not an error. Refuses anything that could be something else: on Windows the entry must be a
    /// reparse point with the AF_UNIX tag; elsewhere, where .NET shows a socket as an empty file, it must be
    /// an empty entry that is neither a folder nor a link. So no file with content is ever removed here.
    /// </summary>
    public void DeleteSocket(string path)
    {
        var socketPath = RequireAbsolute(path, nameof(path));
        var kind = Probe(socketPath);
        if (kind == EntryKind.Missing)
        {
            return;
        }

        var isSocket = OperatingSystem.IsWindows()
            ? kind == EntryKind.OtherReparsePoint && WindowsReparse.Read(PathRules.Normalize(socketPath)).Tag == WindowsReparse.TagAfUnix
            : kind == EntryKind.File && new FileInfo(socketPath).Length == 0;
        if (!isSocket)
        {
            throw new SafeFsException($"Refusing to remove '{socketPath}' as a socket: it is {Describe(kind)}.");
        }

        RefuseInsideProtectedRoot(socketPath, "remove a socket");
        File.Delete(socketPath);
    }

    /// <summary>
    /// Deletes the regular file at <paramref name="path"/> and nothing else: a folder, a link or anything else
    /// is refused, so a link is never followed to delete what it points to. A missing path is not an error.
    /// Used for files SaveTether wrote itself, such as the autostart entry.
    /// </summary>
    public void RemoveFile(string path)
    {
        var filePath = RequireAbsolute(path, nameof(path));
        var kind = Probe(filePath);
        if (kind == EntryKind.Missing)
        {
            return;
        }

        if (kind != EntryKind.File)
        {
            throw new SafeFsException($"Refusing to delete '{filePath}' as a file: it is {Describe(kind)}.");
        }

        RefuseInsideProtectedRoot(filePath, "delete a file");
        File.Delete(filePath);
    }

    /// <summary>
    /// Deletes one regular file inside the sync root, for a mapping the owner confirmed deleting (design 5.3, invariant 2's third
    /// exception). Every folder from <paramref name="below"/> (the folder the shared data lives in) down to the file's parent
    /// must be a real folder: a link in the way, such as an app folder that is a symlink or junction, would take the delete
    /// somewhere else, so it is refused. A missing file is not an error.
    /// </summary>
    public static void RemoveSharedFile(string below, string path)
    {
        var filePath = RequireAbsolute(path, nameof(path));
        RequirePlainFolders(below, filePath);
        var kind = Probe(filePath);
        if (kind == EntryKind.Missing)
        {
            return;
        }

        if (kind != EntryKind.File)
        {
            throw new SafeFsException($"Refusing to delete '{filePath}' as a file: it is {Describe(kind)}.");
        }

        var attributes = File.GetAttributes(filePath);
        ClearReadOnly(filePath, attributes);
        File.Delete(filePath);
    }

    /// <summary>
    /// Moves one regular file in one rename, for <c>map delete</c>: out of the share to a staging folder, or back into it. The path
    /// that is inside <paramref name="below"/> gets the checks of <see cref="RemoveSharedFile"/> (every folder from
    /// <paramref name="below"/> down must be a real folder); the other is the tool's own staging folder, which it makes.
    /// Nothing is overwritten: a destination that exists is refused. The folders the destination needs are made.
    /// </summary>
    public static void MoveSharedFile(string below, string from, string to)
    {
        var source = RequireAbsolute(from, nameof(from));
        var destination = RequireAbsolute(to, nameof(to));
        if (PathRules.IsSameOrInside(source, PathRules.Normalize(below)))
        {
            RequirePlainFolders(below, source);
        }

        if (Probe(source) != EntryKind.File)
        {
            throw new SafeFsException($"Refusing to move '{source}': it is {Describe(Probe(source))}, not a file.");
        }

        if (Probe(destination) != EntryKind.Missing)
        {
            throw new SafeFsException($"Refusing to move '{source}' to '{destination}': something is already there.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (PathRules.IsSameOrInside(destination, PathRules.Normalize(below)))
        {
            RequirePlainFolders(below, destination);
        }

        File.Move(source, destination);
    }

    /// <summary>
    /// Whether a folder can be made at <paramref name="folder"/> and written in, checked by making it and a small file in it, which is
    /// removed again. False when the system refuses. For choosing where <c>map delete</c> sets files aside.
    /// </summary>
    public static bool CanWriteIn(string folder)
    {
        var path = RequireAbsolute(folder, nameof(folder));
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, "probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a file <c>map delete</c> set aside: with the checks of <see cref="RemoveSharedFile"/> when it is inside the share,
    /// and as a plain file when it is in the tool's staging folder outside it. A link or a folder is refused either way.
    /// </summary>
    public static void RemoveStagedFile(string below, string path)
    {
        var filePath = RequireAbsolute(path, nameof(path));
        if (PathRules.IsSameOrInside(filePath, PathRules.Normalize(below)))
        {
            RemoveSharedFile(below, filePath);
            return;
        }

        var kind = Probe(filePath);
        if (kind == EntryKind.Missing)
        {
            return;
        }

        if (kind != EntryKind.File)
        {
            throw new SafeFsException($"Refusing to delete '{filePath}' as a file: it is {Describe(kind)}.");
        }

        ClearReadOnly(filePath, File.GetAttributes(filePath));
        File.Delete(filePath);
    }

    /// <summary>The empty-folder removal of <see cref="RemoveEmptySharedFolder"/> for a staging folder, inside the share or outside it.</summary>
    public static bool RemoveEmptyStagedFolder(string below, string path)
    {
        var folder = RequireAbsolute(path, nameof(path));
        if (PathRules.IsSameOrInside(folder, PathRules.Normalize(below)))
        {
            return RemoveEmptySharedFolder(below, folder);
        }

        var kind = Probe(folder);
        if (kind == EntryKind.Missing)
        {
            return true;
        }

        if (kind != EntryKind.Directory)
        {
            throw new SafeFsException($"Refusing to remove '{folder}' as a folder: it is {Describe(kind)}.");
        }

        if (Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return false;
        }

        try
        {
            Directory.Delete(folder, recursive: false);
            return true;
        }
        catch (IOException) when (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return false;
        }
    }

    /// <summary>
    /// Removes an empty folder inside the sync root, with the same checks as <see cref="RemoveSharedFile"/>, and never what is in it:
    /// answers false, and leaves the folder, when it has anything in it (a sync tool may have just added something). A missing
    /// folder is not an error.
    /// </summary>
    public static bool RemoveEmptySharedFolder(string below, string path)
    {
        var folder = RequireAbsolute(path, nameof(path));
        RequirePlainFolders(below, folder);
        var kind = Probe(folder);
        if (kind == EntryKind.Missing)
        {
            return true;
        }

        if (kind != EntryKind.Directory)
        {
            throw new SafeFsException($"Refusing to remove '{folder}' as a folder: it is {Describe(kind)}.");
        }

        if (Directory.EnumerateFileSystemEntries(folder).Any())
        {
            return false;
        }

        try
        {
            MakeDirectoryDeletable(folder, File.GetAttributes(folder));
            Directory.Delete(folder, recursive: false);
            return true;
        }
        catch (IOException) when (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            // Something arrived between the look and the removal: it is left alone.
            return false;
        }
    }

    /// <summary>
    /// Requires every folder below <paramref name="below"/>, down to the parent of <paramref name="path"/>, to be a real folder, not
    /// a link or anything else. Lexical on the path as given, each part looked at itself (a link is seen, not followed).
    /// </summary>
    public static void RequirePlainFolders(string below, string path)
    {
        var root = PathRules.Normalize(RequireAbsolute(below, nameof(below)));
        var target = PathRules.Normalize(RequireAbsolute(path, nameof(path)));

        // The folder the shared data lives in must itself be a real folder: a link there would put every child outside the share.
        var rootKind = Probe(root);
        if (rootKind != EntryKind.Directory)
        {
            throw new SafeFsException($"Refusing to delete under '{root}': it is {Describe(rootKind)}, not a plain folder.");
        }

        if (!PathRules.IsSameOrInside(target, root) || PathRules.AreSame(target, root))
        {
            throw new SafeFsException($"Refusing to delete '{target}': it is not inside '{root}'.");
        }

        var folders = new Stack<string>();
        for (var folder = Path.GetDirectoryName(target); folder is not null && !PathRules.AreSame(folder, root); folder = Path.GetDirectoryName(folder))
        {
            folders.Push(folder);
        }

        foreach (var folder in folders)
        {
            var kind = Probe(folder);
            if (kind == EntryKind.Missing)
            {
                // Nothing below a missing folder exists either: there is nothing to follow.
                return;
            }

            if (kind != EntryKind.Directory)
            {
                throw new SafeFsException($"Refusing to delete under '{folder}': it is {Describe(kind)}, not a plain folder inside '{root}'.");
            }
        }
    }

    /// <summary>
    /// Deletes the tree at <paramref name="path"/>. Links found inside are removed, never entered,
    /// so nothing outside the tree is touched. A missing path is not an error.
    /// </summary>
    public void DeleteTree(string path, SharedDataDeletion sharedData = SharedDataDeletion.Forbidden)
    {
        var root = RequireAbsolute(path, nameof(path));
        if (sharedData == SharedDataDeletion.Forbidden)
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
    /// Copies the content of the real directory <paramref name="source"/> into the existing real
    /// directory <paramref name="destination"/>. Folders are merged; an existing file is never
    /// overwritten (the copy fails instead). Links inside the source are never followed.
    /// </summary>
    /// <param name="entries">
    /// What to copy, or null for everything. Each is a path below the source with forward slashes:
    /// a name directly inside it ("GAME1") or deeper ("00050000/101c9500"). A named entry must not
    /// already exist in the destination, in any letter case; its parent folders are created as needed.
    /// </param>
    public static CopyResult CopyInto(
        string source, string destination, IReadOnlyCollection<string>? entries = null,
        LinkCopyPolicy links = LinkCopyPolicy.RecordOnly)
    {
        var from = RequireAbsolute(source, nameof(source));
        var to = RequireAbsolute(destination, nameof(destination));

        var kind = Probe(from);
        if (kind != EntryKind.Directory)
        {
            throw new SafeFsException($"Cannot copy '{from}': it is {Describe(kind)}, not a folder.");
        }

        var destinationKind = Probe(PathRules.ResolveAll(to));
        if (destinationKind != EntryKind.Directory)
        {
            throw new SafeFsException($"Cannot copy into '{to}': it is {Describe(destinationKind)}, not a folder.");
        }

        var resolvedFrom = PathRules.ResolveParents(from);
        var resolvedTo = PathRules.ResolveAll(to);
        if (PathRules.IsSameOrInside(resolvedTo, resolvedFrom) || PathRules.IsSameOrInside(resolvedFrom, resolvedTo))
        {
            throw new SafeFsException($"Cannot copy '{from}' into '{to}': one contains the other.");
        }

        var found = new List<LinkRecord>();
        var fileCount = 0;
        long byteCount = 0;
        if (entries is null)
        {
            CopyDirectory(from, to, string.Empty, links, found, ref fileCount, ref byteCount);
        }
        else
        {
            foreach (var entry in entries.Order(StringComparer.Ordinal))
            {
                var parts = entry.Split('/');
                if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    throw new SafeFsException($"Cannot copy '{entry}': it is not a path below '{from}'.");
                }

                var entrySource = Path.Combine([from, .. parts]);
                var entryDestination = Path.Combine(PlaceFor(to, parts[..^1], entry), parts[^1]);
                if (FindIgnoringCase(Path.GetDirectoryName(entryDestination)!, parts[^1]) is not null)
                {
                    throw new SafeFsException($"Cannot copy '{entry}' into '{to}': it is already there.");
                }

                CopyEntry(entrySource, entryDestination, entry, links, found, ref fileCount, ref byteCount);
            }
        }

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
    /// <param name="flushToDisk">
    /// Also ask the operating system to put the content on the disk before the rename. For the
    /// journal, which must survive a crash or power cut.
    /// </param>
    public static void WriteFileAtomically(string path, string content, bool flushToDisk = false)
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
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
                stream.Flush(flushToDisk);
            }

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

    /// <summary>
    /// The folder inside <paramref name="root"/> that an entry's parent folders lead to, creating
    /// those that are missing. A folder that exists under a name differing only by letter case is
    /// refused where case matters: copying beside it would give two folders that are one on Windows.
    /// </summary>
    private static string PlaceFor(string root, string[] parents, string entry)
    {
        var current = root;
        foreach (var name in parents)
        {
            var existing = FindIgnoringCase(current, name);
            if (existing is not null && existing != name && !OperatingSystem.IsWindows())
            {
                throw new SafeFsException(
                    $"Cannot copy '{entry}' into '{root}': it has '{existing}' where the source has '{name}'. " +
                    "The two names differ only by letter case; rename one by hand.");
            }

            current = Path.Combine(current, existing ?? name);
            var kind = Probe(current);
            if (kind == EntryKind.Missing)
            {
                Directory.CreateDirectory(current);
            }
            else if (kind != EntryKind.Directory)
            {
                throw new SafeFsException($"Cannot copy '{entry}' into '{root}': '{current}' is {Describe(kind)}, not a folder.");
            }
        }

        return current;
    }

    /// <summary>The name of the entry in <paramref name="directory"/> that equals <paramref name="name"/> ignoring case, or null.</summary>
    private static string? FindIgnoringCase(string directory, string name) =>
        Directory.GetFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .FirstOrDefault(found => found.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void CopyDirectory(
        string from, string to, string relative, LinkCopyPolicy policy,
        List<LinkRecord> found, ref int fileCount, ref long byteCount)
    {
        Directory.CreateDirectory(to);
        foreach (var path in SortedEntries(from))
        {
            var name = Path.GetFileName(path);
            var entryRelative = relative.Length == 0 ? name : relative + "/" + name;
            CopyEntry(path, Path.Combine(to, name), entryRelative, policy, found, ref fileCount, ref byteCount);
        }
    }

    /// <summary>Copies one file or folder to <paramref name="destination"/>. A link is recorded, and recreated if the policy says so; it is never followed.</summary>
    private static void CopyEntry(
        string path, string destination, string entryRelative, LinkCopyPolicy policy,
        List<LinkRecord> found, ref int fileCount, ref long byteCount)
    {
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
