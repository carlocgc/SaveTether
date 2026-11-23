using System.Globalization;
using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;

namespace SaveTether.Gui.Presentation;

/// <summary>One backup as the Backups page lists it (desktop-app-spec.md, section 9.7).</summary>
/// <param name="OfSharedCopy">True when it was taken of the shared folder, not of this device's save folder.</param>
/// <param name="Kept">The mapping's first backup, which retention never removes.</param>
public sealed record BackupRow(BackupInfo Info, string When, string Reason, bool OfSharedCopy, bool Kept, string Size)
{
    /// <summary>"Shared copy" or "This device", for the row.</summary>
    public string Of => OfSharedCopy ? "Shared copy" : "This device";

    /// <summary>The backup's plain copy of the folder, for "Open folder".</summary>
    public string DataFolder => Path.Combine(Info.Location, "data");
}

/// <summary>A mapping's backups, newest first.</summary>
public sealed record BackupGroup(string MappingId, string AppId, string Name, IReadOnlyList<BackupRow> Rows);

/// <summary>
/// The Backups page (section 9.7): backups grouped by mapping, the app filter, and the words for each
/// backup's reason and for restore's refusals and confirmation. Reads only; restore runs through the core.
/// </summary>
public static class BackupsPage
{
    /// <summary>
    /// Reads this device's backups, grouped, with each app named from the catalog. Reads only, so no lock;
    /// call it off the UI thread. A <see cref="Core.Storage.StoreException"/> means the device cannot be read now.
    /// </summary>
    public static (IReadOnlyList<BackupGroup> Groups, Func<string, string> AppName) Load(Core.Storage.LocalStore local)
    {
        var workspace = Core.Operations.Workspace.Open(local, NoProcesses.Instance);
        var catalog = workspace.Definitions;
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        string AppName(string id)
        {
            if (!names.TryGetValue(id, out var name))
            {
                name = AppNames.Resolve(catalog, id).Name;
                names[id] = name;
            }

            return name;
        }

        var groups = From(workspace.ListBackups(), workspace.Bootstrap.SyncRoot, AppName);
        return (groups, AppName);
    }

    /// <summary>Groups the core's list by mapping, in name order, each newest first.</summary>
    /// <param name="syncRoot">This device's sync folder: a backup taken inside it is of the shared copy.</param>
    /// <param name="appName">The app's name for an app ID, as the Apps page names it.</param>
    public static IReadOnlyList<BackupGroup> From(IReadOnlyList<BackupInfo> backups, string syncRoot, Func<string, string> appName)
    {
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(appName);
        var root = PathRules.ResolveAll(syncRoot);
        return backups
            .GroupBy(b => b.MappingId, StringComparer.Ordinal)
            .Select(g =>
            {
                var appId = g.Key.Split('.')[0];
                var role = g.Key.Contains('.', StringComparison.Ordinal) ? g.Key[(g.Key.IndexOf('.', StringComparison.Ordinal) + 1)..] : g.Key;
                var first = g.MinBy(b => b.CreatedUtc)!.Id;
                var rows = g
                    .OrderByDescending(b => b.CreatedUtc)
                    .ThenByDescending(b => b.Id, StringComparer.Ordinal)
                    .Select(b => new BackupRow(
                        b,
                        b.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        Reason(b.Reason),
                        IsOfShared(b.SourcePath, root),
                        b.Id == first,
                        $"{b.FileCount} {(b.FileCount == 1 ? "file" : "files")}, {ActionTexts.Size(b.TotalBytes)}"))
                    .ToList();
                return new BackupGroup(g.Key, appId, $"{appName(appId)} {role}", rows);
            })
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The app filter's choices: each app with backups, by name, as (ID, name).</summary>
    public static IReadOnlyList<(string AppId, string Name)> Apps(IReadOnlyList<BackupGroup> groups, Func<string, string> appName)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(appName);
        return groups.Select(g => g.AppId).Distinct(StringComparer.Ordinal)
            .Select(id => (id, appName(id)))
            .OrderBy(a => a.Item2, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The groups for one app, or all of them when <paramref name="appId"/> is null.</summary>
    public static IReadOnlyList<BackupGroup> Filter(IReadOnlyList<BackupGroup> groups, string? appId)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return appId is null ? groups : groups.Where(g => g.AppId == appId).ToList();
    }

    public static string Reason(BackupReason reason) => reason switch
    {
        BackupReason.PreApply => "before linking",
        BackupReason.PreRevert => "before reverting",
        BackupReason.PreRestore => "before restoring",
        BackupReason.PreDelete => "before deleting",
        BackupReason.Baseline => "first backup",
        BackupReason.Snapshot => "snapshot",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "A reason with no text."),
    };

    public const string Intro =
        "SaveTether copies a folder here before it changes it. Each backup is a plain copy: Open folder shows it, and its files can be copied back by hand.";

    public const string KeptText = "Kept: the first backup of this folder is never removed.";

    /// <summary>Delete's confirmation title.</summary>
    public static string DeleteTitle(BackupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"Delete the backup from {row.When}?";
    }

    /// <summary>
    /// Delete's confirmation. The kept first backup is worded more strongly: it is usually the only copy of
    /// the folder from before SaveTether changed it, and nothing else would bring that state back.
    /// </summary>
    public static string DeleteText(BackupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var what = $"{row.Of}, {row.Reason}, {row.Size}. It cannot be undone.";
        return row.Kept
            ? $"{what}\n\nThis is the first backup of this folder: usually the only copy of it from before SaveTether changed it. " +
              "Keep it unless you are sure you will never want that state back."
            : what;
    }

    /// <summary>Delete's button in the confirmation: plainer for an ordinary backup, explicit for the kept one.</summary>
    public static string DeleteButton(BackupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Kept ? "Delete the first backup" : "Delete";
    }

    /// <summary>The confirmation for a backup of the shared copy (section 9.7), like replace-shared's.</summary>
    public static string RestoreSharedTitle(string role) => $"Restore the shared copy of {role}?";

    public const string RestoreSharedText = "The shared copy is backed up on this device, then replaced with this backup.";

    /// <summary>Typed to confirm restoring the shared copy, as for every other deletion of saves.</summary>
    public const string RestoreSharedPhrase = "restore shared data";

    /// <summary>
    /// A local restore's confirmation sentence, above the plan's. The plan's own sentences say a backup is
    /// taken first, so this does not (it said it twice on the Deck).
    /// </summary>
    public static string RestoreLocalText(BackupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return $"The save folder is put back as it was on {row.When} ({row.Reason}).";
    }

    private static bool IsOfShared(string sourcePath, string resolvedSyncRoot)
    {
        try
        {
            return PathRules.IsSameOrInside(PathRules.ResolveParents(PathRules.Normalize(sourcePath)), resolvedSyncRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
