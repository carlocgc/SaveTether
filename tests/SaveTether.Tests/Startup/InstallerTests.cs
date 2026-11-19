using SaveTether.Core.FileSystem;
using SaveTether.Core.Startup;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Startup;

/// <summary>
/// Desktop app spec section 11, "Installing", on Linux: where the programs go, when it is offered, the copy and its undoing.
/// On Windows a conventional installer does this; the tests that copy files run on Linux only, the rest everywhere.
/// </summary>
public class InstallerTests
{
    private static InstallLayout LayoutIn(Sandbox sandbox) => InstallLayout.For(sandbox.Dir("home"), null);

    /// <summary>An unpacked package: each program file, with its name and a version as content.</summary>
    private static string Package(Sandbox sandbox, string name, string content)
    {
        var folder = sandbox.Dir(name);
        foreach (var file in Installer.PackageFiles)
        {
            File.WriteAllText(Path.Combine(folder, file), $"{file} {content}");
        }

        File.WriteAllText(Path.Combine(folder, "LICENSE"), "MIT");
        File.WriteAllText(Path.Combine(folder, "savetether.png"), "icon");
        return folder;
    }

    private static string RepositoryRoot()
    {
        var folder = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(folder, "SaveTether.slnx")))
        {
            folder = Path.GetDirectoryName(folder) ?? throw new InvalidOperationException("The repository root was not found.");
        }

        return folder;
    }

    [Fact]
    public void The_layout_puts_the_app_in_lib_the_commands_in_bin_and_a_menu_entry_in_applications()
    {
        var linux = InstallLayout.For("/home/deck", null);
        Assert.Equal(Path.Combine("/home/deck", ".local", "lib", "savetether"), linux.AppFolder);
        Assert.Equal(Path.Combine(linux.AppFolder, "savetether-gui"), linux.AppPath);
        Assert.Equal(Path.Combine("/home/deck", ".local", "bin", "savetether"), linux.CliPath);
        Assert.Equal(Path.Combine("/home/deck", ".local", "bin", "savetether-gui"), linux.AppLink);
        Assert.Equal(Path.Combine("/home/deck", ".local", "share", "applications", "savetether.desktop"), linux.MenuEntry);
        Assert.Equal(Path.Combine("/data", "applications", "savetether.desktop"), InstallLayout.For("/home/deck", "/data").MenuEntry);
        Assert.Equal(linux.MenuEntry, InstallLayout.For("/home/deck", "relative").MenuEntry);
        Assert.Equal(OperatingSystem.IsLinux(), InstallLayout.ForSystem() is not null);
    }

    [Theory]
    [InlineData("0.3.0+abc", "0.2.0+def", 1)]
    [InlineData("0.2.0+abc", "0.2.0+def", 0)]
    [InlineData("0.2.1", "0.10.0", -1)]
    [InlineData("0.3.0-rc.1+abc", "0.3.0+def", -1)]
    [InlineData("0.3.0+abc", "0.3.0-rc.2", 1)]
    [InlineData("0.3.0-rc.2", "0.3.0-rc.1", 1)]
    public void Versions_are_ordered_ignoring_the_commit_with_a_prerelease_before_its_release(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(Installer.CompareVersions(a, b)));
    }

    [Fact]
    public void The_installer_copies_exactly_the_programs_the_linux_release_package_holds()
    {
        // build/check-package.sh is the package's file list; the installer copies its programs and libraries.
        var script = File.ReadAllLines(Path.Combine(RepositoryRoot(), "build", "check-package.sh"));
        var line = script.Single(l => l.TrimStart().StartsWith("linux-*) expected=(", StringComparison.Ordinal));
        var open = line.IndexOf('(', StringComparison.Ordinal);
        var listed = line[(open + 1)..line.IndexOf(')', open)]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Except(["LICENSE", "README.md", "savetether.png"]);
        Assert.Equal(listed.Order(StringComparer.Ordinal), Installer.PackageFiles.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_menu_entry_starts_the_installed_app_with_its_window()
    {
        var entry = Installer.MenuEntry("/home/deck/.local/lib/savetether/savetether-gui", "/home/deck/.local/lib/savetether/savetether.png");
        Assert.Contains("Exec=\"/home/deck/.local/lib/savetether/savetether-gui\"\n", entry, StringComparison.Ordinal);
        Assert.Contains("Icon=/home/deck/.local/lib/savetether/savetether.png\n", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("Icon=", Installer.MenuEntry("/x/savetether-gui", null), StringComparison.Ordinal);
        Assert.DoesNotContain("--minimized", entry, StringComparison.Ordinal);
        Assert.Contains("Type=Application\n", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_is_replaced_whole_and_a_folder_in_the_way_is_refused()
    {
        using var sandbox = new Sandbox();
        var fs = new SafeFs();
        var target = sandbox.At("program");
        File.WriteAllText(target, "old");
        File.WriteAllText(sandbox.At("program.new"), "new");

        fs.ReplaceFile(sandbox.At("program.new"), target);

        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal(["program"], Directory.EnumerateFileSystemEntries(sandbox.Work).Select(Path.GetFileName).Where(n => n!.StartsWith("program", StringComparison.Ordinal)));

        var folder = sandbox.Dir("a-folder");
        File.WriteAllText(sandbox.At("more.new"), "x");
        Assert.Throws<SafeFsException>(() => fs.ReplaceFile(sandbox.At("more.new"), folder));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Nothing_is_offered_from_an_incomplete_package_or_from_the_installed_copy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);
        var build = sandbox.Dir("bin", "Debug");
        File.WriteAllText(Path.Combine(build, "savetether-gui"), "the app without the CLI");
        Assert.Equal(InstallState.NotAPackage, Installer.Inspect(build, layout, "0.2.0").State);

        var package = Package(sandbox, "unpacked", "0.2.0");
        Assert.Equal(InstallState.NotInstalled, Installer.Inspect(package, layout, "0.2.0").State);
        Installer.Install(package, layout, "0.2.0+abc");
        Assert.Equal(InstallState.RunningInstalled, Installer.Inspect(layout.AppFolder, layout, "0.2.0+abc").State);
        Assert.Equal(InstallState.InstalledSameOrNewer, Installer.Inspect(package, layout, "0.2.0+def").State);

        var newer = Installer.Inspect(Package(sandbox, "newer", "0.3.0"), layout, "0.3.0+fff");
        Assert.Equal(InstallState.InstalledOlder, newer.State);
        Assert.Equal("0.2.0+abc", newer.InstalledVersion);
    }

    [Fact]
    public void Install_puts_each_program_in_place_and_an_update_replaces_them()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => Installer.Install("C:\\x", InstallLayout.For("C:\\home", null), "0.2.0"));
            return;
        }

        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);

        Installer.Install(Package(sandbox, "v2", "0.2.0"), layout, "0.2.0+abc");

        Assert.Equal("savetether-gui 0.2.0", File.ReadAllText(layout.AppPath));
        Assert.Equal("savetether 0.2.0", File.ReadAllText(layout.CliPath));
        Assert.Equal("MIT", File.ReadAllText(Path.Combine(layout.AppFolder, "LICENSE")));
        Assert.Equal("0.2.0+abc", Installer.InstalledVersion(layout));
        Assert.Equal(EntryKind.Symlink, SafeFs.Probe(layout.AppLink));
        Assert.Equal(layout.AppPath, SafeFs.ReadLinkTarget(layout.AppLink));
        Assert.Equal(Installer.MenuEntry(layout.AppPath, Path.Combine(layout.AppFolder, "savetether.png")), File.ReadAllText(layout.MenuEntry));
        Assert.True(File.GetUnixFileMode(layout.CliPath).HasFlag(UnixFileMode.UserExecute));
        Assert.True(File.GetUnixFileMode(layout.AppPath).HasFlag(UnixFileMode.UserExecute));
        // Only programs in the commands folder: the libraries stay with the app.
        Assert.Equal(["savetether", "savetether-gui"], Directory.EnumerateFileSystemEntries(layout.BinFolder).Select(Path.GetFileName).Order());

        Installer.Install(Package(sandbox, "v3", "0.3.0"), layout, "0.3.0+def");

        Assert.Equal("savetether-gui 0.3.0", File.ReadAllText(layout.AppPath));
        Assert.Equal("0.3.0+def", Installer.InstalledVersion(layout));
        Assert.DoesNotContain(Directory.EnumerateFiles(layout.AppFolder), f => f.EndsWith(".new", StringComparison.Ordinal) || f.Contains(".old-", StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_removes_only_what_install_put_there_and_the_autostart_entry_for_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);
        Installer.Install(Package(sandbox, "v2", "0.2.0"), layout, "0.2.0+abc");
        // Something of the owner's beside the CLI, and an autostart entry for the installed app.
        File.WriteAllText(Path.Combine(layout.BinFolder, "other-tool"), "mine");
        var autostart = new XdgAutostartStore(sandbox.Dir("config", "autostart"));
        autostart.Write(layout.AppPath);

        var (removed, left) = Installer.Uninstall(layout, autostart);

        Assert.Empty(left);
        Assert.False(Directory.Exists(layout.AppFolder));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(layout.CliPath));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(layout.AppLink));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(layout.MenuEntry));
        Assert.Null(autostart.ReadProgram());
        Assert.Contains(layout.AppFolder, removed);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(layout.BinFolder, "other-tool")));
    }

    [Fact]
    public void Uninstall_leaves_files_that_are_not_saveTethers_and_an_autostart_entry_for_another_copy()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);
        Installer.Install(Package(sandbox, "v2", "0.2.0"), layout, "0.2.0+abc");
        File.WriteAllText(Path.Combine(layout.AppFolder, "notes.txt"), "the owner's");
        var autostart = new XdgAutostartStore(sandbox.Dir("config", "autostart"));
        autostart.Write("/opt/elsewhere/savetether-gui");

        var (_, left) = Installer.Uninstall(layout, autostart);

        Assert.Single(left);
        Assert.Contains("notes.txt", left[0], StringComparison.Ordinal);
        Assert.Equal("the owner's", File.ReadAllText(Path.Combine(layout.AppFolder, "notes.txt")));
        Assert.Equal(EntryKind.Missing, SafeFs.Probe(layout.AppPath));
        Assert.Equal("/opt/elsewhere/savetether-gui", autostart.ReadProgram());
    }

    [Fact]
    public void The_cli_asks_or_needs_the_flag_and_without_it_removes_nothing()
    {
        using var sandbox = new Sandbox();
        var pc = new Cli.CliDevice(sandbox, "pc");
        var layout = InstallLayout.For(sandbox.Dir("install-home"), null);
        Directory.CreateDirectory(layout.AppFolder);
        Directory.CreateDirectory(layout.BinFolder);
        File.WriteAllText(layout.AppPath, "app");
        File.WriteAllText(layout.CliPath, "cli");
        pc.Install = layout;

        Assert.Contains("--confirm-uninstall", pc.Expect(SaveTether.Cli.CliApp.ExitRefused, "uninstall").Err, StringComparison.Ordinal);
        Assert.True(File.Exists(layout.AppPath));

        pc.Typing = new StringReader("no\n");
        Assert.Contains("Cancelled", pc.Expect(SaveTether.Cli.CliApp.ExitRefused, "uninstall").Err, StringComparison.Ordinal);
        Assert.True(File.Exists(layout.CliPath));

        pc.Typing = new StringReader("yes\n");
        Assert.Contains("Uninstalled SaveTether", pc.Ok("uninstall").Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(layout.AppFolder));
        Assert.False(File.Exists(layout.CliPath));
    }

    // Found on the Deck: the CLI's uninstall left the installed app running, from files it had just removed.
    [Fact]
    public void The_cli_asks_the_running_installed_app_to_quit_before_removing_it()
    {
        using var sandbox = new Sandbox();
        var pc = new Cli.CliDevice(sandbox, "pc");
        var layout = InstalledLayout(sandbox);
        var programs = new Cli.RunningProgramsStub();
        programs.Start(4242);
        var asked = false;
        using var app = SaveTether.Gui.Presentation.SingleInstance.TryClaim(pc.LocalFolder, () => { }, () =>
        {
            asked = true;
            programs.StopAll();
        });
        Assert.NotNull(app);
        pc.Install = layout;
        pc.RunningPrograms = programs;

        var run = pc.Ok("uninstall", "--confirm-uninstall");

        Assert.True(asked);
        Assert.DoesNotContain("still running", run.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(layout.AppFolder));
    }

    [Fact]
    public void The_cli_names_an_installed_app_that_did_not_quit()
    {
        using var sandbox = new Sandbox();
        var pc = new Cli.CliDevice(sandbox, "pc");
        var layout = InstalledLayout(sandbox);
        var programs = new Cli.RunningProgramsStub();
        programs.Start(4242);
        pc.Install = layout;
        pc.RunningPrograms = programs;
        pc.QuitWait = TimeSpan.FromMilliseconds(300);

        var run = pc.Ok("uninstall", "--confirm-uninstall");

        Assert.Contains("still running (process 4242)", run.Out, StringComparison.Ordinal);
        Assert.False(Directory.Exists(layout.AppFolder));
    }

    private static InstallLayout InstalledLayout(Sandbox sandbox)
    {
        var layout = InstallLayout.For(sandbox.Dir("install-home"), null);
        Directory.CreateDirectory(layout.AppFolder);
        Directory.CreateDirectory(layout.BinFolder);
        File.WriteAllText(layout.AppPath, "app");
        File.WriteAllText(layout.CliPath, "cli");
        return layout;
    }

    [Fact]
    public void The_offer_is_made_until_declined_for_that_location_and_names_what_it_does()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);
        var local = sandbox.Dir("local");
        var unpacked = Package(sandbox, "unpacked", "0.2.0");
        var offer = new InstallOffer(local, layout, unpacked, "0.2.0+abc");

        var view = offer.Offer();
        Assert.NotNull(view);
        Assert.False(view.IsUpdate);
        Assert.Equal("Install SaveTether for this user?", view.Title);
        Assert.Contains(layout.AppFolder, view.Text, StringComparison.Ordinal);
        Assert.False(offer.CanUninstall);

        // The app's own folder comes with a trailing separator; the offer names it without (found on the Deck).
        var withSeparator = new InstallOffer(local, layout, unpacked + Path.DirectorySeparatorChar, "0.2.0+abc").Offer()!;
        Assert.Contains($"running from {unpacked}. ", withSeparator.Text, StringComparison.Ordinal);

        offer.Decline();
        Assert.Null(offer.Offer());
        Assert.Null(new InstallOffer(local, layout, unpacked + Path.DirectorySeparatorChar, "0.2.0+abc").Offer());
        // Another unpacked copy is asked again.
        Assert.NotNull(new InstallOffer(local, layout, Package(sandbox, "elsewhere", "0.2.0"), "0.2.0+abc").Offer());
    }

    [Fact]
    public void An_older_install_is_offered_an_update_and_the_installed_copy_offers_uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);
        var local = sandbox.Dir("local");
        Assert.Equal((true, null), new InstallOffer(local, layout, Package(sandbox, "v2", "0.2.0"), "0.2.0+abc").Install());

        var update = new InstallOffer(local, layout, Package(sandbox, "v3", "0.3.0"), "0.3.0+def");
        var view = update.Offer();
        Assert.NotNull(view);
        Assert.True(view.IsUpdate);
        Assert.Equal("Update the installed SaveTether to 0.3.0 (def)?", view.Title);
        Assert.Contains("Version 0.2.0 (abc) is installed", view.Text, StringComparison.Ordinal);
        Assert.Equal((true, null), update.Install());
        Assert.Null(update.Offer());

        var installed = new InstallOffer(local, layout, layout.AppFolder, "0.3.0+def");
        Assert.True(installed.CanUninstall);
        Assert.Contains("linked folders stay linked", installed.UninstallText(), StringComparison.Ordinal);
        Assert.Equal((true, null), installed.Uninstall(autostart: null));
        Assert.False(Directory.Exists(layout.AppFolder));
    }
}
