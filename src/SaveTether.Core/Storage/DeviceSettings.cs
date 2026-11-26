using System.Text.Json;
using System.Text.Json.Serialization;
using SaveTether.Core.Backup;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Storage;

/// <summary>
/// This device's settings (desktop-app-spec.md, section 11), in one file in the local folder, shared by the app, the CLI and,
/// through the CLI, the Decky plugin. A value never set, or a file that cannot be read, gives the default; every default is
/// the safe one (backups on). Each call reads the file afresh, so a change made elsewhere is seen at once.
/// </summary>
public sealed class DeviceSettings(string localFolder)
{
    public const string FileName = "settings.json";

    /// <summary>The notifications switch's file before <see cref="FileName"/>: read while the new file has no value, removed on the next write.</summary>
    public const string LegacyNotificationsFileName = "notifications.txt";

    public const int MaxBackupsLimit = 20;

    private string File => Path.Combine(localFolder, FileName);

    private string LegacyNotificationsFile => Path.Combine(localFolder, LegacyNotificationsFileName);

    /// <summary>"Desktop notifications": off silences SaveTether's notifications only; the tray icon still shows the state.</summary>
    public bool Notifications => Read().Notifications ?? LegacyNotifications() ?? true;

    /// <summary>"Back up before actions". Off is unsafe mode: no action takes a backup; "Back up now" still does.</summary>
    public bool BackupBeforeActions => Read().BackupBeforeActions ?? true;

    /// <summary>How many backups of each folder are kept, besides its first; 0 keeps them all.</summary>
    public int MaxBackupsPerFolder => Read().MaxBackupsPerFolder is int n && n is >= 0 and <= MaxBackupsLimit ? n : BackupStore.DefaultKeep;

    /// <summary>"Detect sync conflicts": look for a sync tool's conflict copies in each linked shared folder. Off by default.</summary>
    public bool DetectSyncConflicts => Read().DetectSyncConflicts ?? false;

    /// <summary>Each setter answers why the setting could not be saved, or null.</summary>
    public string? SetNotifications(bool on) => Write(s => s with { Notifications = on });

    public string? SetDetectSyncConflicts(bool on) => Write(s => s with { DetectSyncConflicts = on });

    public string? SetBackupBeforeActions(bool on) => Write(s => s with { BackupBeforeActions = on });

    public string? SetMaxBackupsPerFolder(int count) =>
        count is < 0 or > MaxBackupsLimit
            ? $"The number of backups to keep must be from 0 to {MaxBackupsLimit}, not {count}."
            : Write(s => s with { MaxBackupsPerFolder = count });

    private SettingsFile Read()
    {
        try
        {
            return SafeFs.Probe(File) == EntryKind.File
                ? JsonSerializer.Deserialize(System.IO.File.ReadAllText(File), SettingsJsonContext.Default.SettingsFile) ?? new SettingsFile()
                : new SettingsFile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SettingsFile();
        }
    }

    private bool? LegacyNotifications()
    {
        try
        {
            return SafeFs.Probe(LegacyNotificationsFile) == EntryKind.File
                ? System.IO.File.ReadAllText(LegacyNotificationsFile).Trim() != "off"
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? Write(Func<SettingsFile, SettingsFile> change)
    {
        try
        {
            var current = Read();
            Directory.CreateDirectory(localFolder);
            var updated = change(current with { Notifications = current.Notifications ?? LegacyNotifications() });
            SafeFs.WriteFileAtomically(File, JsonSerializer.Serialize(updated, SettingsJsonContext.Default.SettingsFile) + "\n");
            new SafeFs().RemoveFile(LegacyNotificationsFile);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SafeFsException)
        {
            return $"The setting could not be saved in {File}: {e.Message}";
        }
    }
}

internal sealed record SettingsFile
{
    public bool? Notifications { get; init; }

    public bool? BackupBeforeActions { get; init; }

    public int? MaxBackupsPerFolder { get; init; }

    public bool? DetectSyncConflicts { get; init; }
}

// Unknown names are ignored, not refused: a newer SaveTether may add settings this one does not know.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
