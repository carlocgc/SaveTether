using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Operations;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// Apply, Revert and Recover from the window (desktop-app-spec.md, sections 7 and 9). Each takes the
/// operation lock for its whole length, plans, shows a refusal with only the actions it allows or the
/// confirmation, runs with progress, and shows the result. Planning and running are off the UI thread.
/// </summary>
internal sealed class ActionFlow(string localFolder, Overlay overlay, GuiLog log)
{
    private static readonly IProcessLister Processes = new SystemProcessLister();

    private readonly LocalStore _local = new(localFolder);

    /// <summary>Unsafe mode ("Back up before actions" off): the texts then promise no backup. Read each time, as the core does.</summary>
    private bool NoBackups => !new DeviceSettings(localFolder).BackupBeforeActions;

    /// <summary>
    /// Work off the UI thread behind a busy dialog (<see cref="Overlay.WorkAsync{T}"/>): the page is locked at once,
    /// so nothing can be opened meanwhile, and the dialog shows if the work is not quick.
    /// </summary>
    private Task<T> Work<T>(string what, Func<T> work) => overlay.WorkAsync("Working", what, work);

    private Task<bool> Work(string what, Action work) => overlay.WorkAsync("Working", what, () =>
    {
        work();
        return true;
    });

    /// <summary>
    /// Checks now and shows the result, from inside an operation at a point where it holds no lock: after an add,
    /// before "Apply it now?", so the page behind the question shows the app just added. Set by the app.
    /// </summary>
    public Func<Task> Refresh { get; set; } = () => Task.CompletedTask;

    public Task ApplyAsync(MappingState mapping, bool useThisCopy)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return ApplyAsync(new Subject(mapping.MappingId, mapping.AppName, mapping.Role), useThisCopy);
    }

    /// <summary>Apply for each mapping just added (section 8, "Apply it now?"): each its own operation, in turn.</summary>
    public async Task ApplyAddedAsync(string appName, IReadOnlyList<string> mappingIds)
    {
        ArgumentNullException.ThrowIfNull(mappingIds);
        foreach (var id in mappingIds)
        {
            await ApplyAsync(new Subject(id, appName, id.Contains('.', StringComparison.Ordinal) ? id[(id.IndexOf('.', StringComparison.Ordinal) + 1)..] : id), useThisCopy: false);
        }
    }

    private async Task ApplyAsync(Subject mapping, bool useThisCopy)
    {
        var role = mapping.Name;
        var session = await BeginAsync("apply");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            var request = useThisCopy ? new ApplyRequest(SetupChoice.ReplaceShared, ConfirmReplace: true) : new ApplyRequest();
            while (true)
            {
                var outcome = await Work("Checking what applying involves…", () => session.PlanApply(mapping.MappingId, request));
                if (outcome.Plan is { } plan)
                {
                    await ConfirmAndRunAsync(session, plan, role, assumedSynced: request.AssumeSynced);
                    return;
                }

                var refusal = outcome.Refusal!;
                log.Write($"apply {mapping.MappingId} refused: {refusal.Kind}: {refusal.Message.Text}");
                switch (refusal.Kind)
                {
                    case RefusalKind.ChoiceNeeded:
                        if (await ChooseAsync(refusal, mapping, role, session.HasOtherDevices) is not { } choice)
                        {
                            return;
                        }

                        request = request with { Choice = choice, ConfirmReplace = choice == SetupChoice.ReplaceShared };
                        continue;

                    case RefusalKind.ConfirmationNeeded:
                        request = request with { ConfirmReplace = true };
                        continue;
                }

                var next = await HandleRefusalAsync(session, mapping, role, refusal);
                if (next is null)
                {
                    return;
                }

                request = next.Value switch
                {
                    RefusalAction.AssumeClosed => request with { AssumeClosed = true },
                    RefusalAction.AssumeSynced => request with { AssumeSynced = true },
                    _ => request,
                };
            }
        }
    }

    public Task RevertAsync(MappingState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return RevertAsync(new Subject(state.MappingId, state.AppName, state.Role));
    }

    /// <summary>
    /// Restore (section 9.7): a backup of the shared copy is confirmed first, like replace-shared; then the plan,
    /// its refusals and confirmation as for apply. Refused because the save folder is linked, it offers Revert,
    /// which runs as its own operation once the restore's lock is released.
    /// </summary>
    public async Task RestoreAsync(BackupRow row, string appName, string roleName)
    {
        ArgumentNullException.ThrowIfNull(row);
        var mapping = new Subject(row.Info.MappingId, appName, roleName);
        var role = mapping.Name;
        var session = await BeginAsync("backup restore");
        if (session is null)
        {
            return;
        }

        var revertInstead = false;
        using (session)
        {
            var request = new RestoreRequest(ConfirmReplace: row.OfSharedCopy);
            while (true)
            {
                var outcome = await Work("Checking what restoring involves…", () => session.PlanRestore(row.Info.Id, request));
                if (outcome.Plan is { } plan)
                {
                    await ConfirmAndRunAsync(session, plan, role, row.OfSharedCopy ? null : BackupsPage.RestoreLocalText(row));
                    return;
                }

                var refusal = outcome.Refusal!;
                log.Write($"backup restore {row.Info.Id} refused: {refusal.Kind}: {refusal.Message.Text}");
                if (refusal.Kind == RefusalKind.ConfirmationNeeded)
                {
                    // The typed phrase comes last, after the plan (ConfirmAndRunAsync).
                    request = request with { ConfirmReplace = true };
                    continue;
                }

                var next = await HandleRefusalAsync(session, mapping, role, refusal);
                if (next is null)
                {
                    return;
                }

                if (next == RefusalAction.Revert)
                {
                    revertInstead = true;
                    break;
                }

                request = request with { AssumeClosed = request.AssumeClosed || next == RefusalAction.AssumeClosed };
            }
        }

        if (revertInstead)
        {
            await RevertAsync(mapping);
        }
    }

    /// <summary>
    /// Delete one backup (section 9.7, as built): a confirmation worded more strongly for the kept first backup,
    /// then the core's delete under the lock. Nothing else is touched. Answers whether it was deleted.
    /// </summary>
    public async Task<bool> DeleteBackupAsync(BackupRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!await overlay.ConfirmAsync(BackupsPage.DeleteTitle(row), BackupsPage.DeleteText(row), BackupsPage.DeleteButton(row)))
        {
            return false;
        }

        var session = await BeginAsync("backup delete");
        if (session is null)
        {
            return false;
        }

        using (session)
        {
            try
            {
                var deleted = await Work("Deleting the backup…", () => session.DeleteBackup(row.Info.Id));
                log.Write($"backup delete {row.Info.Id}: done{(deleted.WasFirst ? ", the first backup" : string.Empty)}, {deleted.Remaining} left");
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Write($"backup delete {row.Info.Id} refused: {e.Message}");
                await overlay.InformAsync("Nothing was deleted", e.Message);
                return false;
            }
        }
    }

    private async Task RevertAsync(Subject mapping)
    {
        var role = mapping.Name;
        var session = await BeginAsync("revert");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            var assumeClosed = false;
            while (true)
            {
                var outcome = await Work("Checking what reverting involves…", () => session.PlanRevert(mapping.MappingId, assumeClosed));
                if (outcome.Plan is { } plan)
                {
                    if (await ConfirmAndRunAsync(session, plan, role) is { Status: ExecutionStatus.Completed })
                    {
                        await OfferRevokeAsync(session, mapping.MappingId);
                    }

                    return;
                }

                var refusal = outcome.Refusal!;
                log.Write($"revert {mapping.MappingId} refused: {refusal.Kind}: {refusal.Message.Text}");
                var next = await HandleRefusalAsync(session, mapping, role, refusal);
                if (next is null)
                {
                    return;
                }

                assumeClosed |= next == RefusalAction.AssumeClosed;
            }
        }
    }

    /// <summary>"Remove" (section 5): <c>map remove</c> after its confirmation. Nothing is deleted.</summary>
    public async Task RemoveAsync(MappingState mapping)
    {
        var role = $"{mapping.AppName} {mapping.Role}";
        if (!await overlay.ConfirmAsync($"Remove {role} from this device?", AppsPage.RemoveWarning(role), "Remove"))
        {
            return;
        }

        var session = await BeginAsync("map remove");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                await Work("Updating the mapping…", () => session.Remove(mapping.MappingId));
                log.Write($"map remove {mapping.MappingId}: done");
            }
            catch (StoreException e)
            {
                log.Write($"map remove {mapping.MappingId} refused: {e.Problem}: {e.Message}");
                await overlay.InformAsync("Nothing was changed", e.Message);
            }
        }
    }

    /// <summary>"Back up now" (section 5), on a Linked row: a verified backup of the shared copy. Copies only, so no confirmation.</summary>
    public async Task BackUpNowAsync(MappingState mapping)
    {
        var session = await BeginAsync("back up now");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                var backup = await Work("Backing up…", () => session.BackUpNow(mapping.MappingId));
                log.Write($"back up now {mapping.MappingId}: {backup.Id}");
                await overlay.InformAsync("Backed up", AppsPage.BackedUpText($"{mapping.AppName} {mapping.Role}", backup.FileCount, backup.Location));
            }
            catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException or SafeFsException or BackupException)
            {
                log.Write($"back up now {mapping.MappingId} failed: {e.Message}");
                // The AOT build keeps only resource keys for the system's messages ("UnauthorizedAccess_IODenied_Path, <path>").
                await overlay.InformAsync("No backup was made", e is IOException or UnauthorizedAccessException ? AppsPage.BackUpFailedText : e.Message);
            }
        }
    }

    /// <summary>
    /// "Delete" (section 5), on a "Not set up here" row no device uses: <c>map delete</c> after its confirmation. It removes the
    /// shared saves, record and markers for every device, after a verified backup on this device (design 5.3, invariant 2).
    /// </summary>
    public Task DeleteMappingAsync(MappingState mapping) =>
        DeleteMappingAsync(mapping.MappingId, $"{mapping.AppName} {mapping.Role}");

    /// <param name="confirmed">The caller has already asked (the Maintenance screen asks for a typed phrase).</param>
    public async Task DeleteMappingAsync(string mappingId, string role, bool confirmed = false)
    {
        var session = await BeginAsync("map delete");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                var described = await Work("Checking what the shared folder holds…", () => session.DescribeMappingDeletion(mappingId));
                if (!confirmed && !await overlay.ConfirmAsync(
                        AppsPage.DeleteTitle(role), AppsPage.DeleteText(session.HasOtherDevices, NoBackups), "Delete", Overlay.OperationDialogTimeout, danger: true))
                {
                    return;
                }

                var done = await Work("Backing up the shared saves and deleting…", () => session.DeleteMapping(mappingId));
                log.Write($"map delete {mappingId}: done{(done.Backup is { } backup ? $", backed up as {backup.Id}" : string.Empty)}");
                await overlay.InformAsync("Deleted", AppsPage.DeletedText(role, done.Backup is not null));
            }
            catch (MappingDeletionIncompleteException e)
            {
                // It stopped after it had started: the shared copy may already be gone, so this is not "nothing was deleted".
                log.Write($"map delete {mappingId} stopped part way: {e.Message}");
                await overlay.InformAsync("Deleted only in part", e.Message);
            }
            catch (Exception e) when (e is StoreException or IOException or UnauthorizedAccessException or SafeFsException or BackupException)
            {
                log.Write($"map delete {mappingId} refused: {e.Message}");
                await overlay.InformAsync("Nothing was deleted", e.Message);
            }
        }
    }

    /// <summary>
    /// Apply all (section 9.4): each row planned with no choice; those that need nothing more are applied after
    /// one confirmation listing them, each its own operation; the rest are named at the end, to open one by one.
    /// Unlike the CLI's <c>apply --all</c>, a mapping that needs a choice does not stop the others.
    /// </summary>
    public async Task ApplyAllAsync(IReadOnlyList<AppRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var roles = rows.ToDictionary(r => r.Mapping.MappingId, r => $"{r.Mapping.AppName} {r.Mapping.Role}", StringComparer.Ordinal);
        var session = await BeginAsync("apply all");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            var ids = rows.Select(r => r.Mapping.MappingId).ToList();
            var (plans, needYou) = await Work("Checking each mapping…", () => session.PlanApplyAll(ids));
            var chosen = new Dictionary<string, SetupChoice>(StringComparer.Ordinal);

            // Mappings with saves only on this device share one question, so a first setup is not row-by-row.
            var firstDevice = needYou.Where(AppsPage.NeedsFirstDeviceChoice).ToList();
            if (firstDevice.Count > 0)
            {
                var names = firstDevice.Select(r => roles[r.MappingId]).ToList();
                var leave = new Button { Content = AppsPage.FirstDeviceLeave };
                var empty = new Button { Content = AppsPage.FirstDeviceStartEmpty };
                var share = new Button { Content = AppsPage.FirstDeviceShare };
                var pressed = await overlay.ShowAsync(
                    AppsPage.FirstDeviceTitle(names.Count, names[0]), Overlay.Body(AppsPage.FirstDeviceText(names, session.HasOtherDevices, NoBackups)), [leave, empty, share], leave, Overlay.OperationDialogTimeout);
                if (pressed != leave)
                {
                    var choice = pressed == share ? SetupChoice.Copy : SetupChoice.NoCopy;
                    var batch = firstDevice.Select(r => r.MappingId).ToList();
                    var (more, still) = await Work("Checking each mapping…", () => session.PlanApplyAll(batch, choice));
                    foreach (var plan in more.Where(p => p.Classification == Classification.FirstDevice))
                    {
                        chosen[plan.MappingId] = choice;
                    }

                    plans = [.. plans, .. more];
                    needYou = [.. needYou.Where(r => !batch.Contains(r.MappingId, StringComparer.Ordinal)), .. still];
                }
            }

            var waiting = needYou.Select(r => roles[r.MappingId]).ToList();
            var linked = 0;
            if (plans.Count > 0)
            {
                var list = string.Join("\n", plans.Select(p => chosen.TryGetValue(p.MappingId, out var c)
                    ? $"{roles[p.MappingId]} ({(c == SetupChoice.Copy ? "share this device's data" : "start empty")})"
                    : roles[p.MappingId]));
                var intro = waiting.Count == 0 ? string.Empty : $"\n\nNot these, which need a choice or a fix first: {string.Join(", ", waiting)}.";
                if (!await overlay.ConfirmAsync(
                        plans.Count == 1
                            ? $"Link {roles[plans[0].MappingId]} on this device to its shared folder?"
                            : $"Link {plans.Count} on this device to their shared folders?",
                        $"{list}\n\nEach is backed up first where it has data.{intro}",
                        "Apply all", Overlay.OperationDialogTimeout))
                {
                    return;
                }

                foreach (var plan in plans)
                {
                    var role = roles[plan.MappingId];
                    var result = await RunWithProgressAsync(ActionTexts.Confirmation(plan, role, session.HasOtherDevices).Title, plan, progress => session.Run(plan, progress), role, quietOnSuccess: true);
                    if (result.Status == ExecutionStatus.Completed)
                    {
                        linked++;
                    }
                    else
                    {
                        waiting.Add(role);
                        if (result.Status == ExecutionStatus.RecoveryNeeded)
                        {
                            // Nothing else may run until it is recovered (section 9.3).
                            break;
                        }
                    }
                }
            }

            await overlay.InformAsync("Apply all", AppsPage.ApplyAllSummary(linked, waiting));
        }
    }

    /// <summary>Banner B2's Recover (section 9.5): a confirmation naming what is unfinished, then the core's recovery.</summary>
    public async Task RecoverAsync()
    {
        var unfinished = ActionTexts.Unfinished(Journal.For(_local).TryRead());
        if (!await overlay.ConfirmAsync("Recover the unfinished operation?", $"{unfinished}\n\n{ActionTexts.RecoverText}", "Recover"))
        {
            return;
        }

        var session = await BeginAsync("recover");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            await RunWithProgressAsync("Recovering", null, progress => session.Recover(progress), "the operation", recovery: true);
        }
    }

    private static bool Expected(Exception e) =>
        e is StoreException or IOException or UnauthorizedAccessException or SafeFsException or BackupException;

    /// <summary>"Rename" (phase 3.6): a text dialog, then <c>device rename</c> under the lock. A check follows and redraws the title and tray.</summary>
    public async Task RenameDeviceAsync(string currentName, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var name = await overlay.AskTextAsync(
            "Rename this device", "The name shows in the sync folder. Your links and saves are not touched.",
            "Rename", typed => MaintenancePage.DeviceNameProblem(typed, taken), initial: currentName);
        if (name is null || name == currentName)
        {
            return;
        }

        var session = await BeginAsync("device rename");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                var done = await Work("Renaming the device…", () => session.RenameDevice(name));
                log.Write($"device rename {done.OldName} -> {done.NewName}: done");
                await overlay.InformAsync("Renamed", $"This device is now '{done.NewName}'.");
            }
            catch (Exception e) when (Expected(e))
            {
                log.Write($"device rename refused: {e.Message}");
                await overlay.InformAsync("Nothing was changed", e.Message);
            }
        }
    }

    /// <summary>"Remove" on another device: one warning that asks for its typed name (D8), then <c>device remove --other</c>.</summary>
    public async Task RemoveOtherDeviceAsync(string name)
    {
        if (await overlay.AskTextAsync(MaintenancePage.RemoveOtherTitle(name), MaintenancePage.RemoveOtherText, "Remove device", typed => MaintenancePage.TypedPhraseProblem(typed, name), danger: true) is null)
        {
            return;
        }

        var session = await BeginAsync("device remove --other");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                var files = await Work("Removing the device from the sync folder…", () => session.RemoveOtherDevice(name, dryRun: false));
                log.Write($"device remove --other {name}: done, {files.Count} file(s)");
                await overlay.InformAsync("Removed", $"'{name}' is removed from the sync folder. {files.Count} file(s) there were deleted.");
            }
            catch (Exception e) when (Expected(e))
            {
                log.Write($"device remove --other {name} refused: {e.Message}");
                await overlay.InformAsync("Nothing was changed", e.Message);
            }
        }
    }

    /// <summary>An orphaned mapping's two deletions. With the saves it asks for the typed phrase (D8) and goes through <c>map delete</c>.</summary>
    public async Task DeleteOrphanAsync(string mappingId, bool withSaves)
    {
        if (withSaves)
        {
            if (await overlay.AskTextAsync(
                    $"Delete {mappingId} and its data?", MaintenancePage.DeleteOrphanWithSavesText, "Delete", typed => MaintenancePage.TypedPhraseProblem(typed, MaintenancePage.DeleteEverythingPhrase), danger: true) is null)
            {
                return;
            }

            await DeleteMappingAsync(mappingId, mappingId, confirmed: true);
            return;
        }

        if (!await overlay.ConfirmAsync($"Delete the mapping {mappingId}?", MaintenancePage.DeleteOrphanMappingText, "Delete mapping", Overlay.OperationDialogTimeout, danger: true))
        {
            return;
        }

        var session = await BeginAsync("map delete");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                await Work("Deleting the mapping…", () => session.DeleteMappingRecord(mappingId));
                log.Write($"mapping record {mappingId}: deleted, saves kept");
                await overlay.InformAsync("Deleted", $"The mapping {mappingId} is deleted. Its saves in the sync folder stay.");
            }
            catch (Exception e) when (Expected(e))
            {
                log.Write($"mapping record {mappingId} refused: {e.Message}");
                await overlay.InformAsync("Nothing was deleted", e.Message);
            }
        }
    }

    /// <summary>Backup Clean up (D9) over the given mappings (null: every backup). "All" asks for the typed phrase in its one dialog.</summary>
    public async Task CleanUpBackupsAsync(IReadOnlyList<string>? mappingIds, string scope, BackupCleanup mode)
    {
        var session = await BeginAsync("backup delete");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                IReadOnlyList<string?> targets = mappingIds is null ? [null] : [.. mappingIds];
                var found = await Work("Checking the backups…", () => targets.SelectMany(m => session.DescribeBackupCleanup(m, mode)).ToList());
                if (found.Count == 0)
                {
                    await overlay.InformAsync("Nothing to delete", BackupCleanupTexts.Result(0));
                    return;
                }

                var text = $"{BackupCleanupTexts.Describe(mode, scope)}\n\nThis deletes {BackupCleanupTexts.Count(found.Count)} ({ActionTexts.Size(found.Sum(b => b.TotalBytes))}).";
                var all = mode == BackupCleanup.All;
                if (all
                        ? await overlay.AskTextAsync(BackupCleanupTexts.Label(mode) + "?", text, "Delete all backups", typed => MaintenancePage.TypedPhraseProblem(typed, BackupCleanupTexts.AllPhrase), danger: true, timeout: Overlay.OperationDialogTimeout) is null
                        : !await overlay.ConfirmAsync(BackupCleanupTexts.Label(mode) + "?", text, "Delete", Overlay.OperationDialogTimeout))
                {
                    return;
                }

                var removed = await Work("Deleting the backups…", () => targets.Sum(m => session.CleanUpBackups(m, mode).Count));
                log.Write($"backup cleanup {mode} of {scope}: {removed} deleted");
                await overlay.InformAsync("Backups deleted", BackupCleanupTexts.Result(removed));
            }
            catch (Exception e) when (Expected(e))
            {
                log.Write($"backup cleanup refused: {e.Message}");
                await overlay.InformAsync("Nothing was deleted", e.Message);
            }
        }
    }

    /// <summary>
    /// "Remove this device", after page 2 (phase 3.6): plan again under the lock, the typed phrases (D8), the removal, the result.
    /// When it is done a check finds the device not set up and the window shows setup, with no restart.
    /// </summary>
    public async Task RemoveThisDeviceAsync(RemovalChoices choices)
    {
        var session = await BeginAsync("device remove");
        if (session is null)
        {
            return;
        }

        using (session)
        {
            try
            {
                var plan = await Work("Planning the removal…", () => session.PlanDeviceRemoval(choices));
                if (plan.IsRefused)
                {
                    var refusals = RemoveDeviceFlow.Refusals(plan, id => RemoveDeviceFlow.NamesOf(session.Workspace, id));
                    await overlay.InformAsync("Nothing was changed", string.Join("\n\n", refusals));
                    return;
                }

                foreach (var phrase in RemoveDeviceFlow.PhrasesToType(plan.DeviceName, choices))
                {
                    var asking = phrase == MaintenancePage.DeleteEverythingPhrase
                        ? "This deletes saves from the sync folder."
                        : string.Join("\n", RemoveDeviceFlow.PlanLines(plan));
                    if (await overlay.AskTextAsync($"Type {phrase} to confirm", asking, "Continue", typed => MaintenancePage.TypedPhraseProblem(typed, phrase), danger: true, timeout: Overlay.OperationDialogTimeout) is null)
                    {
                        return;
                    }
                }

                var result = await Work("Removing this device…", () => session.RemoveDevice(plan));
                log.Write($"device remove: {(result.Failure is null ? "done" : "a revert did not finish")}, {result.GivenBack.Count} folder(s) given back");
                var (text, details) = RemoveDeviceFlow.Result(result);
                await overlay.InformAsync(
                    result.Failure is null ? "This device is removed" : "Not removed", text, details.Count > 0 ? Overlay.Details(string.Join("\n", details)) : null);
            }
            catch (Exception e) when (Expected(e))
            {
                log.Write($"device remove refused: {e.Message}");
                await overlay.InformAsync("Nothing was changed", e.Message);
            }
        }
    }

    private async Task<OperationSession?> BeginAsync(string command)
    {
        var (session, refused) = await Work("Checking nothing else is running…", () => OperationSession.Begin(_local, command, Processes, FlatpakCheck.ForSystem()));
        if (refused is not null)
        {
            log.Write($"{command} not started: {refused}");
            await overlay.InformAsync("Not now", refused);
        }

        return session;
    }

    /// <summary>A refusal with the actions it allows. Answers the action chosen, or null to stop.</summary>
    private async Task<RefusalAction?> HandleRefusalAsync(OperationSession session, Subject mapping, string role, Refusal refusal)
    {
        var grant = await Work("Checking the sandbox…", () => session.GrantableSandbox(mapping.MappingId, refusal));
        var view = ActionTexts.Describe(refusal, role, mapping.AppName, grant is not null, NoBackups);
        var close = new Button { Content = view.Actions.Count == 0 ? "Close" : "Cancel" };
        var buttons = new List<Button> { close };
        var actionOf = new Dictionary<Button, RefusalAction>();
        foreach (var action in view.Actions)
        {
            var button = new Button { Content = Label(action, mapping.AppName) };
            actionOf[button] = action;
            buttons.Add(button);
        }

        var body = Overlay.Body(string.Join("\n\n", view.Sentences), Overlay.Details(view.Details));
        var pressed = await overlay.ShowAsync(view.Title, body, buttons, close, Overlay.OperationDialogTimeout);
        if (!actionOf.TryGetValue(pressed, out var chosen))
        {
            return null;
        }

        // "Is closed" and "were deleted" carry their warning in the refusal's own sentences (ActionTexts.Describe), and the plan's
        // confirmation still follows, so they get no dialog of their own.
        if (chosen == RefusalAction.GiveAccess)
        {
            var granted = await Work("Giving access…", () => session.GrantSandbox(grant!));
            log.Write($"flatpak grant for {grant!.AppId}: {(granted ? "done" : "failed")}");
            if (!granted)
            {
                await overlay.InformAsync("Access not given", $"'{grant.FixCommand}' did not succeed. Nothing was changed.");
                return null;
            }
        }

        return chosen;
    }

    /// <summary>The choice dialog (section 9.2): the comparison, then exactly the allowed choices, none selected at first.</summary>
    private async Task<SetupChoice?> ChooseAsync(Refusal refusal, Subject mapping, string role, bool others)
    {
        var body = new StackPanel { Spacing = 10 };
        if (refusal.Comparison is { } comparison)
        {
            body.Children.Add(new TextBlock { Text = ActionTexts.Newest(comparison), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            foreach (var (group, entries) in ActionTexts.ComparisonGroups(comparison))
            {
                var list = new TextBlock { Text = string.Join("\n", entries), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                body.Children.Add(new Expander { Header = group, Content = list, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
        }

        var cont = new Button { Content = "Continue", IsEnabled = false };
        var chosen = (SetupChoice?)null;
        foreach (var option in ActionTexts.Choices(refusal, mapping.AppName, others, NoBackups))
        {
            var radio = new RadioButton { GroupName = "choice", Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = option.Label, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = option.Sentence, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, FontSize = 13 },
                },
            } };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    chosen = option.Choice;
                    cont.IsEnabled = true;
                }
            };
            body.Children.Add(radio);
        }

        if (!NoBackups)
        {
            body.Children.Add(new TextBlock { Text = ActionTexts.EveryChoiceBacksUp, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        }
        var cancel = new Button { Content = "Cancel" };
        var pressed = await overlay.ShowAsync(ActionTexts.ChoiceTitle(role), body, [cancel, cont], cancel, Overlay.OperationDialogTimeout);
        if (pressed != cont || chosen is not { } choice)
        {
            return null;
        }

        return choice;
    }

    /// <summary>
    /// The plan's one confirmation with its steps under "Show steps", then the run and its result. Null when cancelled.
    /// A plan that empties the shared folder asks for its typed phrase in that same dialog.
    /// </summary>
    private async Task<ExecutionResult?> ConfirmAndRunAsync(OperationSession session, Plan plan, string role, string? lead = null, bool assumedSynced = false)
    {
        var view = ActionTexts.Confirmation(plan, role, session.HasOtherDevices, assumedSynced);
        var text = string.Join("\n\n", lead is null ? view.Sentences : [lead, .. view.Sentences]);
        var steps = new Expander
        {
            Header = "Show steps",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = string.Join("\n", plan.Steps.Select((s, i) => $"{i + 1}. {ActionTexts.Step(s, plan.NoBackups)}")), TextWrapping = TextWrapping.Wrap },
        };
        var go = view.Phrase is { } phrase
            ? await overlay.AskTextAsync(view.Title, text, view.Button, typed => MaintenancePage.TypedPhraseProblem(typed, phrase), danger: true, timeout: Overlay.OperationDialogTimeout, details: steps) is not null
            : await overlay.ConfirmAsync(view.Title, text, view.Button, Overlay.OperationDialogTimeout, steps);
        if (!go)
        {
            return null;
        }

        return await RunWithProgressAsync(view.Title, plan, progress => session.Run(plan, progress), role);
    }

    /// <summary>
    /// Section 9.3's progress: the steps, each ticked as the executor finishes it. Nothing can be pressed meanwhile.
    /// With <paramref name="role"/>, the finished list stays on screen under the result's title and sentences, with
    /// an OK button, so the whole flow can be read before it closes. <paramref name="quietOnSuccess"/> shows it only when
    /// the operation did not complete (Apply all reports its successes in one summary).
    /// </summary>
    private async Task<ExecutionResult> RunWithProgressAsync(string title, Plan? plan, Func<IProgress<StepProgress>, ExecutionResult> run, string? role = null, bool recovery = false, bool quietOnSuccess = false)
    {
        var lines = new StackPanel { Spacing = 4 };
        var marks = new List<TextBlock>();
        foreach (var step in plan?.Steps ?? [])
        {
            var mark = new TextBlock { Text = "○", Width = 20 };
            marks.Add(mark);
            lines.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, new TextBlock { Text = ActionTexts.Step(step, plan!.NoBackups), TextWrapping = TextWrapping.Wrap } } });
        }

        if (marks.Count == 0)
        {
            lines.Children.Add(new TextBlock { Text = "Working…" });
        }

        // The executor reports from its thread at its own speed; the list shows the reports on this thread at a
        // pace that can be read, then holds the finished list (section 9.3). Only the display waits.
        var progress = new PacedProgress();
        ExecutionResult result;
        Task<Button>? shown = null;
        using (overlay.ShowBlocking(title, lines))
        {
            var work = Task.Run(() =>
            {
                try
                {
                    return run(progress);
                }
                finally
                {
                    progress.Complete();
                }
            });
            await progress.ShowAsync(p =>
            {
                if (p.Index < marks.Count)
                {
                    marks[p.Index].Text = p.Status == StepStatus.Done ? "✓" : "…";
                }
            });
            result = await work;
            log.Write($"{result.Operation?.ToString() ?? "recover"} {result.MappingId}: {result.Status}{(result.Error is { } e ? $": {e.Text}" : string.Empty)}");
            if (result.Diagnostic is not null)
            {
                log.Write($"  {result.Diagnostic}");
            }

            if (role is not null && !(quietOnSuccess && result.Status == ExecutionStatus.Completed))
            {
                // The list moves from the progress layer to the result's, in one step, so nothing flashes between.
                var (resultTitle, steps, sentences) = ActionTexts.FinishedView(plan, result, role, recovery);
                var body = new StackPanel { Spacing = 12 };
                if (steps.Count > 0)
                {
                    var kept = new StackPanel { Spacing = 4 };
                    foreach (var row in lines.Children.ToList())
                    {
                        lines.Children.Remove(row);
                        kept.Children.Add(row);
                    }

                    body.Children.Add(kept);
                }

                if (sentences.Count > 0)
                {
                    body.Children.Add(new TextBlock { Text = string.Join("\n\n", sentences), TextWrapping = TextWrapping.Wrap });
                }

                var ok = new Button { Content = "OK" };
                shown = overlay.ShowAsync(resultTitle, body, [ok], ok);
            }
        }

        if (shown is not null)
        {
            await shown;
        }

        return result;
    }

    /// <summary>After a revert (section 9.6): take back a Flatpak grant no mapping needs now. Keep is the safe choice.</summary>
    private async Task OfferRevokeAsync(OperationSession session, string mappingId)
    {
        if (await Work("Checking the sandbox…", () => session.GrantNoLongerNeeded(mappingId)) is not { } grant)
        {
            return;
        }

        var keep = new Button { Content = "Keep" };
        var remove = new Button { Content = "Remove access" };
        var pressed = await overlay.ShowAsync(
            "Remove the Flatpak access?",
            Overlay.Body($"SaveTether gave the Flatpak app {grant.AppId} access to {string.Join(" and ", grant.Paths)}, and no mapping here needs it now. Removing it only tidies up."),
            [keep, remove], keep, Overlay.OperationDialogTimeout);
        if (pressed == remove)
        {
            var revoked = await Work("Removing access…", () => session.RevokeSandbox(grant));
            log.Write($"flatpak revoke for {grant.AppId}: {(revoked ? "done" : "failed")}");
            if (!revoked)
            {
                await overlay.InformAsync("Access not removed", $"'{grant.RemoveCommand}' did not succeed. The app can still write there.");
            }
        }
    }

    /// <summary>The mapping an action is about, as the dialogs name it: "Eden saves".</summary>
    private sealed record Subject(string MappingId, string AppName, string Role)
    {
        public string Name => $"{AppName} {Role}";
    }

    private static string Label(RefusalAction action, string app) => action switch
    {
        RefusalAction.CheckAgain => "Check again",
        RefusalAction.AssumeClosed => $"{app} is closed",
        RefusalAction.AssumeSynced => "They were deleted on purpose",
        RefusalAction.GiveAccess => "Give access",
        RefusalAction.Revert => "Revert",
        _ => action.ToString(),
    };
}
