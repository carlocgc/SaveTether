using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Core.Execution;
using SaveTether.Core.Model;
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
        if (useThisCopy && !await overlay.ConfirmAsync(
                "Make this device's copy the one every device keeps?",
                "No files change; the other devices' claims are marked as replaced. Do this only if this device's saves are the ones to keep.",
                "Use this device's copy"))
        {
            return;
        }

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
                var outcome = await Task.Run(() => session.PlanApply(mapping.MappingId, request));
                if (outcome.Plan is { } plan)
                {
                    await ConfirmAndRunAsync(session, plan, role);
                    return;
                }

                var refusal = outcome.Refusal!;
                log.Write($"apply {mapping.MappingId} refused: {refusal.Kind}: {refusal.Message.Text}");
                switch (refusal.Kind)
                {
                    case RefusalKind.ChoiceNeeded:
                        if (await ChooseAsync(refusal, mapping, role) is not { } choice)
                        {
                            return;
                        }

                        request = request with { Choice = choice, ConfirmReplace = choice == SetupChoice.ReplaceShared };
                        continue;

                    case RefusalKind.ConfirmationNeeded:
                        if (!await ConfirmReplaceAsync(role))
                        {
                            return;
                        }

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
        if (row.OfSharedCopy && !await overlay.ConfirmAsync(
                BackupsPage.RestoreSharedTitle(role), BackupsPage.RestoreSharedText, "Restore on every device"))
        {
            return;
        }

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
                var outcome = await Task.Run(() => session.PlanRestore(row.Info.Id, request));
                if (outcome.Plan is { } plan)
                {
                    await ConfirmAndRunAsync(session, plan, role, row.OfSharedCopy ? null : BackupsPage.RestoreLocalText(row));
                    return;
                }

                var refusal = outcome.Refusal!;
                log.Write($"backup restore {row.Info.Id} refused: {refusal.Kind}: {refusal.Message.Text}");
                if (refusal.Kind == RefusalKind.ConfirmationNeeded)
                {
                    if (!await overlay.ConfirmAsync(BackupsPage.RestoreSharedTitle(role), BackupsPage.RestoreSharedText, "Restore on every device", Overlay.OperationDialogTimeout))
                    {
                        return;
                    }

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
                var outcome = await Task.Run(() => session.PlanRevert(mapping.MappingId, assumeClosed));
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

    /// <summary>"Stop using here" (section 5): <c>map remove</c> after its confirmation. Nothing is deleted.</summary>
    public async Task StopUsingAsync(MappingState mapping)
    {
        var role = $"{mapping.AppName} {mapping.Role}";
        if (!await overlay.ConfirmAsync($"Stop using {role} here?", AppsPage.StopUsingWarning(role), "Stop using here"))
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
                await Task.Run(() => session.StopUsing(mapping.MappingId));
                log.Write($"map remove {mapping.MappingId}: done");
            }
            catch (StoreException e)
            {
                log.Write($"map remove {mapping.MappingId} refused: {e.Problem}: {e.Message}");
                await overlay.InformAsync("Nothing was changed", e.Message);
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
            var (plans, needYou) = await Task.Run(() => session.PlanApplyAll(rows.Select(r => r.Mapping.MappingId)));
            var waiting = needYou.Select(r => roles[r.MappingId]).ToList();
            var linked = 0;
            if (plans.Count > 0)
            {
                var list = string.Join("\n", plans.Select(p => roles[p.MappingId]));
                var intro = waiting.Count == 0 ? string.Empty : $"\n\nNot these, which need you first: {string.Join(", ", waiting)}.";
                if (!await overlay.ConfirmAsync(
                        plans.Count == 1
                            ? $"Link {roles[plans[0].MappingId]} on this device to its shared folder?"
                            : $"Link {plans.Count} on this device to their shared folders?",
                        $"{list}\n\nEach is backed up first where it has saves.{intro}",
                        "Apply all", Overlay.OperationDialogTimeout))
                {
                    return;
                }

                foreach (var plan in plans)
                {
                    var role = roles[plan.MappingId];
                    var result = await RunWithProgressAsync(ActionTexts.Confirmation(plan, role).Title, plan, progress => session.Run(plan, progress));
                    if (result.Status == ExecutionStatus.Completed)
                    {
                        linked++;
                    }
                    else
                    {
                        waiting.Add(role);
                        await ShowResultAsync(result, role);
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
            var result = await RunWithProgressAsync("Recovering", null, progress => session.Recover(progress));
            await ShowResultAsync(result, result.MappingId ?? "the operation", recovery: true);
        }
    }

    private async Task<OperationSession?> BeginAsync(string command)
    {
        var (session, refused) = await Task.Run(() => OperationSession.Begin(_local, command, Processes, FlatpakCheck.ForSystem()));
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
        var grant = await Task.Run(() => session.GrantableSandbox(mapping.MappingId, refusal));
        var view = ActionTexts.Describe(refusal, role, mapping.AppName, grant is not null);
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

        if (chosen == RefusalAction.AssumeClosed && !await overlay.ConfirmAsync(
                $"Is {mapping.AppName} closed?", ActionTexts.AssumeClosedWarning(mapping.AppName), $"{mapping.AppName} is closed", Overlay.OperationDialogTimeout))
        {
            return null;
        }

        if (chosen == RefusalAction.AssumeSynced && !await overlay.ConfirmAsync(
                "Were the shared saves deleted?", ActionTexts.AssumeSyncedWarning, "They were deleted", Overlay.OperationDialogTimeout))
        {
            return null;
        }

        if (chosen == RefusalAction.GiveAccess)
        {
            var granted = await Task.Run(() => session.GrantSandbox(grant!));
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
    private async Task<SetupChoice?> ChooseAsync(Refusal refusal, Subject mapping, string role)
    {
        var body = new StackPanel { Spacing = 10 };
        if (refusal.Comparison is { } comparison)
        {
            body.Children.Add(new TextBlock { Text = ActionTexts.Newest(comparison), TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
            foreach (var (group, entries) in ActionTexts.ComparisonGroups(comparison))
            {
                var list = new TextBlock { Text = string.Join("\n", entries), TextWrapping = TextWrapping.Wrap, FontSize = 13 };
                body.Children.Add(group.StartsWith("On both, the same", StringComparison.Ordinal)
                    ? new Expander { Header = group, Content = list, HorizontalAlignment = HorizontalAlignment.Stretch }
                    : new StackPanel { Spacing = 2, Children = { new TextBlock { Text = group, FontWeight = FontWeight.SemiBold }, list } });
            }
        }

        var cont = new Button { Content = "Continue", IsEnabled = false };
        var chosen = (SetupChoice?)null;
        foreach (var option in ActionTexts.Choices(refusal, mapping.AppName))
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

        body.Children.Add(new TextBlock { Text = ActionTexts.EveryChoiceBacksUp, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        var cancel = new Button { Content = "Cancel" };
        var pressed = await overlay.ShowAsync($"{role} on this device already has saves.", body, [cancel, cont], cancel, Overlay.OperationDialogTimeout);
        if (pressed != cont || chosen is not { } choice)
        {
            return null;
        }

        // Replacing the shared saves has its own dialog, never combined with the choice.
        return choice == SetupChoice.ReplaceShared && !await ConfirmReplaceAsync(role) ? null : choice;
    }

    private Task<bool> ConfirmReplaceAsync(string role) =>
        overlay.ConfirmAsync(ActionTexts.ReplaceTitle(role), ActionTexts.ReplaceText, "Replace on every device", Overlay.OperationDialogTimeout);

    /// <summary>The plan's confirmation with its steps under "Show steps", then the run and its result. Null when cancelled.</summary>
    private async Task<ExecutionResult?> ConfirmAndRunAsync(OperationSession session, Plan plan, string role, string? lead = null)
    {
        var (title, planSentences) = ActionTexts.Confirmation(plan, role);
        IReadOnlyList<string> sentences = lead is null ? planSentences : [lead, .. planSentences];
        var steps = new Expander
        {
            Header = "Show steps",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = string.Join("\n", plan.Steps.Select((s, i) => $"{i + 1}. {ActionTexts.Step(s)}")), TextWrapping = TextWrapping.Wrap },
        };
        var go = plan.Operation switch
        {
            PlanOperation.Revert => "Revert",
            PlanOperation.Restore => "Restore",
            _ => "Apply",
        };
        if (!await overlay.ConfirmAsync(title, string.Join("\n\n", sentences), go, Overlay.OperationDialogTimeout, steps))
        {
            return null;
        }

        var result = await RunWithProgressAsync(title, plan, progress => session.Run(plan, progress));
        await ShowResultAsync(result, role);
        return result;
    }

    /// <summary>Section 9.3's progress: the steps, each ticked as the executor finishes it. Nothing can be pressed meanwhile.</summary>
    private async Task<ExecutionResult> RunWithProgressAsync(string title, Plan? plan, Func<IProgress<StepProgress>, ExecutionResult> run)
    {
        var lines = new StackPanel { Spacing = 4 };
        var marks = new List<TextBlock>();
        foreach (var step in plan?.Steps ?? [])
        {
            var mark = new TextBlock { Text = "○", Width = 20 };
            marks.Add(mark);
            lines.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, new TextBlock { Text = ActionTexts.Step(step), TextWrapping = TextWrapping.Wrap } } });
        }

        if (marks.Count == 0)
        {
            lines.Children.Add(new TextBlock { Text = "Working…" });
        }

        // The executor reports from its thread at its own speed; the list shows the reports on this thread at a
        // pace that can be read, then holds the finished list (section 9.3). Only the display waits.
        var progress = new PacedProgress();
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
            var result = await work;
            log.Write($"{result.Operation?.ToString() ?? "recover"} {result.MappingId}: {result.Status}{(result.Error is { } e ? $": {e.Text}" : string.Empty)}");
            return result;
        }
    }

    private async Task ShowResultAsync(ExecutionResult result, string role, bool recovery = false)
    {
        var (title, sentences) = ActionTexts.Result(result, role, recovery);
        await overlay.InformAsync(title, sentences.Count == 0 ? "Done." : string.Join("\n\n", sentences));
    }

    /// <summary>After a revert (section 9.6): take back a Flatpak grant no mapping needs now. Keep is the safe choice.</summary>
    private async Task OfferRevokeAsync(OperationSession session, string mappingId)
    {
        if (await Task.Run(() => session.GrantNoLongerNeeded(mappingId)) is not { } grant)
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
            var revoked = await Task.Run(() => session.RevokeSandbox(grant));
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
        RefusalAction.AssumeSynced => "The shared saves really were deleted",
        RefusalAction.GiveAccess => "Give access",
        RefusalAction.Revert => "Revert",
        _ => action.ToString(),
    };
}
