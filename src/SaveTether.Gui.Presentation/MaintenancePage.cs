using System.Globalization;
using SaveTether.Core.Backup;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Gui.Presentation;

/// <summary>One device in "Other devices" or "This device" (plan phase 3.6, "The app: screens and dialogs").</summary>
/// <param name="RemoveDisabled">Why Remove is disabled, or null when it is not.</param>
/// <param name="Note">"Removed, but its file came back" for a tombstoned device, else null.</param>
public sealed record DeviceRow(string Name, string InstallId, bool IsThisDevice, string Activity, string Mappings, string? Note, string? RemoveDisabled);

/// <summary>One orphaned mapping, with why its actions are disabled, if they are.</summary>
public sealed record OrphanRow(string MappingId, string? Disabled);

/// <summary>What the Maintenance screen shows, from the core's overview. Reads nothing itself. <c>This</c> is null when this device's file is gone (a removal that did not finish).</summary>
public sealed record MaintenanceView(
    IReadOnlyList<string> OverviewLines, DeviceRow? This, IReadOnlyList<DeviceRow> Others, IReadOnlyList<OrphanRow> Orphans, string? Blocker);

/// <summary>The Maintenance screen's rows and texts, and the checks behind its text dialogs.</summary>
public static class MaintenancePage
{
    public const string BlockedByJournal = "An operation did not finish. Recover it first.";
    public const string BlockedByLock = "Another SaveTether operation is running. Wait for it to finish.";
    public const string BlockedBySyncRoot = "The sync folder is not available.";
    public const string RemoveThisDeviceOnly = "This device: use Remove this device.";
    public const string ReturnedNote = "Removed, but its file came back";
    public const string DeleteEverythingPhrase = "delete everything";

    /// <summary>Why every action is disabled, or null. The same rule as the Apps page: what the core would refuse.</summary>
    public static string? Blocker(bool journalOpen, bool lockHeld, bool syncRootAvailable) =>
        journalOpen ? BlockedByJournal : lockHeld ? BlockedByLock : !syncRootAvailable ? BlockedBySyncRoot : null;

    public static MaintenanceView From(MaintenanceOverview overview, string? blocker, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        var clock = now ?? DateTimeOffset.UtcNow;
        var self = overview.Devices.SingleOrDefault(d => d.IsThisDevice);
        var lines = new List<string>
        {
            $"Sync folder: {overview.SyncRoot}",
            $"Devices registered: {overview.Devices.Count}",
            $"Mappings in the sync folder: {overview.Mappings.Count}",
            $"Mappings this device uses: {overview.MappingsHere.Count}, {overview.LinkedHere.Count} linked here",
            $"Orphaned mappings: {overview.Orphans.Count}",
            overview.BackupCount == 0
                ? "Backups: none"
                : $"Backups: {overview.BackupCount}, {ActionTexts.Size(overview.BackupBytes)}",
        };
        DeviceRow Row(DeviceOverview d) => new(
            d.Name, d.InstallId, d.IsThisDevice, Ago(d.LastActivityUtc, clock),
            $"{d.MappingCount} {(d.MappingCount == 1 ? "mapping" : "mappings")}",
            d.Tombstoned ? ReturnedNote : null,
            d.IsThisDevice ? RemoveThisDeviceOnly : blocker);
        return new MaintenanceView(
            lines,
            self is null ? null : Row(self),
            overview.Devices.Where(d => !d.IsThisDevice).Select(Row).ToList(),
            overview.Orphans.Select(o => new OrphanRow(o, blocker)).ToList(),
            blocker);
    }

    /// <summary>What the screen opens with: the core's overview and the rows, or why there are none (the sync folder cannot be read).</summary>
    public sealed record Loaded(MaintenanceOverview? Overview, MaintenanceView? View, string DeviceName, string InstallId, string? Blocker);

    /// <summary>
    /// Reads the device and the sync folder, and works out why actions are disabled, if they are. Call it off the UI thread. A
    /// <see cref="StoreException"/> means this device is not set up.
    /// </summary>
    public static Loaded Load(LocalStore local)
    {
        ArgumentNullException.ThrowIfNull(local);
        var workspace = Workspace.Open(local, NoProcesses.Instance);
        var journal = workspace.Journal.IsOpen;
        var locked = OperationLock.HeldByAnotherProcess(local) is not null;
        MaintenanceOverview? overview = null;
        try
        {
            overview = Maintenance.Overview(workspace);
        }
        catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException)
        {
            // The sync folder cannot be read: no rows, and the blocker says so.
        }

        var blocker = Blocker(journal, locked, overview is not null);
        return new Loaded(overview, overview is null ? null : From(overview, blocker), workspace.DeviceName, workspace.Bootstrap.InstallId, blocker);
    }

    /// <summary>"3 hours ago", in the coarsest unit that is not zero.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        var (n, unit) = span.TotalDays >= 1 ? ((int)span.TotalDays, "day")
            : span.TotalHours >= 1 ? ((int)span.TotalHours, "hour")
            : ((int)span.TotalMinutes, "minute");
        return string.Create(CultureInfo.InvariantCulture, $"{n} {unit}{(n == 1 ? "" : "s")} ago");
    }

    // The text dialogs (D8). Both ignore case and spaces at either end; spaces inside the phrase must match.

    /// <summary>Why <paramref name="name"/> cannot be this device's name, or null. <paramref name="taken"/> holds the other devices' names.</summary>
    public static string? DeviceNameProblem(string name, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var trimmed = name?.Trim();
        if (!Identifiers.IsValidDeviceName(trimmed))
        {
            return "Use up to 40 letters, digits, dots, dashes or underscores, with no spaces.";
        }

        return taken.Contains(trimmed!, StringComparer.Ordinal) ? $"'{trimmed}' is used by another device." : null;
    }

    public static string? TypedPhraseProblem(string typed, string phrase) =>
        string.Equals(typed?.Trim(), phrase, StringComparison.OrdinalIgnoreCase) ? null : $"Type {phrase} to confirm.";

    // Remove another device (D3).
    public static string RemoveOtherTitle(string name) => $"Remove the device '{name}'?";

    public const string RemoveOtherText =
        "This deletes the device's records from the sync folder. No links or saves are touched. " +
        "If that device is still in use, it asks what to do the next time it checks.";

    public const string DeleteOrphanMappingText = "The mapping is deleted. Its folder in the sync folder stays.";
    public const string DeleteOrphanWithSavesText = "The mapping and its folder in the sync folder are deleted, after a backup.";
}

/// <summary>Backup Clean up (D9): the three modes and their sentences.</summary>
public static class BackupCleanupTexts
{
    public const string AllPhrase = "delete all backups";

    public static string Label(BackupCleanup mode) => mode switch
    {
        BackupCleanup.KeepFirst => "Keep only the first backup",
        BackupCleanup.KeepLast => "Keep only the newest backup",
        BackupCleanup.All => "Delete all backups",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A mode with no text."),
    };

    public static string Describe(BackupCleanup mode, string scope) => mode switch
    {
        BackupCleanup.KeepFirst => $"Each mapping of {scope} keeps its first backup, the copy taken before it was first linked. The rest are deleted.",
        BackupCleanup.KeepLast => $"Each mapping of {scope} keeps its newest backup. The rest are deleted.",
        BackupCleanup.All =>
            $"Every backup of {scope} is deleted, including the first one, which is usually the only copy from before SaveTether changed the folder. It cannot be undone.",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "A mode with no text."),
    };

    /// <summary>"3 backups", for the confirmation and the result.</summary>
    public static string Count(int n) => $"{n} {(n == 1 ? "backup" : "backups")}";

    /// <summary>The scope's name: the app's, or "all apps".</summary>
    public static string Scope(string? appName) => appName ?? "all apps";

    public static string Result(int removed) => removed == 0 ? "Nothing to delete." : $"Deleted {Count(removed)}.";
}
