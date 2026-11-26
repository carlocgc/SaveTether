using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaveTether.Core.Backup;
using SaveTether.Core.Definitions;
using SaveTether.Core.Health;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Cli;

/// <summary>How one operation ended.</summary>
internal enum OperationOutcome
{
    /// <summary>Dry run: this is what would be done. Nothing was changed.</summary>
    [JsonStringEnumMemberName("planned")]
    Planned,

    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>A precondition, or a choice the owner still has to make. Nothing was changed.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,

    /// <summary>The owner said no at a prompt. Nothing was changed.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    [JsonStringEnumMemberName("rolled-back")]
    RolledBack,

    [JsonStringEnumMemberName("recovery-needed")]
    RecoveryNeeded,

    [JsonStringEnumMemberName("nothing-to-recover")]
    NothingToRecover,
}

/// <summary>One apply, revert, restore or recovery.</summary>
/// <param name="Operation">Null only when recovery found nothing to do.</param>
/// <param name="Plan">What was, or would be, done. Absent when refused.</param>
/// <param name="Refusal">Why the planner said no, with the allowed choices and the comparison.</param>
/// <param name="Error">Why it did not complete, when there is no planner refusal to say so.</param>
internal sealed record OperationOutput(
    string? MappingId,
    PlanOperation? Operation,
    OperationOutcome Outcome,
    Plan? Plan,
    Refusal? Refusal,
    IReadOnlyList<string> BackupIds,
    IReadOnlyList<string> Messages,
    string? Error);

/// <summary>"app add": what was, or would be, written, or why not.</summary>
/// <param name="Added">True when the mappings and this device's entries were written.</param>
/// <param name="Plan">Absent when there is no plan.</param>
/// <param name="Problem">Why there is no plan.</param>
/// <param name="Candidates">The installs of the app for this system, and whether each was found.</param>
/// <param name="Conflict">When <paramref name="Problem"/> is a conflict: what is in the way.</param>
internal sealed record AppOutput(
    string AppId, bool Added, AppAddPlan? Plan, AppAddProblem? Problem, IReadOnlyList<InstallCandidate> Candidates,
    ConflictFacts? Conflict = null);

/// <summary>"app list": one app that can be added.</summary>
internal sealed record AppListEntry(string Id, string? Name, DefinitionSource Source, string? Error)
{
    /// <summary>False for an imported definition.</summary>
    public bool Verified => Source != DefinitionSource.Imported;
}

/// <summary>"app list --found": an app found on this device, with its install and when its save folder last changed.</summary>
internal sealed record FoundEntry(string Id, string Name, DefinitionSource Source, string Install, string SaveFolder, DateTime? NewestWrite)
{
    /// <summary>False for an imported definition.</summary>
    public bool Verified => Source != DefinitionSource.Imported;
}

internal sealed record MappingOutput(
    string Id, string Target, string Source, IReadOnlyList<string> ProcessNames, int EntryDepth,
    bool CreatedMapping, bool EntryDepthChanged, bool Removed);

/// <summary>
/// The one JSON object a command prints with <c>--json</c>. Which section is present depends on
/// the command; <see cref="Schema"/>, <see cref="Command"/> and <see cref="ExitCode"/> always are.
/// </summary>
internal sealed record CliOutput
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;

    public required string Command { get; init; }

    public required int ExitCode { get; init; }

    /// <summary>Why the command as a whole did not run. Per-operation reasons are in <see cref="Operations"/>.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// When the command was refused by one of the core's store checks, which kind it was, such as
    /// <c>operation-running</c>. Absent otherwise.
    /// </summary>
    public StoreProblem? Problem { get; init; }

    /// <summary>When <see cref="Problem"/> is <c>conflict</c>: what is in the way, as fields. Absent otherwise.</summary>
    public ConflictFacts? Conflict { get; init; }

    public string? Message { get; init; }

    public InitResult? Init { get; init; }

    public MappingOutput? Mapping { get; init; }

    public AppOutput? App { get; init; }

    public IReadOnlyList<AppListEntry>? Apps { get; init; }

    /// <summary>"app list --found": the apps found on this device, newest save first.</summary>
    public IReadOnlyList<FoundEntry>? Found { get; init; }

    public StatusReport? Status { get; init; }

    /// <summary>"notifications": the switch as it now is.</summary>
    public NotificationsOutput? Notifications { get; init; }

    /// <summary>"sync-conflicts": the switch as it now is.</summary>
    public SyncConflictsOutput? SyncConflicts { get; init; }

    /// <summary>"backup auto" and "backup keep": the backup settings as they now are.</summary>
    public BackupSettingsOutput? BackupSettings { get; init; }

    /// <summary>"device list": the devices, the mappings and the backups.</summary>
    public MaintenanceOverview? Devices { get; init; }

    /// <summary>"device rename": the old and the new name.</summary>
    public RenameResult? Renamed { get; init; }

    /// <summary>"device remove": what was done, or with a dry run what would be.</summary>
    public DeviceRemovalOutput? Removal { get; init; }

    public IReadOnlyList<OperationOutput>? Operations { get; init; }

    public IReadOnlyList<BackupInfo>? Backups { get; init; }

    /// <summary>"backup delete": the backup removed, whether it was its mapping's first, and how many are left.</summary>
    public BackupDeletion? BackupDeleted { get; init; }

    /// <summary>"backup delete --all/--keep-first/--keep-last": the backups deleted, or with a dry run, the ones that would be.</summary>
    public BackupCleanupResult? BackupsCleaned { get; init; }

    /// <summary>"map delete": the shared folder and record removed, and the backup taken first.</summary>
    public MappingDeletion? MappingDeleted { get; init; }

    /// <summary>"uninstall": what was removed, and what was left and why.</summary>
    public UninstallOutput? Uninstalled { get; init; }
}

/// <summary>"device remove". With <paramref name="DryRun"/> nothing was changed and the rest is what would be done.</summary>
/// <param name="GivenBack">The save folders that are (or would be) ordinary folders again, copied back from the shared saves.</param>
/// <param name="NotLinked">Mappings this device has a folder for that are not linked, and so were skipped.</param>
/// <param name="Orphans">Mappings only this device used, with what is done to each.</param>
/// <param name="SharedFiles">This device's files in the sync folder that go.</param>
/// <param name="BackupsDeleted">How many backups were (or would be) deleted; 0 when they are kept.</param>
/// <param name="Other">The name of the device removed with "--other"; null when this device was removed.</param>
public sealed record DeviceRemovalOutput(
    bool DryRun,
    IReadOnlyList<string> GivenBack,
    IReadOnlyList<string> NotLinked,
    IReadOnlyList<OrphanRemoval> Orphans,
    IReadOnlyList<string> SharedFiles,
    int BackupsDeleted,
    string? Other = null,
    [property: JsonIgnore] bool DeleteEverything = false);

/// <summary>"notifications": whether the "Desktop notifications" switch is on.</summary>
public sealed record NotificationsOutput(bool On);

/// <summary>"sync-conflicts": whether the "Detect sync conflicts" switch is on.</summary>
public sealed record SyncConflictsOutput(bool On);

/// <summary>"backup auto" and "backup keep": whether actions back up first (false is unsafe mode), and how many backups of each folder are kept besides its first (0: all).</summary>
public sealed record BackupSettingsOutput(bool BeforeActions, int MaxPerFolder);

/// <summary>"uninstall": the paths removed, and each one left with the reason.</summary>
public sealed record UninstallOutput(IReadOnlyList<string> Removed, IReadOnlyList<string> Left);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(NoteJsonConverter)])]
[JsonSerializable(typeof(CliOutput))]
[JsonSerializable(typeof(PlanOutcome))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

/// <summary>Prints a <see cref="CliOutput"/>: one JSON object on stdout, or text with problems on stderr.</summary>
internal static class OutputWriter
{
    public static void Write(CliOutput output, bool json, TextWriter stdout, TextWriter stderr)
    {
        if (json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.CliOutput));
            return;
        }

        if (output.Init is { } init)
        {
            WriteInit(init, stdout);
        }

        if (output.Mapping is { } mapping)
        {
            WriteMapping(mapping, stdout);
        }

        if (output.App is { } app)
        {
            WriteApp(app, stdout, stderr);
        }

        if (output.Apps is { } apps)
        {
            var width = apps.Count == 0 ? 0 : apps.Max(e => e.Id.Length);
            foreach (var entry in apps)
            {
                var from = entry.Source switch { DefinitionSource.Shared => "shared", DefinitionSource.Imported => "imported (unverified)", _ => "built-in" };
                var id = entry.Id.PadRight(width);
                stdout.WriteLine(entry.Error is null ? $"  {id}  {entry.Name}  ({from})" : $"  {id}  not usable: {entry.Error}  ({from})");
            }

            stdout.WriteLine($"{apps.Count.ToString("N0", CultureInfo.InvariantCulture)} app(s).");
        }

        if (output.Found is { } found)
        {
            var width = found.Count == 0 ? 0 : found.Max(e => e.Id.Length);
            foreach (var entry in found)
            {
                var when = entry.NewestWrite is { } w ? w.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "no files";
                var mark = entry.Verified ? string.Empty : "  (unverified)";
                stdout.WriteLine($"  {entry.Id.PadRight(width)}  {entry.Name}  last played {when}{mark}");
            }

            stdout.WriteLine($"{found.Count.ToString("N0", CultureInfo.InvariantCulture)} app(s) found on this device.");
        }

        if (output.Status is { } status)
        {
            WriteStatus(status, stdout, stderr);
        }

        if (output.Notifications is { } notifications)
        {
            stdout.WriteLine($"Desktop notifications: {(notifications.On ? "on" : "off")}");
        }

        if (output.SyncConflicts is { } syncConflicts)
        {
            stdout.WriteLine($"Detect sync conflicts: {(syncConflicts.On ? "on" : "off")}");
        }

        if (output.BackupSettings is { } backupSettings)
        {
            stdout.WriteLine($"Back up before actions: {(backupSettings.BeforeActions ? "on" : "off (unsafe mode)")}");
            stdout.WriteLine($"Max backups per folder: {(backupSettings.MaxPerFolder == 0 ? "unlimited" : backupSettings.MaxPerFolder)}, besides the first");
        }

        if (output.Devices is { } overview)
        {
            WriteDevices(overview, stdout);
        }

        if (output.Renamed is { } renamed)
        {
            stdout.WriteLine(renamed.OldName == renamed.NewName
                ? $"This device is already '{renamed.NewName}'. Any files left by an earlier rename were cleared."
                : $"Renamed this device from '{renamed.OldName}' to '{renamed.NewName}'.");
        }

        if (output.Removal is { } removal)
        {
            WriteRemoval(removal, stdout);
        }

        foreach (var operation in output.Operations ?? [])
        {
            WriteOperation(operation, stdout, stderr);
        }

        if (output.Backups is { } backups)
        {
            WriteBackups(backups, stdout);
        }

        if (output.BackupDeleted is { } deleted)
        {
            stdout.WriteLine($"Deleted backup {deleted.Backup.Id}. {deleted.Remaining} backup(s) of {deleted.Backup.MappingId} left.");
        }

        if (output.BackupsCleaned is { } cleaned)
        {
            var verb = cleaned.DryRun ? "Would delete" : "Deleted";
            foreach (var backup in cleaned.Removed)
            {
                stdout.WriteLine($"{verb} {backup.Id} ({backup.FileCount} file(s), {FormatBytes(backup.TotalBytes)})");
            }

            stdout.WriteLine($"{verb} {cleaned.Removed.Count} backup(s).");
        }

        if (output.MappingDeleted is { } mappingDeleted)
        {
            stdout.WriteLine($"Deleted '{mappingDeleted.MappingId}' from the shared folder.");
            if (mappingDeleted.Backup is { } backup)
            {
                stdout.WriteLine($"  The shared saves ({backup.FileCount} file(s), {FormatBytes(backup.TotalBytes)}) are backed up as {backup.Id}.");
            }
        }

        if (output.Uninstalled is { } uninstalled)
        {
            stdout.WriteLine("Uninstalled SaveTether. Removed:");
            foreach (var path in uninstalled.Removed)
            {
                stdout.WriteLine($"  {path}");
            }

            foreach (var left in uninstalled.Left)
            {
                stdout.WriteLine($"  left: {left}");
            }

            stdout.WriteLine("Your setup, linked folders, backups and the sync folder were not touched.");
        }

        if (output.Message is { } message)
        {
            stdout.WriteLine(message);
        }

        if (output.Error is { } error)
        {
            stderr.WriteLine($"savetether: {error}");
        }
    }

    private static void WriteInit(InitResult init, TextWriter stdout)
    {
        var how = init.NewDevice ? "new device" : init.TookOver ? "name taken over" : "already set up";
        stdout.WriteLine($"This device is '{init.DeviceName}' ({how}).");
        stdout.WriteLine($"  sync root: {init.SyncRoot}{(init.CreatedRoot ? " (SaveTether folder created)" : string.Empty)}");
        stdout.WriteLine($"  backups:   {init.BackupRoot}");
        stdout.WriteLine($"  devices:   {string.Join(", ", init.Devices)}");
        foreach (var note in init.Notes)
        {
            stdout.WriteLine($"Note: {Hints.Compose(note)}");
        }
    }

    private static void WriteMapping(MappingOutput mapping, TextWriter stdout)
    {
        if (mapping.Removed)
        {
            stdout.WriteLine($"Removed this device's save folder for {mapping.Id}. The shared mapping is kept for other devices.");
            return;
        }

        stdout.WriteLine(mapping.CreatedMapping ? $"Added mapping {mapping.Id}." : $"Mapping {mapping.Id} already existed; set this device's save folder.");
        stdout.WriteLine($"  save folder:   {mapping.Source}");
        stdout.WriteLine($"  shared folder: savetether/{mapping.Target}");
        if (mapping.ProcessNames.Count > 0)
        {
            stdout.WriteLine($"  app processes: {string.Join(", ", mapping.ProcessNames)}");
        }

        if (mapping.EntryDepth > 1 || mapping.EntryDepthChanged)
        {
            stdout.WriteLine($"  entry depth:   {mapping.EntryDepth}{(mapping.EntryDepthChanged ? " (changed)" : string.Empty)}");
        }

        stdout.WriteLine($"No saves were touched. Run 'savetether apply {mapping.Id}' to link it.");
    }

    private static void WriteApp(AppOutput app, TextWriter stdout, TextWriter stderr)
    {
        if (app.Plan is not { } plan)
        {
            // The reason itself is the command's error; show what was looked at, so the owner can tell why.
            foreach (var candidate in app.Candidates)
            {
                stderr.WriteLine(candidate.IsDetected
                    ? $"  install {candidate.Id}: found ({candidate.Found})"
                    : $"  install {candidate.Id}: not found. {Hints.ComposeOrNull(candidate.Reason)}");
            }

            return;
        }

        var from = plan.DefinitionSource == DefinitionSource.Shared ? $" (definition: {plan.DefinitionLocation})" : string.Empty;
        stdout.WriteLine($"{plan.AppName}, install '{plan.Install}'{from}:");
        if (!plan.Verified)
        {
            stdout.WriteLine("  Unverified: this definition was converted from the Ludusavi manifest and has not been tried by hand. Check the save folders below before you apply.");
        }

        foreach (var role in plan.Roles)
        {
            var size = !role.Exists ? "not there yet"
                : role.IsLink ? "already a link"
                : $"{role.Files.ToString("N0", CultureInfo.InvariantCulture)} file(s), {FormatBytes(role.Bytes)}";
            stdout.WriteLine($"  {role.MappingId}  ({size})");
            stdout.WriteLine($"    save folder:   {role.Source}");
            stdout.WriteLine($"    shared folder: savetether/{role.Target}{(role.CreatesMapping ? " (new mapping)" : string.Empty)}");
            if (role.EntryDepth > 1)
            {
                stdout.WriteLine($"    entry depth:   {role.EntryDepth}");
            }

            if (role.Note is { } note)
            {
                stdout.WriteLine($"    Note: {Hints.Compose(note)}");
            }
        }

        foreach (var quirk in plan.Quirks)
        {
            stdout.WriteLine($"Note: {quirk}");
        }

        if (!app.Added)
        {
            stdout.WriteLine("Dry run: nothing was changed.");
            return;
        }

        stdout.WriteLine("Added. No saves were touched.");
        var toApply = plan.Roles.Where(r => !r.IsLink).Select(r => r.MappingId).ToList();
        var linked = plan.Roles.Where(r => r.IsLink).Select(r => r.MappingId).ToList();
        foreach (var id in toApply)
        {
            stdout.WriteLine($"Run 'savetether apply {id}' to link it.");
        }

        if (linked.Count > 0)
        {
            // A link here was already in place; whether it is SaveTether's and healthy is what status says.
            stdout.WriteLine($"Already a link: {string.Join(", ", linked)}. 'savetether status' shows whether it is healthy.");
        }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KiB",
        _ => $"{bytes} bytes",
    };

    private static void WriteStatus(StatusReport status, TextWriter stdout, TextWriter stderr)
    {
        stdout.WriteLine($"Device '{status.DeviceName}', sync root '{status.SyncRoot}'");
        if (status.SyncRootProblem is { } problem)
        {
            stdout.WriteLine(
                status.Mappings.Count == 0
                    ? "The sync root is not available, and this device has not linked anything."
                    : "The sync root is not available. These are the save folders this device linked, from its own record:");
            stderr.WriteLine($"Warning: {problem}");
        }
        else if (status.Mappings.Count == 0 && status.Removal is null)
        {
            stdout.WriteLine("No mappings yet. Add one with 'savetether map add'.");
        }

        var width = status.Mappings.Select(m => m.MappingId.Length).DefaultIfEmpty(0).Max();
        foreach (var mapping in status.Mappings)
        {
            var size = mapping.Bytes is { } bytes ? $" ({mapping.Files} file(s), {FormatBytes(bytes)})" : "";
            stdout.WriteLine($"  {mapping.MappingId.PadRight(width)}  {Describe(mapping.State),-13}  {Hints.Compose(mapping.Detail)}{size}");
            foreach (var conflict in mapping.Conflicts ?? [])
            {
                stdout.WriteLine($"  {"".PadRight(width)}  {"",-13}  {conflict}");
            }
        }

        if (status.OperationRunning)
        {
            stdout.WriteLine("Another SaveTether process is running an operation.");
        }

        if (status.DeviceFileMismatch)
        {
            stderr.WriteLine(
                $"Warning: device file mismatch. The name '{status.DeviceName}' now belongs to another install; two machines may be using it. " +
                "Run 'savetether init' with --take-over on the machine that should keep it.");
        }

        if (status.JournalOpen)
        {
            stderr.WriteLine("Warning: an earlier operation did not finish. Run 'savetether recover'.");
        }

        if (status.Removal == RemovalKind.RemovedByAnother)
        {
            stderr.WriteLine(
                $"Warning: this device was removed from the sync folder by '{status.RemovedBy}'. " +
                "Run 'savetether device remove' to finish the removal here, or set it up again with 'savetether init'.");
        }
        else if (status.Removal == RemovalKind.Unfinished)
        {
            stderr.WriteLine("Warning: the removal of this device did not finish. Run 'savetether device remove' again.");
        }

        foreach (var name in status.ReturnedDevices)
        {
            stderr.WriteLine(
                $"Warning: the device '{name}' was removed from the sync folder, but its file came back (a sync tool restored it). " +
                "Delete it with 'savetether device remove --other " + name + "' if it is gone for good.");
        }

        if (status.AutostartMissing is { } program)
        {
            stderr.WriteLine(
                $"Warning: SaveTether is set to start with the session, but the entry points to '{program}', which is missing. " +
                "Start the desktop app from where it is now to fix the entry, or turn starting with the session off in its settings.");
        }
    }

    private static void WriteDevices(MaintenanceOverview overview, TextWriter stdout)
    {
        stdout.WriteLine($"Sync root '{overview.SyncRoot}'");
        var width = overview.Devices.Select(d => d.Name.Length).DefaultIfEmpty(0).Max();
        foreach (var device in overview.Devices)
        {
            var when = device.LastActivityUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            var notes = (device.IsThisDevice ? "  (this device)" : string.Empty) + (device.Tombstoned ? "  (removed)" : string.Empty);
            stdout.WriteLine($"  {device.Name.PadRight(width)}  last active {when}  {device.MappingCount} mapping(s){notes}");
        }

        stdout.WriteLine(
            $"{overview.Mappings.Count} mapping(s): {overview.MappingsHere.Count} with a save folder here, {overview.LinkedHere.Count} linked here.");
        if (overview.Orphans.Count > 0)
        {
            stdout.WriteLine($"No device uses: {string.Join(", ", overview.Orphans)}. 'savetether map delete <id>' removes one.");
        }

        stdout.WriteLine($"Backups on this device: {overview.BackupCount}, {FormatBytes(overview.BackupBytes)}.");
    }

    private static void WriteRemoval(DeviceRemovalOutput removal, TextWriter stdout)
    {
        if (removal.Other is { } other)
        {
            stdout.WriteLine(removal.DryRun ? $"Would remove the device '{other}' from the sync folder:" : $"Removed the device '{other}' from the sync folder.");
            stdout.WriteLine($"  Its {removal.SharedFiles.Count} file(s) in the sync folder {(removal.DryRun ? "would be" : "were")} deleted. Nobody's links are touched.");
            stdout.WriteLine(removal.DryRun ? "Dry run: nothing was changed. Add --confirm-remove to go ahead." : "Its removal is recorded in the sync folder.");
            return;
        }

        stdout.WriteLine(removal.DryRun ? "Would remove this device from the sync folder:" : "Removed this device from the sync folder.");
        if (removal.DeleteEverything)
        {
            stdout.WriteLine(removal.DryRun
                ? "  The whole SaveTether folder in the sync folder would be deleted: every mapping and shared save. It cannot be undone."
                : "  The whole SaveTether folder in the sync folder was deleted.");
        }

        if (removal.GivenBack.Count > 0)
        {
            stdout.WriteLine(removal.DryRun
                ? "  Linked folders that become real folders again, with a copy of the shared saves:"
                : "  These folders are real folders again, yours to keep or delete:");
            foreach (var folder in removal.GivenBack)
            {
                stdout.WriteLine($"    {folder}");
            }
        }

        foreach (var skipped in removal.NotLinked)
        {
            stdout.WriteLine($"  '{skipped}' is not linked here, so its folder is left as it is.");
        }

        foreach (var orphan in removal.Orphans)
        {
            var what = orphan.Choice switch
            {
                OrphanChoice.DeleteMapping => "mapping deleted, shared saves kept",
                OrphanChoice.DeleteWithSaves => "mapping and shared saves deleted",
                _ => "kept in the sync folder",
            };
            stdout.WriteLine($"  Only this device used '{orphan.MappingId}': {(removal.DryRun ? "to be: " : string.Empty)}{what}.");
        }

        stdout.WriteLine($"  This device's {removal.SharedFiles.Count} file(s) in the sync folder {(removal.DryRun ? "would be" : "were")} deleted.");
        stdout.WriteLine(removal.BackupsDeleted > 0
            ? $"  {removal.BackupsDeleted} backup(s) {(removal.DryRun ? "would be" : "were")} deleted."
            : "  Backups are kept.");
        stdout.WriteLine(removal.DryRun
            ? "Dry run: nothing was changed. Add --confirm-remove to go ahead."
            : "SaveTether itself stays installed; to remove it, uninstall it (Settings, Apps on Windows; 'savetether uninstall' on Linux). Run 'init' to set this device up again.");
    }

    private static void WriteOperation(OperationOutput operation, TextWriter stdout, TextWriter stderr)
    {
        switch (operation.Outcome)
        {
            case OperationOutcome.Refused when operation.Refusal is { } refusal:
                stderr.Write(PlanRenderer.ToText(new PlanOutcome(null, refusal)));
                return;

            case OperationOutcome.Refused:
                stderr.WriteLine($"Nothing was changed{For(operation)}. {operation.Error}");
                return;

            case OperationOutcome.Cancelled:
                stderr.WriteLine($"Cancelled{For(operation)}. Nothing was changed.");
                return;

            case OperationOutcome.Planned:
                stdout.Write(PlanRenderer.ToText(new PlanOutcome(operation.Plan, null)));
                stdout.WriteLine("Dry run: nothing was changed.");
                return;

            case OperationOutcome.NothingToRecover:
                stdout.WriteLine("Nothing to recover: no operation is unfinished.");
                return;

            case OperationOutcome.Completed:
                if (operation.Plan is { } plan && Headline(plan) is { } headline)
                {
                    stdout.WriteLine(headline);
                    foreach (var note in plan.Notes)
                    {
                        stdout.WriteLine($"Note: {Hints.Compose(note)}");
                    }
                }

                WriteDetails(operation, stdout);
                return;

            default:
                stderr.WriteLine(operation.Error);
                WriteDetails(operation, stderr);
                return;
        }
    }

    private static void WriteDetails(OperationOutput operation, TextWriter writer)
    {
        foreach (var message in operation.Messages)
        {
            writer.WriteLine(message);
        }

        foreach (var backup in operation.BackupIds)
        {
            writer.WriteLine($"Backup: {backup}");
        }
    }

    /// <summary>The first line for a finished operation. Recovery says its own in its messages.</summary>
    private static string? Headline(Plan plan) => plan.Operation switch
    {
        PlanOperation.Revert => $"Reverted {plan.MappingId}: '{plan.Source}' is a real folder again.",
        PlanOperation.Restore => $"Restored the backup into '{plan.Target}'.",
        _ when plan.Classification == Classification.AlreadyApplied => $"Already applied: {plan.MappingId}. The link was checked and recorded.",
        _ => $"Applied {plan.MappingId}: '{plan.Source}' now links to '{plan.Target}'.",
    };

    private static void WriteBackups(IReadOnlyList<BackupInfo> backups, TextWriter stdout)
    {
        if (backups.Count == 0)
        {
            stdout.WriteLine("No backups.");
        }

        foreach (var backup in backups)
        {
            var when = backup.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
            stdout.WriteLine(
                $"{backup.Id}  {when}  {Describe(backup.Reason),-11}  {backup.FileCount} file(s), {backup.TotalBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes  from {backup.SourcePath}");
        }
    }

    private static string For(OperationOutput operation) => operation.MappingId is { } id ? $" for {id}" : string.Empty;

    private static string Describe(HealthState state) => state switch
    {
        HealthState.Ok => "OK",
        HealthState.NotApplied => "Not applied",
        HealthState.Replaced => "Replaced",
        HealthState.Broken => "Broken",
        HealthState.Retargeted => "Retargeted",
        HealthState.DoubleSeed => "Double seed",
        HealthState.FileConflict => "File conflict",
        _ => "Foreign link",
    };

    private static string Describe(BackupReason reason) => reason switch
    {
        BackupReason.PreApply => "pre-apply",
        BackupReason.PreRevert => "pre-revert",
        BackupReason.PreRestore => "pre-restore",
        BackupReason.PreDelete => "pre-delete",
        BackupReason.Baseline => "baseline",
        _ => "snapshot",
    };
}
