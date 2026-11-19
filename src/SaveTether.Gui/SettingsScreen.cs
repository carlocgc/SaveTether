using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SaveTether.Gui.Presentation;

namespace SaveTether.Gui;

/// <summary>
/// The Settings page (desktop-app-spec.md, section 11), in place of the Apps page while open. Built so far:
/// Start with the session, and About. The backup location and This device come later.
/// </summary>
internal sealed class SettingsScreen : UserControl, IScreen
{
    /// <summary>The way back, shown by the window above the scrolling page.</summary>
    public ContentControl Bar { get; } = new();

    public SettingsScreen(string localFolder, SessionStart session, Overlay overlay, Action close)
    {
        var page = new StackPanel { Spacing = 10, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        Bar.Content = Navigation.BackRow("Back to apps", close);
        page.Children.Add(new TextBlock { Text = "Settings", FontSize = 22, FontWeight = FontWeight.SemiBold });

        page.Children.Add(Heading("Start with the session"));
        if (session.Available)
        {
            var toggle = new ToggleSwitch { IsChecked = session.IsOn, OnContent = "On", OffContent = "Off" };
            AutomationProperties.SetName(toggle, "Start with the session");
            var applying = false;
            toggle.IsCheckedChanged += async (_, _) =>
            {
                if (applying)
                {
                    return;
                }

                var on = toggle.IsChecked == true;
                if (session.Set(on) is { } problem)
                {
                    App.Options.Log.Write(problem);
                    applying = true;
                    toggle.IsChecked = session.IsOn;
                    applying = false;
                    await overlay.InformAsync("Nothing was changed", problem);
                    return;
                }

                App.Options.Log.Write($"start with the session: {(on ? "on" : "off")}");
            };
            page.Children.Add(toggle);
            page.Children.Add(Note(
                "SaveTether starts in the tray when you log in, and checks your save folders in the background. " +
                "While this is on, the entry is updated with SaveTether's location each time it starts."));
        }
        else
        {
            page.Children.Add(Note("This desktop has no place for programs that start with the session."));
        }

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
        page.Children.Add(Note("MIT licence."));

        Content = page;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) };

    private static TextBlock Note(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, FontSize = 13 };
}
