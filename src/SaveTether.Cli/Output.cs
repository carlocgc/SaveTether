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

    public IReadOnlyList<OperationOutput>? Operations { get; init; }

    public IReadOnlyList<BackupInfo>? Backups { get; init; }

    /// <summary>"backup delete": the backup removed, whether it was its mapping's first, and how many are left.</summary>
    public BackupDeletion? BackupDeleted { get; init; }

    /// <summary>"uninstall": what was removed, and what was left and why.</summary>
    public UninstallOutput? Uninstalled { get; init; }
}

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
        else if (status.Mappings.Count == 0)
        {
            stdout.WriteLine("No mappings yet. Add one with 'savetether map add'.");
        }

        var width = status.Mappings.Select(m => m.MappingId.Length).DefaultIfEmpty(0).Max();
        foreach (var mapping in status.Mappings)
        {
            stdout.WriteLine($"  {mapping.MappingId.PadRight(width)}  {Describe(mapping.State),-12}  {Hints.Compose(mapping.Detail)}");
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

        if (status.AutostartMissing is { } program)
        {
            stderr.WriteLine(
                $"Warning: SaveTether is set to start with the session, but the entry points to '{program}', which is missing. " +
                "Start the desktop app from where it is now to fix the entry, or turn starting with the session off in its settings.");
        }
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
        _ => "Foreign link",
    };

    private static string Describe(BackupReason reason) => reason switch
    {
        BackupReason.PreApply => "pre-apply",
        BackupReason.PreRevert => "pre-revert",
        BackupReason.PreRestore => "pre-restore",
        BackupReason.Baseline => "baseline",
        _ => "snapshot",
    };
}
