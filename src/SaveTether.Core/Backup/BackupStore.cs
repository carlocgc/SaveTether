using System.Globalization;
using System.Text.Json;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;

namespace SaveTether.Core.Backup;

/// <summary>
/// Verified copies of save folders, kept outside the sync root (design 5.3, invariant 3).
/// Layout: &lt;backup root&gt;/&lt;mapping-id&gt;/&lt;UTC timestamp&gt;/data/ plus manifest.json beside it.
/// The data folder is a plain tree, so a backup can be restored by hand.
/// </summary>
public sealed class BackupStore
{
    public const int DefaultKeep = 5;

    private const string ManifestName = "manifest.json";
    private const string DataName = "data";
    private const string PartialSuffix = ".partial";

    private readonly string _root;
    private readonly SafeFs _fs;
    private readonly TimeProvider _time;

    /// <param name="backupRoot">An existing folder outside the sync root.</param>
    /// <param name="syncRoot">The sync root, so a backup location inside it can be refused.</param>
    /// <param name="time">Clock; defaults to the system clock.</param>
    public BackupStore(string backupRoot, string? syncRoot = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        if (!Path.IsPathFullyQualified(backupRoot))
        {
            throw new BackupException($"The backup location must be an absolute path, but got '{backupRoot}'.");
        }

        _root = PathRules.ResolveAll(backupRoot);
        if (SafeFs.Probe(_root) != EntryKind.Directory)
        {
            throw new BackupException(
                $"The backup location '{backupRoot}' does not exist. If it is on a removable drive, check that the drive is connected.");
        }

        if (syncRoot is not null)
        {
            var resolvedSync = PathRules.ResolveAll(syncRoot);
            if (PathRules.IsSameOrInside(_root, resolvedSync))
            {
                throw new BackupException(
                    $"The backup location '{backupRoot}' is inside the sync root. Backups there would be deleted along with the saves they protect.");
            }

            if (PathRules.IsSameOrInside(resolvedSync, _root))
            {
                throw new BackupException($"The backup location '{backupRoot}' contains the sync root. Choose a separate folder.");
            }
        }

        _fs = new SafeFs(syncRoot);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The backup location, with links resolved.</summary>
    public string Root => _root;

    /// <summary>Test seam: runs on the unfinished backup folder just before it is verified.</summary>
    internal Action<string>? BeforeVerify { get; set; }

    /// <summary>
    /// Copies <paramref name="source"/> into a new backup and verifies the copy against the source's
    /// hashes. If anything fails, the unfinished backup is removed and nothing else is touched.
    /// </summary>
    public BackupInfo Create(string mappingId, string source, BackupReason reason)
    {
        RequireMappingId(mappingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!Path.IsPathFullyQualified(source))
        {
            throw new BackupException($"The folder to back up must be an absolute path, but got '{source}'.");
        }

        var sourcePath = PathRules.Normalize(source);
        var kind = SafeFs.Probe(sourcePath);
        if (kind != EntryKind.Directory)
        {
            throw new BackupException($"Cannot back up '{sourcePath}': it is not a real folder ({kind}).");
        }

        var mappingFolder = Path.Combine(_root, mappingId);
        Directory.CreateDirectory(mappingFolder);

        var created = _time.GetUtcNow();
        var name = UniqueName(mappingFolder, created);
        var partial = Path.Combine(mappingFolder, name + PartialSuffix);
        var final = Path.Combine(mappingFolder, name);

        try
        {
            Directory.CreateDirectory(partial);
            var copy = SafeFs.CopyTree(sourcePath, Path.Combine(partial, DataName), LinkCopyPolicy.RecordOnly);
            var manifest = BuildManifest($"{mappingId}/{name}", mappingId, created, reason, sourcePath, copy.Links);
            File.WriteAllText(
                Path.Combine(partial, ManifestName),
                JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest));

            BeforeVerify?.Invoke(partial);

            var verification = VerifyFolder(partial);
            if (!verification.IsValid)
            {
                throw new BackupException(
                    $"The backup of '{sourcePath}' did not match the original, so it was discarded. " +
                    "The files may have changed during the copy; make sure the app is closed. " +
                    $"First problem: {verification.Problems[0]}");
            }

            _fs.RenameDirectory(partial, final);
            return ToInfo(manifest, final);
        }
        catch
        {
            _fs.DeleteTree(partial);
            throw;
        }
    }

    /// <summary>Re-reads a backup and compares every file with the hash recorded when it was taken.</summary>
    public BackupVerification Verify(string backupId) => VerifyFolder(FolderOf(backupId));

    /// <summary>Finished, readable backups, oldest first. Optionally for one mapping only.</summary>
    public IReadOnlyList<BackupInfo> List(string? mappingId = null)
    {
        if (mappingId is not null)
        {
            RequireMappingId(mappingId);
        }

        var mappingFolders = mappingId is null
            ? Directory.GetDirectories(_root)
            : [Path.Combine(_root, mappingId)];

        var found = new List<BackupInfo>();
        foreach (var mappingFolder in mappingFolders)
        {
            if (SafeFs.Probe(mappingFolder) != EntryKind.Directory)
            {
                continue;
            }

            foreach (var folder in Directory.GetDirectories(mappingFolder))
            {
                if (IsPartial(folder) || SafeFs.Probe(folder) != EntryKind.Directory)
                {
                    continue;
                }

                if (TryReadManifest(folder) is { } manifest)
                {
                    found.Add(ToInfo(manifest, folder));
                }
            }
        }

        return found
            .OrderBy(b => b.MappingId, StringComparer.Ordinal)
            .ThenBy(b => b.CreatedUtc)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Deletes old backups of one mapping, keeping the newest <paramref name="keep"/> and always the
    /// first one ever taken (the only copy of the state before SaveTether touched the folder).
    /// Also clears unfinished backups left by a crash. Returns the IDs it deleted.
    /// </summary>
    public IReadOnlyList<string> Prune(string mappingId, int keep = DefaultKeep)
    {
        RequireMappingId(mappingId);
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);

        var mappingFolder = Path.Combine(_root, mappingId);
        if (SafeFs.Probe(mappingFolder) != EntryKind.Directory)
        {
            return [];
        }

        foreach (var folder in Directory.GetDirectories(mappingFolder).Where(IsPartial))
        {
            _fs.DeleteTree(folder);
        }

        var backups = List(mappingId);
        var deleted = new List<string>();
        foreach (var backup in backups.Skip(1).SkipLast(keep))
        {
            _fs.DeleteTree(backup.Location);
            deleted.Add(backup.Id);
        }

        return deleted;
    }

    /// <summary>The folder holding the plain copy of the files for <paramref name="backupId"/>.</summary>
    public string DataPathOf(string backupId) => Path.Combine(FolderOf(backupId), DataName);

    /// <summary>The links that were inside the backed-up folder. They are recorded, not copied.</summary>
    public IReadOnlyList<BackupLink> LinksOf(string backupId) =>
        TryReadManifest(FolderOf(backupId))?.Links
        ?? throw new BackupException($"The manifest of backup '{backupId}' is missing or unreadable.");

    private static BackupManifest BuildManifest(
        string id, string mappingId, DateTimeOffset created, BackupReason reason, string sourcePath,
        IReadOnlyList<LinkRecord> links)
    {
        var directories = new List<string>();
        var files = new List<BackupFile>();
        foreach (var entry in SafeFs.EnumerateTree(sourcePath))
        {
            if (entry.Kind == EntryKind.Directory)
            {
                directories.Add(entry.RelativePath);
            }
            else if (entry.Kind == EntryKind.File)
            {
                var path = Path.Combine(sourcePath, entry.RelativePath);
                files.Add(new BackupFile(entry.RelativePath, entry.Length, HashFile(path)));
            }
        }

        var recorded = links
            .Select(l => new BackupLink(l.RelativePath, l.Kind == EntryKind.Junction ? "junction" : "symlink", l.Target))
            .ToList();

        return new BackupManifest(
            BackupManifest.CurrentSchema, id, mappingId, created, reason, sourcePath, directories, files, recorded);
    }

    private static BackupVerification VerifyFolder(string folder)
    {
        var manifest = TryReadManifest(folder);
        if (manifest is null)
        {
            return new BackupVerification([$"The manifest is missing or unreadable in '{folder}'."]);
        }

        var data = Path.Combine(folder, DataName);
        if (SafeFs.Probe(data) != EntryKind.Directory)
        {
            return new BackupVerification([$"The data folder is missing in '{folder}'."]);
        }

        var problems = new List<string>();
        var present = SafeFs.EnumerateTree(data).ToDictionary(e => e.RelativePath, StringComparer.Ordinal);

        foreach (var directory in manifest.Directories)
        {
            if (!present.Remove(directory, out var entry) || entry.Kind != EntryKind.Directory)
            {
                problems.Add($"Folder missing from the backup: {directory}");
            }
        }

        foreach (var file in manifest.Files)
        {
            if (!present.Remove(file.Path, out var entry) || entry.Kind != EntryKind.File)
            {
                problems.Add($"File missing from the backup: {file.Path}");
            }
            else if (entry.Length != file.Size)
            {
                problems.Add($"File size differs: {file.Path} (expected {file.Size} bytes, found {entry.Length})");
            }
            else if (!string.Equals(HashFile(Path.Combine(data, file.Path)), file.Sha256, StringComparison.Ordinal))
            {
                problems.Add($"File content differs: {file.Path}");
            }
        }

        problems.AddRange(present.Keys.Order(StringComparer.Ordinal).Select(extra => $"Not in the manifest: {extra}"));
        return new BackupVerification(problems);
    }

    private static BackupManifest? TryReadManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestName);
        if (SafeFs.Probe(path) != EntryKind.File)
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(path), BackupJsonContext.Default.BackupManifest);
            var usable = manifest is { Schema: BackupManifest.CurrentSchema, Files: not null, Directories: not null, Links: not null }
                && Identifiers.IsValidMappingId(manifest.MappingId);
            return usable ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string FolderOf(string backupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupId);
        var parts = backupId.Split('/');
        if (parts.Length != 2 || !Identifiers.IsValidMappingId(parts[0]) || !IsValidName(parts[1]))
        {
            throw new BackupException($"'{backupId}' is not a backup ID. Expected the form 'mapping-id/timestamp'.");
        }

        var folder = Path.Combine(_root, parts[0], parts[1]);
        if (SafeFs.Probe(folder) != EntryKind.Directory)
        {
            throw new BackupException($"There is no backup '{backupId}' in '{_root}'.");
        }

        return folder;
    }

    private static string UniqueName(string mappingFolder, DateTimeOffset created)
    {
        var stamp = created.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var name = stamp;
        for (var n = 2; Exists(name); n++)
        {
            name = $"{stamp}-{n}";
        }

        return name;

        bool Exists(string candidate) =>
            SafeFs.Probe(Path.Combine(mappingFolder, candidate)) != EntryKind.Missing
            || SafeFs.Probe(Path.Combine(mappingFolder, candidate + PartialSuffix)) != EntryKind.Missing;
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= 40 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool IsPartial(string folder) => folder.EndsWith(PartialSuffix, StringComparison.Ordinal);

    private static void RequireMappingId(string mappingId)
    {
        if (!Identifiers.IsValidMappingId(mappingId))
        {
            throw new BackupException($"'{mappingId}' is not a valid mapping ID. Expected a form like 'ppsspp.savedata'.");
        }
    }

    private static string HashFile(string path) => FileHasher.Sha256Hex(path);

    private static BackupInfo ToInfo(BackupManifest manifest, string location) => new(
        manifest.Id, manifest.MappingId, manifest.CreatedUtc, manifest.Reason, manifest.SourcePath,
        manifest.Files.Count, manifest.Files.Sum(f => f.Size), location);
}
