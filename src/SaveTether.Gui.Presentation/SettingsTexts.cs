namespace SaveTether.Gui.Presentation;

/// <summary>The Settings page's "Backups" section (desktop-app-spec.md, section 11).</summary>
public static class SettingsTexts
{
    public const string BackupBeforeActions = "Back up before actions";

    public const string BackupBeforeActionsNote =
        "On, linking, reverting, restoring and deleting back up what they change first. \"Back up now\" works either way.";

    public const string UnsafeModeNote = "Unsafe mode: actions take no backup. Data can be lost for good.";

    public const string UnsafeModeTitle = "Turn off backups before actions?";

    public const string UnsafeModeText =
        "Actions will take no backup. Data they replace or delete cannot be recovered.";

    public const string UnsafeModeConfirm = "Turn off";

    public const string UnsafeModePhrase = "UNSAFE";

    public const string MaxBackups = "Max backups per folder";

    public const string MaxBackupsNote = "Older backups beyond this are deleted. 0 keeps every backup. The first backup of each folder is always kept.";

    public const string SyncConflicts = "Sync conflicts";

    public const string DetectSyncConflicts = "Detect sync conflicts";

    public const string DetectSyncConflictsNote =
        "On, each check looks in your linked shared folders for the conflict copies a sync tool such as Syncthing leaves " +
        "when two devices changed the same file, and shows \"File conflict\" when it finds one.";
}
