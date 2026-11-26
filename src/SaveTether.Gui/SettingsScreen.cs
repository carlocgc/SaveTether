using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Core.Startup;
using SaveTether.Core.Storage;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The Settings page (desktop-app-spec.md, section 11), in place of the Apps page while open. Built so far:
/// Start with the session, Desktop notifications, Backups (before actions, how many to keep), Sync conflicts, and About. The backup location and This device come later.
/// </summary>
internal sealed class SettingsScreen : UserControl, IScreen
{
    /// <summary>The way back, shown by the window above the scrolling page.</summary>
    public ContentControl Bar { get; } = new();

    public SettingsScreen(string localFolder, SessionStart session, Overlay overlay, Action close)
    {
        var page = new StackPanel { Spacing = 10, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        Bar.Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Settings", FontSize = 22, FontWeight = FontWeight.SemiBold },
                Navigation.BackRow("Back", close),
            },
        };

        // An on/off switch whose change is saved by set; on a problem it is logged, shown, and the switch put back. When allow is
        // given and answers false for the new position, nothing is saved and the switch goes back too.
        ToggleSwitch Switch(string name, Func<bool> isOn, Func<bool, string?> set, Func<bool, Task<bool>>? allow = null)
        {
            var toggle = new ToggleSwitch { IsChecked = isOn(), OnContent = "On", OffContent = "Off" };
            AutomationProperties.SetName(toggle, name);
            var reverting = false;
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (reverting)
                {
                    return;
                }

                var on = toggle.IsChecked == true;
                if (allow is not null && !await allow(on))
                {
                    reverting = true;
                    toggle.IsChecked = isOn();
                    reverting = false;
                    return;
                }

                if (set(on) is { } problem)
                {
                    App.Options.Log.Write(problem);
                    reverting = true;
                    toggle.IsChecked = isOn();
                    reverting = false;
                    await overlay.InformAsync("Nothing was changed", problem);
                    return;
                }

                App.Options.Log.Write($"{name.ToLowerInvariant()}: {(on ? "on" : "off")}");
            };
            return toggle;
        }

        page.Children.Add(Heading("Start with the session"));
        if (session.Available)
        {
            page.Children.Add(Switch("Start with the session", () => session.IsOn, session.Set));
            page.Children.Add(Note(
                "SaveTether starts in the tray when you log in, and checks your save folders in the background. " +
                "While this is on, the entry is updated with SaveTether's location each time it starts."));
        }
        else
        {
            page.Children.Add(Note("This desktop has no place for programs that start with the session."));
        }

        page.Children.Add(Heading("Desktop notifications"));
        var settings = new DeviceSettings(localFolder);
        page.Children.Add(Switch("Desktop notifications", () => settings.Notifications, settings.SetNotifications));
        page.Children.Add(Note(
            "SaveTether tells you when a save folder needs attention. With this off, only the tray icon's colour shows it."));

        page.Children.Add(Heading("Backups"));
        var unsafeNote = new TextBlock
        {
            Text = SettingsTexts.UnsafeModeNote,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Danger.Text,
            FontWeight = FontWeight.SemiBold,
            IsVisible = !settings.BackupBeforeActions,
        };
        var beforeActions = Switch(
            SettingsTexts.BackupBeforeActions,
            () => settings.BackupBeforeActions,
            on =>
            {
                var problem = settings.SetBackupBeforeActions(on);
                unsafeNote.IsVisible = !settings.BackupBeforeActions;
                return problem;
            },
            async on => on || await overlay.AskTextAsync(
                SettingsTexts.UnsafeModeTitle, SettingsTexts.UnsafeModeText, SettingsTexts.UnsafeModeConfirm,
                typed => MaintenancePage.TypedPhraseProblem(typed, SettingsTexts.UnsafeModePhrase), danger: true) is not null);
        page.Children.Add(new TextBlock { Text = SettingsTexts.BackupBeforeActions, FontWeight = FontWeight.SemiBold });
        page.Children.Add(beforeActions);
        page.Children.Add(unsafeNote);
        page.Children.Add(Note(SettingsTexts.BackupBeforeActionsNote));

        var maxBackups = new NumericUpDown
        {
            Minimum = 0,
            Maximum = DeviceSettings.MaxBackupsLimit,
            Increment = 1,
            FormatString = "0",
            ParsingNumberStyle = System.Globalization.NumberStyles.Integer,
            Value = settings.MaxBackupsPerFolder,
            Width = 140,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(maxBackups, SettingsTexts.MaxBackups);
        maxBackups.ValueChanged += async (_, e) =>
        {
            // An emptied box keeps the saved value; the box shows it again.
            if (e.NewValue is not { } value)
            {
                maxBackups.Value = settings.MaxBackupsPerFolder;
                return;
            }

            var count = (int)Math.Round(value);
            if (count == settings.MaxBackupsPerFolder)
            {
                return;
            }

            if (settings.SetMaxBackupsPerFolder(count) is { } problem)
            {
                App.Options.Log.Write(problem);
                maxBackups.Value = settings.MaxBackupsPerFolder;
                await overlay.InformAsync("Nothing was changed", problem);
                return;
            }

            App.Options.Log.Write($"max backups per folder: {count}");
        };
        page.Children.Add(new TextBlock { Text = SettingsTexts.MaxBackups, FontWeight = FontWeight.SemiBold });
        page.Children.Add(maxBackups);
        page.Children.Add(Note(SettingsTexts.MaxBackupsNote));

        page.Children.Add(Heading(SettingsTexts.SyncConflicts));
        page.Children.Add(Switch(
            SettingsTexts.DetectSyncConflicts,
            () => settings.DetectSyncConflicts,
            on =>
            {
                // Checks at once, so the rows show the result without waiting for the next background check.
                var problem = settings.SetDetectSyncConflicts(on);
                if (problem is null)
                {
                    App.Instance?.CheckNow();
                }

                return problem;
            }));
        page.Children.Add(Note(SettingsTexts.DetectSyncConflictsNote));

        // Linux, in the installed copy only (section 11, "Installing"). On Windows, Settings > Apps uninstalls what the installer put there.
        if (InstallOffer.ForSystem(localFolder, AppContext.BaseDirectory, Program.Version) is { CanUninstall: true } installed)
        {
            page.Children.Add(Heading("Uninstall"));
            page.Children.Add(Note($"SaveTether is installed in {installed.Layout.AppFolder}. Uninstalling removes it; your setup and linked folders stay."));
            var uninstall = new Button { Content = "Uninstall SaveTether" };
            uninstall.Click += async (_, _) =>
            {
                if (!await overlay.ConfirmAsync(InstallOffer.UninstallTitle, installed.UninstallText(), "Uninstall"))
                {
                    return;
                }

                var (done, problem) = installed.Uninstall(Core.Startup.Autostart.ForSystem());
                App.Options.Log.Write(done ? $"uninstalled from {installed.Layout.AppFolder}" : $"uninstall failed: {problem}");
                if (problem is not null)
                {
                    await overlay.InformAsync(done ? "Uninstalled, with files left" : "Not uninstalled", problem);
                }

                if (done)
                {
                    App.Instance?.Quit();
                }
            };
            page.Children.Add(uninstall);
        }

        // Linux, in an unpacked copy: the offer made at start, for someone who chose Not now (or the update, when a newer copy is unpacked).
        if (InstallOffer.ForSystem(localFolder, AppContext.BaseDirectory, Program.Version) is { } installer && installer.Offer(ignoreDeclined: true) is { } offer)
        {
            page.Children.Add(Heading(offer.IsUpdate ? "Update" : "Install"));
            page.Children.Add(Note(offer.Text));
            var install = new Button { Content = offer.Confirm + " SaveTether" };
            install.Click += async (_, _) =>
            {
                if (!await overlay.ConfirmAsync(offer.Title, offer.Text, offer.Confirm))
                {
                    return;
                }

                var (installed, problem) = await overlay.WorkAsync(offer.Confirm, offer.IsUpdate ? "Updating SaveTether…" : "Installing SaveTether…", installer.Install);
                if (!installed)
                {
                    App.Options.Log.Write(problem ?? "install failed");
                    await overlay.InformAsync("Nothing was installed", problem ?? "SaveTether could not be installed.");
                    return;
                }

                if (App.Instance is { } app)
                {
                    await app.LaunchInstalledAsync(installer, (title, text) => overlay.InformAsync(title, text));
                }
            };
            page.Children.Add(install);
        }

        page.Children.Add(Heading("About"));
        // The build stamps "<version>+<commit>"; the commit is shortened as git shows it.
        var stamped = Program.Version;
        var parts = stamped.Split('+', 2);
        var version = parts.Length == 2 ? $"{parts[0]} ({parts[1][..Math.Min(7, parts[1].Length)]})" : stamped;
        page.Children.Add(new TextBlock { Text = $"SaveTether {version}" });
        var open = new Button { Content = "Open" };
        open.Click += (_, _) => FolderOpener.Open(localFolder, App.Options.Log);
        page.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { new TextBlock { Text = $"Local folder: {localFolder}", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }, open },
        });
        page.Children.Add(Note("MIT licence. Game definitions marked unverified come from the Ludusavi manifest, compiled from PCGamingWiki."));
        var notices = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        if (File.Exists(notices))
        {
            var openNotices = new Button { Content = "Open third-party notices", HorizontalAlignment = HorizontalAlignment.Left };
            openNotices.Click += (_, _) => FolderOpener.Open(notices, App.Options.Log);
            page.Children.Add(openNotices);
        }

        Content = page;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) };

    private static TextBlock Note(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 };
}
