using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SaveTether.Core.Backup;
using SaveTether.Core.Health;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;

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

internal sealed record MappingOutput(
    string Id, string Target, string Source, IReadOnlyList<string> ProcessNames, bool CreatedMapping, bool Removed);

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

    public string? Message { get; init; }

    public InitResult? Init { get; init; }

    public MappingOutput? Mapping { get; init; }

    public StatusReport? Status { get; init; }

    public IReadOnlyList<OperationOutput>? Operations { get; init; }

    public IReadOnlyList<BackupInfo>? Backups { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliOutput))]
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
            stdout.WriteLine($"Note: {note}");
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

        stdout.WriteLine($"No saves were touched. Run 'savetether apply {mapping.Id}' to link it.");
    }

    private static void WriteStatus(StatusReport status, TextWriter stdout, TextWriter stderr)
    {
        stdout.WriteLine($"Device '{status.DeviceName}', sync root '{status.SyncRoot}'");
        if (status.Mappings.Count == 0)
        {
            stdout.WriteLine("No mappings yet. Add one with 'savetether map add'.");
        }

        var width = status.Mappings.Select(m => m.MappingId.Length).DefaultIfEmpty(0).Max();
        foreach (var mapping in status.Mappings)
        {
            stdout.WriteLine($"  {mapping.MappingId.PadRight(width)}  {Describe(mapping.State),-12}  {mapping.Detail}");
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
                        stdout.WriteLine($"Note: {note}");
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
