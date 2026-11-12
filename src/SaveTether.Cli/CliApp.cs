using SaveTether.Core;
using SaveTether.Core.Diagnostics;
using SaveTether.Core.Execution;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;

namespace SaveTether.Cli;

/// <summary>
/// The command line (design 5.12). It parses arguments, calls the core, and prints the result:
/// text by default, one JSON object with <c>--json</c>. It holds no logic about saves.
/// </summary>
internal static class CliApp
{
    /// <summary>Done, or nothing needed doing.</summary>
    public const int ExitSuccess = 0;

    /// <summary>A precondition refused the operation, or the owner has a choice to make. Nothing was changed.</summary>
    public const int ExitRefused = 1;

    /// <summary>An operation failed and was rolled back.</summary>
    public const int ExitRolledBack = 2;

    /// <summary>An operation is unfinished. Run "recover".</summary>
    public const int ExitRecoveryNeeded = 3;

    /// <summary>The command line was not understood (EX_USAGE). Nothing was done.</summary>
    public const int ExitUsage = 64;

    /// <summary>The self-test found this build does not work on this machine.</summary>
    public const int ExitSelfTestFailed = 70;

    private const string Unfinished = "An earlier operation did not finish. Run 'savetether recover' first.";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, CliEnvironment? environment = null)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            stdout.WriteLine(Usage);
            return ExitSuccess;
        }

        if (args is ["--version"])
        {
            stdout.WriteLine($"savetether {ProductInfo.Version}");
            return ExitSuccess;
        }

        // Not listed in the help: a diagnostic that CI runs against the native binary.
        if (args is ["--self-test"])
        {
            return SelfTest.Run(stdout) ? ExitSuccess : ExitSelfTestFailed;
        }

        // Decided before parsing, so that a command line that cannot be parsed is still answered in the form asked for.
        var json = args.TakeWhile(a => a != "--").Contains(ArgParser.Json);
        try
        {
            var command = ArgParser.Parse(args);
            if (command.Has(ArgParser.Help))
            {
                stdout.WriteLine(Usage);
                return ExitSuccess;
            }

            var output = new Commands(command, environment ?? CliEnvironment.FromSystem(), stdout).Run();
            OutputWriter.Write(output, json, stdout, stderr);
            return output.ExitCode;
        }
        catch (UsageException e)
        {
            if (json)
            {
                OutputWriter.Write(new CliOutput { Command = "usage", ExitCode = ExitUsage, Error = e.Message }, json, stdout, stderr);
            }
            else
            {
                stderr.WriteLine($"savetether: {e.Message}");
                stderr.WriteLine("Run 'savetether --help' for usage.");
            }

            return ExitUsage;
        }
    }

    internal const string Usage = """
        SaveTether: redirect save folders into a synced folder.

        Usage:
          savetether init --sync-root <path> [--device <name>] [--create-root] [--take-over] [--backup-root <path>]
              Set this device up. --create-root only on the first device ever; every other
              device waits for the sync tool to deliver the savetether folder.
              --take-over claims an existing device name, after a reinstall.

          savetether map add <id> --source <path> [--target <app>/<save folder>] [--process <name>]...
              Add a mapping, or set this device's save folder for an existing one.
              <id> is app.role, for example ppsspp.savedata. --target is needed for a new mapping.
              --process names the app's process, so apply can refuse while it runs.
          savetether map remove <id>
              Remove this device's save folder for a mapping. Other devices keep theirs.

          savetether status [<id>]
              Show each mapping's state on this device.

          savetether apply <id> [--copy | --no-copy | --add-missing | --replace-shared --confirm-replace]
                           [--assume-synced] [--assume-closed] [--dry-run]
          savetether apply --all [--assume-synced] [--assume-closed] [--dry-run]
              Link the save folder to the shared folder. When this device has saves, choose:
                --copy            they become the shared copy (only when the shared folder is empty)
                --no-copy         use the shared saves; this device's are kept in the backup only
                --add-missing     also add what the shared copy lacks; nothing is overwritten
                --replace-shared  they replace the shared copy on every device (needs --confirm-replace)
              --assume-synced  go ahead although the shared copy may not have fully arrived
              --assume-closed  go ahead although the app appears to be running
              --all stops at the first mapping that needs a choice.

          savetether revert <id> [--assume-closed] [--dry-run]
              Make the save folder a real folder again, holding a copy of the shared saves.

          savetether backup list [<id>]
          savetether backup restore <backup-id> [--confirm-replace] [--assume-closed] [--dry-run]
              Put a backup back where it was taken from. What is there now is backed up first.
              --confirm-replace is needed for a backup of a shared folder.

          savetether recover
              Undo or finish an operation that was interrupted.

          savetether --version
          savetether --help

        Every command accepts --json: one JSON object on standard output, and no questions asked.

        Exit codes: 0 done; 1 refused, nothing changed; 2 failed and rolled back;
        3 an operation is unfinished, run 'savetether recover'; 64 the command line was not understood.
        """;

    /// <summary>Runs one parsed command and returns what to print.</summary>
    private sealed class Commands(ParsedCommand command, CliEnvironment environment, TextWriter stdout)
    {
        private static readonly (string Flag, SetupChoice Choice)[] ChoiceFlags =
        [
            ("--copy", SetupChoice.Copy), ("--no-copy", SetupChoice.NoCopy),
            ("--add-missing", SetupChoice.AddMissing), ("--replace-shared", SetupChoice.ReplaceShared),
        ];

        private readonly LocalStore _local = new(environment.LocalFolder);

        private bool CanAsk => environment.Interactive && environment.Input is not null && !command.Has(ArgParser.Json);

        public CliOutput Run()
        {
            try
            {
                // While an operation is unfinished, only looking and recovering are allowed.
                var readOnly = command.Name is "status" or "backup list" or "recover";
                if (!readOnly && Journal.For(_local).IsOpen)
                {
                    return Failed(ExitRecoveryNeeded, Unfinished);
                }

                return command.Name switch
                {
                    "init" => Init(),
                    "map add" => MapAdd(),
                    "map remove" => MapRemove(),
                    "status" => Status(),
                    "apply" => Apply(),
                    "revert" => Single(
                        ws => ws.PlanRevert(command.Positionals[0], command.Has("--assume-closed")), command.Positionals[0]),
                    "backup list" => new CliOutput
                    {
                        Command = command.Name, ExitCode = ExitSuccess,
                        Backups = Open().ListBackups(command.FirstPositional),
                    },
                    "backup restore" => Restore(),
                    _ => Recover(),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The core's refusals outside the planner. Each says what is wrong and what to do; nothing was changed.
                return Failed(ExitRefused, e.Message);
            }
        }

        private CliOutput Init()
        {
            var syncRoot = Path.GetFullPath(command.Value("--sync-root") ?? throw new UsageException("'init' needs --sync-root <path>"));
            var backupRoot = command.Value("--backup-root") is { } given ? Path.GetFullPath(given) : null;
            var existing = Workspace.ExistingDevices(syncRoot);
            var known = existing.Count == 0 ? string.Empty : $" Devices already set up here: {string.Join(", ", existing)}.";

            var device = command.Value("--device");
            if (device is null)
            {
                if (!CanAsk)
                {
                    return Failed(
                        ExitRefused,
                        "Give this device a name with --device <name>." + known +
                        (existing.Count == 0 ? string.Empty : " To restore one of them after a reinstall, use its name with --take-over."));
                }

                device = Ask($"Name for this device (lowercase letters, digits, '-' and '_').{known}\nDevice name: ");
                if (string.IsNullOrEmpty(device))
                {
                    return Failed(ExitRefused, "No device name was given. Nothing was changed.");
                }
            }

            InitRequest Request(bool takeOver) => new(
                syncRoot, device, command.Has("--create-root"), takeOver, backupRoot, environment.DefaultBackupRoot);
            try
            {
                return new CliOutput
                {
                    Command = command.Name, ExitCode = ExitSuccess, Init = Workspace.Init(_local, Request(command.Has("--take-over"))),
                };
            }
            catch (StoreException e) when (e.Problem == StoreProblem.NameInUse)
            {
                var confirmed = CanAsk && Confirm(
                    $"A device named '{device}' is already set up. Take the name over only if this is that device after a reinstall: " +
                    "two machines using one name would apply each other's paths.\nTake it over?");
                return confirmed
                    ? new CliOutput { Command = command.Name, ExitCode = ExitSuccess, Init = Workspace.Init(_local, Request(takeOver: true)) }
                    : Failed(ExitRefused, e.Message + " To take the name over, add --take-over.");
            }
        }

        private CliOutput MapAdd()
        {
            var id = command.Positionals[0];
            var source = Path.GetFullPath(command.Value("--source") ?? throw new UsageException("'map add' needs --source <path>"));
            var result = Open().MapAdd(id, command.Value("--target"), source, command.Values("--process"));
            return new CliOutput
            {
                Command = command.Name, ExitCode = ExitSuccess,
                Mapping = new MappingOutput(
                    id, result.Mapping.Target, result.Path.Source, result.Path.ProcessNames, result.CreatedMapping, Removed: false),
            };
        }

        private CliOutput MapRemove()
        {
            var id = command.Positionals[0];
            var workspace = Open();
            var removed = workspace.MapRemove(id);
            var target = workspace.Shared.TryReadMapping(id)?.Target ?? string.Empty;
            return new CliOutput
            {
                Command = command.Name, ExitCode = ExitSuccess,
                Mapping = new MappingOutput(id, target, removed.Source, removed.ProcessNames, CreatedMapping: false, Removed: true),
            };
        }

        private CliOutput Status()
        {
            var report = Open().Status(command.FirstPositional);
            return new CliOutput
            {
                Command = command.Name, ExitCode = report.JournalOpen ? ExitRecoveryNeeded : ExitSuccess, Status = report,
            };
        }

        private CliOutput Apply()
        {
            var all = command.Has("--all");
            var id = command.FirstPositional;
            var chosen = ChoiceFlags.Where(c => command.Has(c.Flag)).ToList();
            if (all == (id is not null))
            {
                throw new UsageException(all ? "'apply' takes a mapping ID or --all, not both" : "'apply' needs a mapping ID, or --all");
            }

            if (chosen.Count > 1)
            {
                throw new UsageException($"choose one of {string.Join(", ", chosen.Select(c => c.Flag))}, not several");
            }

            if (all && chosen.Count > 0)
            {
                throw new UsageException($"'{chosen[0].Flag}' cannot be combined with --all: each mapping's choice is its own. Apply that mapping by its ID");
            }

            SetupChoice? choice = chosen.Count == 1 ? chosen[0].Choice : null;
            if (command.Has("--confirm-replace") && choice != SetupChoice.ReplaceShared)
            {
                throw new UsageException("'--confirm-replace' only goes with --replace-shared");
            }

            var request = new ApplyRequest(
                choice, command.Has("--confirm-replace"), command.Has("--assume-synced"), command.Has("--assume-closed"));
            var workspace = Open();
            var operations = new List<OperationOutput>();
            foreach (var mappingId in all ? workspace.MappingsWithPaths() : [id!])
            {
                operations.Add(RunOne(
                    workspace, mappingId,
                    confirmed => workspace.PlanApply(mappingId, request with { ConfirmReplace = request.ConfirmReplace || confirmed })));
                if (ExitCodeOf(operations[^1]) != ExitSuccess)
                {
                    break;
                }
            }

            return new CliOutput
            {
                Command = command.Name,
                ExitCode = operations.Count == 0 ? ExitSuccess : ExitCodeOf(operations[^1]),
                Operations = operations,
                Message = operations.Count == 0 ? "No mapping has a save folder set on this device. Add one with 'savetether map add'." : null,
            };
        }

        private CliOutput Restore()
        {
            var backupId = command.Positionals[0];
            var request = new RestoreRequest(command.Has("--confirm-replace"), command.Has("--assume-closed"));
            return Single(
                (ws, confirmed) => ws.PlanRestore(backupId, request with { ConfirmReplace = request.ConfirmReplace || confirmed }),
                backupId.Split('/')[0]);
        }

        private CliOutput Recover()
        {
            var result = Open().Recover();
            var operation = new OperationOutput(
                result.MappingId, result.Operation, OutcomeOf(result.Status), null, null, result.BackupIds, result.Messages, result.Error);

            // Undoing the unfinished operation is recovery succeeding.
            return new CliOutput
            {
                Command = command.Name,
                ExitCode = result.Status == ExecutionStatus.RecoveryNeeded ? ExitRecoveryNeeded : ExitSuccess,
                Operations = [operation],
            };
        }

        private CliOutput Single(Func<Workspace, PlanOutcome> plan, string mappingId) => Single((ws, _) => plan(ws), mappingId);

        private CliOutput Single(Func<Workspace, bool, PlanOutcome> plan, string mappingId)
        {
            var workspace = Open();
            var operation = RunOne(workspace, mappingId, confirmed => plan(workspace, confirmed));
            return new CliOutput { Command = command.Name, ExitCode = ExitCodeOf(operation), Operations = [operation] };
        }

        /// <summary>Plans one operation, asks what must be asked when a person is there to answer, and runs it.</summary>
        /// <param name="plan">Makes the plan. Its argument is true once the owner confirmed replacing shared data at a prompt.</param>
        private OperationOutput RunOne(Workspace workspace, string mappingId, Func<bool, PlanOutcome> plan)
        {
            OperationOutput Ended(OperationOutcome outcome, Plan? made = null, Refusal? refusal = null, string? error = null) =>
                new(mappingId, made?.Operation ?? refusal?.Operation, outcome, made, refusal, [], [], error);

            PlanOutcome outcome;
            try
            {
                outcome = plan(false);
                if (outcome.Refusal is { Kind: RefusalKind.ConfirmationNeeded } unconfirmed && CanAsk)
                {
                    if (!Confirm(unconfirmed.Message.Replace(" Add --confirm-replace to go ahead.", string.Empty, StringComparison.Ordinal) + "\nReplace the shared saves?"))
                    {
                        return Ended(OperationOutcome.Cancelled, refusal: unconfirmed);
                    }

                    outcome = plan(true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Ended(OperationOutcome.Refused, error: e.Message);
            }

            if (outcome.Plan is not { } made)
            {
                return Ended(OperationOutcome.Refused, refusal: outcome.Refusal);
            }

            if (command.Has(ArgParser.DryRun))
            {
                return Ended(OperationOutcome.Planned, made);
            }

            if (made.NeedsFirstSeedConfirmation && CanAsk && !Confirm(
                "No other device has set this mapping up, as far as this device can see. " +
                "If you already set it up on another device, wait until that has synced.\nMake this device's saves the shared copy?"))
            {
                return Ended(OperationOutcome.Cancelled, made);
            }

            var result = workspace.Run(made);
            return new OperationOutput(
                mappingId, made.Operation, OutcomeOf(result.Status), made, null, result.BackupIds, result.Messages, result.Error);
        }

        private static OperationOutcome OutcomeOf(ExecutionStatus status) => status switch
        {
            ExecutionStatus.Completed => OperationOutcome.Completed,
            ExecutionStatus.Refused => OperationOutcome.Refused,
            ExecutionStatus.RolledBack => OperationOutcome.RolledBack,
            ExecutionStatus.RecoveryNeeded => OperationOutcome.RecoveryNeeded,
            _ => OperationOutcome.NothingToRecover,
        };

        private static int ExitCodeOf(OperationOutput operation) => operation.Outcome switch
        {
            OperationOutcome.Planned or OperationOutcome.Completed or OperationOutcome.NothingToRecover => ExitSuccess,
            OperationOutcome.RolledBack => ExitRolledBack,
            OperationOutcome.RecoveryNeeded => ExitRecoveryNeeded,
            _ when operation.Refusal?.Failures.Any(f => f.Kind == PrecheckKind.UnfinishedOperation) == true => ExitRecoveryNeeded,
            _ => ExitRefused,
        };

        private Workspace Open() => Workspace.Open(_local, environment.Processes, environment.Time, environment.Faults);

        private CliOutput Failed(int exitCode, string error) => new() { Command = command.Name, ExitCode = exitCode, Error = error };

        private string? Ask(string question)
        {
            stdout.Write(question);
            stdout.Flush();
            return environment.Input!.ReadLine()?.Trim();
        }

        private bool Confirm(string question) =>
            Ask(question + " Type 'yes' to go ahead: ") is { } answer && answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
