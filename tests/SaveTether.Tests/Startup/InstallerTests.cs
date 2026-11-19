using SaveTether.Core.Definitions;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Startup;
using SaveTether.Gui.Presentation;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Startup;

/// <summary>Desktop app spec section 11, "Installing": where the programs go, when it is offered, and the copy itself.</summary>
public class InstallerTests
{
    private static TargetOs Host => OperatingSystem.IsWindows() ? TargetOs.Windows : TargetOs.Linux;

    private static InstallLayout LayoutIn(Sandbox sandbox) =>
        InstallLayout.For(Host, sandbox.Dir("home"), sandbox.Dir("localappdata"), null);

    /// <summary>An unpacked package: each program file, with its name and a version as content.</summary>
    private static string Package(Sandbox sandbox, string name, string content)
    {
        var folder = sandbox.Dir(name);
        foreach (var file in Installer.PackageFiles(Host))
        {
            File.WriteAllText(Path.Combine(folder, file), $"{file} {content}");
        }

        File.WriteAllText(Path.Combine(folder, "LICENSE"), "MIT");
        return folder;
    }

    [Fact]
    public void The_layout_is_one_folder_on_windows_and_lib_bin_and_a_menu_entry_on_linux()
    {
        var windows = InstallLayout.For(TargetOs.Windows, @"C:\Users\pat", @"C:\Users\pat\AppData\Local", null);
        Assert.Equal(Path.Combine(@"C:\Users\pat\AppData\Local", "Programs", "SaveTether"), windows.AppFolder);
        Assert.Equal(Path.Combine(windows.AppFolder, "savetether-gui.exe"), windows.AppPath);
        Assert.Equal(Path.Combine(windows.AppFolder, "savetether.exe"), windows.CliPath);
        Assert.Null(windows.BinFolder);
        Assert.Null(windows.AppLink);
        Assert.Null(windows.MenuEntry);

        var linux = InstallLayout.For(TargetOs.Linux, "/home/deck", null, null);
        Assert.Equal(Path.Combine("/home/deck", ".local", "lib", "savetether"), linux.AppFolder);
        Assert.Equal(Path.Combine("/home/deck", ".local", "bin", "savetether"), linux.CliPath);
        Assert.Equal(Path.Combine("/home/deck", ".local", "bin", "savetether-gui"), linux.AppLink);
        Assert.Equal(Path.Combine("/home/deck", ".local", "share", "applications", "savetether.desktop"), linux.MenuEntry);
        Assert.Equal(Path.Combine("/data", "applications", "savetether.desktop"), InstallLayout.For(TargetOs.Linux, "/home/deck", null, "/data").MenuEntry);
        Assert.Equal(linux.MenuEntry, InstallLayout.For(TargetOs.Linux, "/home/deck", null, "relative").MenuEntry);
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
    public void Nothing_is_offered_from_an_incomplete_package_or_from_the_installed_copy()
    {
        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);
        var build = sandbox.Dir("bin", "Debug");
        File.WriteAllText(Path.Combine(build, Installer.PackageFiles(Host)[0]), "the app without the CLI");
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
        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);

        Installer.Install(Package(sandbox, "v2", "0.2.0"), layout, "0.2.0+abc");

        Assert.Equal($"{Installer.PackageFiles(Host)[0]} 0.2.0", File.ReadAllText(layout.AppPath));
        Assert.StartsWith(Host == TargetOs.Windows ? "savetether.exe 0.2.0" : "savetether 0.2.0", File.ReadAllText(layout.CliPath), StringComparison.Ordinal);
        Assert.Equal("MIT", File.ReadAllText(Path.Combine(layout.AppFolder, "LICENSE")));
        Assert.Equal("0.2.0+abc", Installer.InstalledVersion(layout));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(EntryKind.Symlink, SafeFs.Probe(layout.AppLink!));
            Assert.Equal(layout.AppPath, SafeFs.ReadLinkTarget(layout.AppLink!));
            Assert.Equal(Installer.MenuEntry(layout.AppPath), File.ReadAllText(layout.MenuEntry!));
            Assert.True(File.GetUnixFileMode(layout.CliPath).HasFlag(UnixFileMode.UserExecute));
            Assert.True(File.GetUnixFileMode(layout.AppPath).HasFlag(UnixFileMode.UserExecute));
            // Only programs in the commands folder: the libraries stay with the app.
            Assert.Equal(["savetether", "savetether-gui"], Directory.EnumerateFileSystemEntries(layout.BinFolder!).Select(Path.GetFileName).Order());
        }

        Installer.Install(Package(sandbox, "v3", "0.3.0"), layout, "0.3.0+def");

        Assert.Equal($"{Installer.PackageFiles(Host)[0]} 0.3.0", File.ReadAllText(layout.AppPath));
        Assert.Equal("0.3.0+def", Installer.InstalledVersion(layout));
        // Nothing left over from the copy or the old files.
        Assert.DoesNotContain(Directory.EnumerateFiles(layout.AppFolder), f => f.EndsWith(".new", StringComparison.Ordinal) || f.Contains(".old-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("win-*", TargetOs.Windows)]
    [InlineData("linux-*", TargetOs.Linux)]
    public void The_installer_copies_exactly_the_programs_the_release_package_holds(string rid, TargetOs os)
    {
        // build/check-package.sh is the package's file list; the installer copies its programs and libraries.
        var script = File.ReadAllLines(Path.Combine(RepositoryRoot(), "build", "check-package.sh"));
        var line = script.Single(l => l.TrimStart().StartsWith(rid + ") expected=(", StringComparison.Ordinal));
        var listed = line[(line.IndexOf('(', StringComparison.Ordinal) + 1)..line.IndexOf(')', line.IndexOf('(', StringComparison.Ordinal))]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Except(["LICENSE", "README.md"]);
        Assert.Equal(listed.Order(StringComparer.Ordinal), Installer.PackageFiles(os).Order(StringComparer.Ordinal));
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
    public void The_menu_entry_starts_the_installed_app_with_its_window()
    {
        var entry = Installer.MenuEntry("/home/deck/.local/lib/savetether/savetether-gui");
        Assert.Contains("Exec=\"/home/deck/.local/lib/savetether/savetether-gui\"\n", entry, StringComparison.Ordinal);
        Assert.DoesNotContain("--minimized", entry, StringComparison.Ordinal);
        Assert.Contains("Type=Application\n", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_is_replaced_whole_and_a_link_or_folder_in_the_way_is_refused()
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
    public void The_path_gains_the_folder_once_keeping_what_is_there()
    {
        Assert.Equal(@"C:\Tools;C:\SaveTether", UserPath.WithFolder(@"C:\Tools", @"C:\SaveTether"));
        Assert.Equal(@"C:\Tools;C:\SaveTether", UserPath.WithFolder(@"C:\Tools;", @"C:\SaveTether\"));
        Assert.Equal(@"C:\SaveTether", UserPath.WithFolder(null, @"C:\SaveTether"));
        Assert.Null(UserPath.WithFolder(@"%USERPROFILE%\bin;c:\savetether\", @"C:\SaveTether"));
        // An empty entry between others is kept; trailing semicolons mean nothing and are not.
        Assert.Equal(@"%USERPROFILE%\bin;;C:\Tools;C:\SaveTether", UserPath.WithFolder(@"%USERPROFILE%\bin;;C:\Tools;;", @"C:\SaveTether"));
    }

    [Fact]
    public void The_path_is_written_with_its_type_kept_under_a_test_key()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var keyPath = @"Software\SaveTetherTests\Environment-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("Path", @"%USERPROFILE%\bin", Microsoft.Win32.RegistryValueKind.ExpandString);
            }

            Assert.True(UserPath.Add(@"C:\SaveTether", keyPath));
            Assert.False(UserPath.Add(@"C:\SaveTether", keyPath));
            using var read = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath)!;
            Assert.Equal(Microsoft.Win32.RegistryValueKind.ExpandString, read.GetValueKind("Path"));
            Assert.Equal(@"%USERPROFILE%\bin;C:\SaveTether", read.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void The_offer_is_made_until_declined_for_that_location_and_names_what_it_does()
    {
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
        Assert.Equal(Host == TargetOs.Windows, view.OffersPath);

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
    public void An_older_install_is_offered_an_update_which_puts_the_new_version_in_place()
    {
        using var sandbox = new Sandbox();
        var layout = LayoutIn(sandbox);
        var local = sandbox.Dir("local");
        Assert.Equal((true, null), new InstallOffer(local, layout, Package(sandbox, "v2", "0.2.0"), "0.2.0+abc").Install(addToPath: false));

        var update = new InstallOffer(local, layout, Package(sandbox, "v3", "0.3.0"), "0.3.0+def");
        var view = update.Offer();
        Assert.NotNull(view);
        Assert.True(view.IsUpdate);
        Assert.Equal("Update the installed SaveTether to 0.3.0 (def)?", view.Title);
        Assert.Contains("Version 0.2.0 (abc) is installed", view.Text, StringComparison.Ordinal);

        Assert.Equal((true, null), update.Install(addToPath: false));
        Assert.Equal("0.3.0+def", Installer.InstalledVersion(layout));
        Assert.Null(update.Offer());
    }
}
