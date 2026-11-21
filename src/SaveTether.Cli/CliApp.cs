using SaveTether.Core;
using SaveTether.Core.Diagnostics;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Startup;
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

          savetether map add <id> --source <path> [--target <app>/<save folder>] [--process <name>]... [--entry-depth <n>]
              Add a mapping, or set this device's save folder for an existing one.
              <id> is app.role, for example ppsspp.savedata. --target is needed for a new mapping.
              --process names the app's process, so apply can refuse while it runs.
              --entry-depth says how many folder levels down one game's saves sit (default 1). It is
              the unit --add-missing copies in. Give it again later to change it.
          savetether map remove <id>
              Remove this device's save folder for a mapping. Other devices keep theirs.
          savetether map delete <id> [--confirm-delete]
              Delete a mapping no device uses, from the shared folder, for every device: its shared saves,
              its record and its seed markers. The shared saves are backed up on this device first. It cannot
              be undone from the share, so it asks first, or needs --confirm-delete. Refused while any device
              has a folder set for it.

          savetether app add <app> [--install <id>] [--role <id>]... [--install-dir <path>]
                                   [--sd-card <path>] [--wine-prefix <path>] [--dry-run]
              Add an app from its definition: find its install on this device, show its save
              folders with their sizes, and add a mapping for each. Links nothing; run apply next.
              --install picks the install when more than one is found. --role picks roles other
              than the default ones. The path options give what cannot be found by looking; they
              are remembered for this device, so running app add again later finds moved folders.
          savetether app list [--found] [--search <text>]
              List the apps that have a definition: built in, imported (unverified), or in the
              shared definitions folder. --search keeps those whose name or ID contains the text,
              or whose Steam app ID it is.

          savetether status [<id>]
              Show each mapping's state on this device.

          savetether apply <id> [--copy | --no-copy | --add-missing | --replace-shared --confirm-replace]
                           [--assume-synced] [--assume-closed] [--grant-sandbox] [--dry-run]
          savetether apply --all [--assume-synced] [--assume-closed] [--grant-sandbox] [--dry-run]
              Link the save folder to the shared folder. When this device has saves, choose:
                --copy            they become the shared copy (only when the shared folder is empty)
                --no-copy         use the shared saves; this device's are kept in the backup only
                --add-missing     also add what the shared copy lacks; nothing is overwritten
                --replace-shared  they replace the shared copy on every device (needs --confirm-replace)
              --assume-synced  go ahead although the shared copy may not have fully arrived
              --assume-closed  go ahead although the app appears to be running
              --grant-sandbox  when a Flatpak app cannot write the shared folder, allow it (that
                               folder only, for this user) instead of stopping
              --all stops at the first mapping that needs a choice.

          savetether revert <id> [--assume-closed] [--dry-run]
              Make the save folder a real folder again, holding a copy of the shared saves.

          savetether backup list [<id>]
          savetether backup restore <backup-id> [--confirm-replace] [--assume-closed] [--dry-run]
              Put a backup back where it was taken from. What is there now is backed up first.
              --confirm-replace is needed for a backup of a shared folder.
          savetether backup delete <backup-id> [--confirm-delete]
              Delete one backup. It cannot be undone, so it asks first, or needs --confirm-delete.
              The first backup of a folder is usually the only copy from before SaveTether changed it.

          savetether recover
              Undo or finish an operation that was interrupted.

          savetether uninstall [--confirm-uninstall]
              Linux: remove SaveTether's programs, which the desktop app installed for this user, and
              its app menu entry; a running SaveTether is asked to quit first. Your setup, linked
              folders, backups and the sync folder stay. It asks first, or needs --confirm-uninstall. On Windows, use Settings > Apps instead.

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
                // One operation at a time on this device, across processes. Taken before the journal is
                // looked at: while another process runs an operation its journal is open, and that is
                // not an unfinished operation to recover.
                using var held = Writes ? OperationLock.Acquire(_local, "cli", command.Name) : null;

                // While an operation is unfinished, only looking and recovering are allowed.
                var readOnly = command.Name is "status" or "backup list" or "recover" or "app list"
                    || (command.Name == "app add" && command.Has(ArgParser.DryRun));
                if (!readOnly && Journal.For(_local).IsOpen)
                {
                    return Failed(ExitRecoveryNeeded, Unfinished);
                }

                return command.Name switch
                {
                    "init" => Init(),
                    "map add" => MapAdd(),
                    "map remove" => MapRemove(),
                    "map delete" => MapDelete(),
                    "app add" => AppAdd(),
                    "app list" => AppList(),
                    "status" => Status(),
                    "apply" => Apply(),
                    "revert" => Revert(),
                    "backup list" => new CliOutput
                    {
                        Command = command.Name, ExitCode = ExitSuccess,
                        Backups = Open().ListBackups(command.FirstPositional),
                    },
                    "backup restore" => Restore(),
                    "backup delete" => DeleteBackup(),
                    "uninstall" => Uninstall(),
                    _ => Recover(),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The core's refusals outside the planner. Each says what is wrong and what to do; nothing was changed.
                var store = e as StoreException;
                return Failed(ExitRefused, Hints.ErrorText(e)) with { Problem = store?.Problem, Conflict = store?.Conflict };
            }
        }

        /// <summary>
        /// Whether the command changes anything, and so takes the operation lock: everything but looking,
        /// and a dry run. Recovery writes, so it takes the lock too.
        /// </summary>
        /// <remarks>
        /// <c>init</c> on a device never set up has no local folder yet, and so no journal or state to
        /// protect. Taking the lock there would create the folder, and a refused <c>init</c> must leave
        /// nothing behind, so it goes without. Two first-time <c>init</c>s at the same instant are not
        /// guarded against.
        /// </remarks>
        private bool Writes =>
            command.Name is not ("status" or "backup list" or "app list" or "uninstall")
            && !command.Has(ArgParser.DryRun)
            && (command.Name != "init" || Directory.Exists(_local.Folder));

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
            int? entryDepth = null;
            if (command.Value("--entry-depth") is { } given)
            {
                if (!int.TryParse(given, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var depth)
                    || depth is < 1 or > MappingFile.MaxEntryDepth)
                {
                    throw new UsageException($"'--entry-depth' needs a whole number from 1 to {MappingFile.MaxEntryDepth}");
                }

                entryDepth = depth;
            }

            var result = Open().MapAdd(id, command.Value("--target"), source, command.Values("--process"), entryDepth);
            return new CliOutput
            {
                Command = command.Name, ExitCode = ExitSuccess,
                Mapping = new MappingOutput(
                    id, result.Mapping.Target, result.Path.Source, result.Path.ProcessNames, result.Mapping.Depth,
                    result.CreatedMapping, result.EntryDepthChanged, Removed: false),
            };
        }

        private CliOutput MapRemove()
        {
            var id = command.Positionals[0];
            var workspace = Open();
            var removed = workspace.MapRemove(id);
            var mapping = workspace.Shared.TryReadMapping(id);
            return new CliOutput
            {
                Command = command.Name, ExitCode = ExitSuccess,
                Mapping = new MappingOutput(
                    id, mapping?.Target ?? string.Empty, removed.Source, removed.ProcessNames, mapping?.Depth ?? 1,
                    CreatedMapping: false, EntryDepthChanged: false, Removed: true),
            };
        }

        /// <summary>
        /// "map delete": a mapping no device uses, after a confirmation at a terminal or with --confirm-delete. The
        /// deletion reaches every device through the sync tool, so what goes is said before it is asked.
        /// </summary>
        private CliOutput MapDelete()
        {
            var id = command.Positionals[0];
            var workspace = Open();
            var described = workspace.DescribeMappingDeletion(id);
            if (!command.Has("--confirm-delete"))
            {
                var what = described.TargetExists
                    ? $"its shared folder '{described.TargetFolder}' ({described.Files} file(s), {OutputWriter.FormatBytes(described.Bytes)})"
                    : "its record (the shared folder is already gone)";
                if (!CanAsk)
                {
                    return Failed(ExitRefused, $"Deleting '{id}' removes {what} from the shared folder, on every device. Add --confirm-delete to go ahead.");
                }

                var question =
                    $"Delete '{id}' from the shared folder? This removes {what}, and the deletion reaches every device through the sync tool." +
                    "\nThe shared saves are backed up on this device first.";
                if (!Confirm(question))
                {
                    return Failed(ExitRefused, "Cancelled. Nothing was deleted.");
                }
            }

            return new CliOutput { Command = command.Name, ExitCode = ExitSuccess, MappingDeleted = workspace.DeleteMapping(id) };
        }

        private CliOutput AppAdd()
        {
            var appId = command.Positionals[0];
            var request = new AppAddRequest(
                appId,
                command.Value("--install"),
                command.Values("--role"),
                command.Value("--install-dir"),
                command.Value("--sd-card"),
                command.Value("--wine-prefix"));
            var workspace = Open();
            var outcome = workspace.PlanAppAdd(request, environment.Resolver);

            // Several installs found: a person at the terminal picks one; otherwise refuse and say how to choose.
            if (outcome.Problem == AppAddProblem.InstallChoiceNeeded && CanAsk)
            {
                var found = outcome.Candidates.Where(c => c.IsDetected).ToList();
                var question = $"{outcome.Message?.Text}\n" +
                    string.Concat(found.Select((c, i) => $"  {i + 1}. {c.Id}  ({c.Found})\n")) +
                    "Which install? Type its number: ";
                if (!int.TryParse(Ask(question), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
                    || number < 1 || number > found.Count)
                {
                    return AppFailed(appId, outcome, "No install was chosen. Nothing was changed.");
                }

                outcome = workspace.PlanAppAdd(request with { Install = found[number - 1].Id }, environment.Resolver);
            }

            if (outcome.Plan is not { } plan)
            {
                var error = Hints.Compose(outcome.Message!);
                return AppFailed(appId, outcome, outcome.Problem == AppAddProblem.UnknownApp ? error + " Find one with 'savetether app list --search <name>'." : error);
            }

            var dryRun = command.Has(ArgParser.DryRun);
            if (!dryRun)
            {
                workspace.AddApp(plan);
            }

            return new CliOutput
            {
                Command = command.Name, ExitCode = ExitSuccess,
                App = new AppOutput(appId, Added: !dryRun, plan, null, outcome.Candidates),
            };
        }

        private CliOutput AppFailed(string appId, AppAddOutcome outcome, string error) => new()
        {
            Command = command.Name, ExitCode = ExitRefused, Error = error,
            App = new AppOutput(appId, Added: false, null, outcome.Problem, outcome.Candidates, outcome.Conflict),
        };

        private CliOutput AppList()
        {
            var workspace = Open();
            var catalog = workspace.Definitions;
            var search = command.Value("--search");
            if (command.Has("--found"))
            {
                // The same match as the plain list's --search (name, ID or Steam app ID), so the two agree.
                var matching = search is null ? null : catalog.Search(search).ToHashSet(StringComparer.Ordinal);
                var found = workspace.ScanFound(environment.Resolver, SaveTether.Core.Detection.ScanOptions.Default)
                    .Where(f => matching is null || matching.Contains(f.Hit.AppId))
                    .OrderByDescending(f => f.Hit.NewestWrite ?? DateTime.MinValue)
                    .ThenBy(f => f.Entry.Definition!.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new FoundEntry(f.Hit.AppId, f.Entry.Definition!.Name, f.Entry.Source, f.Hit.InstallId, f.Hit.SaveFolder, f.Hit.NewestWrite))
                    .ToList();
                return new CliOutput { Command = command.Name, ExitCode = ExitSuccess, Found = found };
            }

            var ids = search is null ? catalog.AvailableIds() : catalog.Search(search);
            var apps = catalog.FindAll(ids)
                .Select(e => new AppListEntry(e.Id, e.Entry.Definition?.Name, e.Entry.Source, e.Entry.IsValid ? null : string.Join("; ", e.Entry.Errors)))
                .ToList();
            return new CliOutput
            {
                Command = command.Name, ExitCode = ExitSuccess, Apps = apps,
                Message = workspace.Shared.IsInitialized
                    ? null
                    : $"Only the built-in and imported definitions are listed: the shared ones could not be read. {workspace.Shared.NotAvailableMessage}",
            };
        }

        private CliOutput Status()
        {
            var report = Open().Status(command.FirstPositional) with
            {
                AutostartMissing = SaveTether.Core.Startup.Autostart.Check(environment.Autostart).MissingProgram,
            };
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

        /// <summary>
        /// Revert, then: when SaveTether's grant to a Flatpak app is no longer used by any mapping
        /// here, offer to take it back. Without a person to ask, only say how.
        /// </summary>
        private CliOutput Revert()
        {
            var id = command.Positionals[0];
            var workspace = Open();
            var output = Single(ws => ws.PlanRevert(id, command.Has("--assume-closed")), id);
            if (output.Operations is not [{ Outcome: OperationOutcome.Completed } operation]
                || workspace.SandboxGrantNoLongerNeeded(id) is not { } grant)
            {
                return output;
            }

            var folders = string.Join(" and ", grant.Paths.Select(p => $"'{p}'"));
            string note;
            if (CanAsk && Confirm(
                $"The Flatpak app {grant.AppId} was allowed to write {folders} for SaveTether, and no mapping here needs it now. " +
                $"Take that back by running:\n  {grant.RemoveCommand}\n(Other permissions of the app are left alone.)\nTake it back?"))
            {
                note = workspace.RevokeSandbox(grant)
                    ? $"Took back the Flatpak app {grant.AppId}'s access to {folders}."
                    : $"'{grant.RemoveCommand}' did not succeed. The app can still write {folders}.";
            }
            else
            {
                note = $"The Flatpak app {grant.AppId} can still write {folders}. To take that back: {grant.RemoveCommand}";
            }

            return output with { Operations = [operation with { Messages = [.. operation.Messages, note] }] };
        }

        private CliOutput Restore()
        {
            var backupId = command.Positionals[0];
            var request = new RestoreRequest(command.Has("--confirm-replace"), command.Has("--assume-closed"));
            return Single(
                (ws, confirmed) => ws.PlanRestore(backupId, request with { ConfirmReplace = request.ConfirmReplace || confirmed }),
                backupId.Split('/')[0]);
        }

        /// <summary>
        /// "backup delete": one backup, after a confirmation at a terminal or with --confirm-delete. The first
        /// backup of a mapping gets a stronger warning, since it is usually the only copy from before SaveTether.
        /// </summary>
        private CliOutput DeleteBackup()
        {
            var backupId = command.Positionals[0];
            var workspace = Open();
            var deletion = workspace.DescribeBackupDeletion(backupId);
            if (!command.Has("--confirm-delete"))
            {
                if (!CanAsk)
                {
                    return Failed(ExitRefused, $"Deleting backup '{backupId}' cannot be undone. Add --confirm-delete to go ahead.");
                }

                var backup = deletion.Backup;
                var first = deletion.WasFirst
                    ? $"\nThis is the first backup of {backup.MappingId}: usually the only copy of '{backup.SourcePath}' from before SaveTether changed it."
                    : string.Empty;
                var question =
                    $"Delete backup '{backupId}' ({backup.FileCount} file(s), {OutputWriter.FormatBytes(backup.TotalBytes)}, of '{backup.SourcePath}')?{first}" +
                    "\nIt cannot be undone.";
                if (!Confirm(question))
                {
                    return Failed(ExitRefused, "Cancelled. Nothing was deleted.");
                }
            }

            var deleted = workspace.DeleteBackup(backupId);
            return new CliOutput { Command = command.Name, ExitCode = ExitSuccess, BackupDeleted = deleted };
        }

        /// <summary>
        /// "uninstall" (Linux): removes what the desktop app's install put in place, after a confirmation at a terminal or
        /// with --confirm-uninstall. Changes no setup, link, backup or shared file. On Windows the installer's own
        /// uninstaller does this.
        /// </summary>
        private CliOutput Uninstall()
        {
            if (environment.Install is not { } layout)
            {
                return Failed(ExitRefused, "On Windows, SaveTether is uninstalled from Settings > Apps > Installed apps. Nothing was changed.");
            }

            if (SafeFs.Probe(layout.AppPath) != EntryKind.File && SafeFs.Probe(layout.CliPath) != EntryKind.File)
            {
                return Failed(ExitRefused, $"SaveTether is not installed in {layout.AppFolder}. Nothing was changed.");
            }

            if (!command.Has("--confirm-uninstall"))
            {
                if (!CanAsk)
                {
                    return Failed(ExitRefused, "Uninstalling removes SaveTether's programs. Add --confirm-uninstall to go ahead.");
                }

                var question =
                    $"Remove SaveTether's programs from {layout.AppFolder} and {layout.BinFolder}, and its app menu entry?" +
                    " A running SaveTether is asked to quit first." +
                    "\nYour setup, linked folders, backups and the sync folder stay; linked folders keep syncing, but nothing checks on them.";
                if (!Confirm(question))
                {
                    return Failed(ExitRefused, "Cancelled. Nothing was removed.");
                }
            }

            // The installed app, left running, would go on from removed files until the end of the session.
            var stillRunning = Installer.QuitRunning(layout, environment.LocalFolder, environment.RunningPrograms, environment.QuitWait);
            var (removed, left) = Installer.Uninstall(layout, environment.Autostart);
            var leftAll = left.Concat(stillRunning.Select(id =>
                $"SaveTether is still running (process {id}) and did not quit when asked: quit it from its tray menu.")).ToList();
            return new CliOutput { Command = command.Name, ExitCode = ExitSuccess, Uninstalled = new UninstallOutput(removed, leftAll) };
        }

        private CliOutput Recover()
        {
            var result = Open().Recover();
            var operation = new OperationOutput(
                result.MappingId, result.Operation, OutcomeOf(result.Status), null, null, result.BackupIds, Hints.Compose(result.Messages), Hints.ComposeOrNull(result.Error));

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
            string? granted = null;
            try
            {
                outcome = plan(false);

                // A Flatpak app that cannot write the shared folder: allow it, if the owner agrees, then plan again.
                // Never on a dry run, which changes nothing, and only when that is all that stands in the way.
                if (outcome.Refusal is { Kind: RefusalKind.Precondition, Failures: [_, ..] failures }
                    && failures.All(f => f.Kind == PrecheckKind.SandboxCannotReach)
                    && !command.Has(ArgParser.DryRun)
                    && workspace.CheckSandbox(mappingId) is { CanReach: false } reach)
                {
                    var agreed = command.Has("--grant-sandbox") || (CanAsk && Confirm(
                        $"The Flatpak app {reach.AppId} cannot write the shared folder, so the link would not work inside it. " +
                        $"SaveTether can allow it, for this user and only {string.Join(" and ", reach.Grants.Select(g => $"'{g}'"))}, by running:\n" +
                        $"  {reach.FixCommand}\nAllow it?"));
                    if (agreed)
                    {
                        if (!workspace.GrantSandbox(reach))
                        {
                            return Ended(OperationOutcome.Refused, error: $"'{reach.FixCommand}' did not succeed. Nothing was changed.");
                        }

                        granted = $"Allowed the Flatpak app {reach.AppId} to write {string.Join(" and ", reach.Grants.Select(g => $"'{g}'"))}.";
                        outcome = plan(false);
                    }
                }

                if (outcome.Refusal is { Kind: RefusalKind.ConfirmationNeeded } unconfirmed && CanAsk)
                {
                    if (!Confirm(unconfirmed.Message.Text + "\nReplace the shared saves?"))
                    {
                        return Ended(OperationOutcome.Cancelled, refusal: unconfirmed);
                    }

                    outcome = plan(true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Ended(OperationOutcome.Refused, error: Hints.ErrorText(e));
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
                mappingId, made.Operation, OutcomeOf(result.Status), made, null, result.BackupIds,
                granted is null ? Hints.Compose(result.Messages) : [granted, .. Hints.Compose(result.Messages)], Hints.ComposeOrNull(result.Error));
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

        private Workspace Open() => Workspace.Open(_local, environment.Processes, environment.Time, environment.Faults, environment.Flatpak);

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
