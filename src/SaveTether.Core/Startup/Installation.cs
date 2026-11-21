using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Startup;

/// <summary>
/// Where "Install SaveTether for this user" puts the programs on Linux (desktop-app-spec.md, section 11, "Installing",
/// as decided by the owner on 2026-11-19): the app and its native libraries in <c>~/.local/lib/savetether</c>, the CLI and
/// a <c>savetether-gui</c> link to the app in <c>~/.local/bin</c>, so that folder holds only programs, and a menu entry in
/// the applications folder. On Windows a conventional installer does this instead (<c>build/windows/savetether.iss</c>).
/// </summary>
/// <param name="AppFolder">The app, its libraries and the record of the installed version.</param>
/// <param name="BinFolder">The CLI and the app's link.</param>
/// <param name="MenuEntry">The desktop entry that puts SaveTether in the app menu.</param>
public sealed record InstallLayout(string AppFolder, string BinFolder, string MenuEntry)
{
    public const string VersionFile = "version.txt";

    /// <summary>The installed app.</summary>
    public string AppPath => Path.Combine(AppFolder, "savetether-gui");

    /// <summary>The installed CLI.</summary>
    public string CliPath => Path.Combine(BinFolder, "savetether");

    /// <summary>The link in the commands folder to the app.</summary>
    public string AppLink => Path.Combine(BinFolder, "savetether-gui");

    /// <summary>This user's layout on Linux; null elsewhere, where SaveTether does not install itself.</summary>
    public static InstallLayout? ForSystem() => OperatingSystem.IsLinux()
        ? For(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("XDG_DATA_HOME"))
        : null;

    /// <param name="xdgDataHome"><c>$XDG_DATA_HOME</c>, used when absolute; else <c>~/.local/share</c>.</param>
    public static InstallLayout For(string home, string? xdgDataHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var dataHome = string.IsNullOrEmpty(xdgDataHome) || !xdgDataHome.StartsWith('/') ? Path.Combine(home, ".local", "share") : xdgDataHome;
        return new InstallLayout(
            Path.Combine(home, ".local", "lib", "savetether"),
            Path.Combine(home, ".local", "bin"),
            Path.Combine(dataHome, "applications", "savetether.desktop"));
    }
}

/// <summary>What starting the app from where it is means for installing.</summary>
public enum InstallState
{
    /// <summary>This is the installed app. Nothing to offer.</summary>
    RunningInstalled,

    /// <summary>The files beside the app are not a whole package (a development build, say). Nothing to offer.</summary>
    NotAPackage,

    /// <summary>Nothing is installed: offer to install.</summary>
    NotInstalled,

    /// <summary>An older version is installed: offer to update it.</summary>
    InstalledOlder,

    /// <summary>The same or a newer version is installed. Nothing to offer.</summary>
    InstalledSameOrNewer,
}

public sealed record InstallCheck(InstallState State, string? InstalledVersion)
{
    public bool Offers => State is InstallState.NotInstalled or InstallState.InstalledOlder;
}

/// <summary>Installs, updates and uninstalls SaveTether's programs for this user on Linux. Every change goes through <see cref="SafeFs"/>.</summary>
public static class Installer
{
    private const string Incoming = ".new";

    /// <summary>The Linux package's programs and libraries (docs/releasing.md), which an install copies.</summary>
    public static IReadOnlyList<string> PackageFiles { get; } = ["savetether-gui", "savetether", "libSkiaSharp.so", "libHarfBuzzSharp.so"];

    /// <summary>Copied when the package has them: the licence, the readme, and the icon the menu entry shows.</summary>
    private static readonly string[] Extras = ["LICENSE", "README.md", "THIRD-PARTY-NOTICES.md", IconFile];

    private const string IconFile = "savetether.png";

    /// <summary>Whether to offer anything, for the app running from <paramref name="runningFolder"/>.</summary>
    public static InstallCheck Inspect(string runningFolder, InstallLayout layout, string runningVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runningFolder);
        ArgumentNullException.ThrowIfNull(layout);
        if (IsInstalledCopy(runningFolder, layout))
        {
            return new InstallCheck(InstallState.RunningInstalled, runningVersion);
        }

        if (PackageFiles.Any(name => SafeFs.Probe(Path.Combine(runningFolder, name)) != EntryKind.File))
        {
            return new InstallCheck(InstallState.NotAPackage, null);
        }

        if (SafeFs.Probe(layout.AppPath) != EntryKind.File)
        {
            return new InstallCheck(InstallState.NotInstalled, null);
        }

        var installed = InstalledVersion(layout);
        return new InstallCheck(
            installed is null || CompareVersions(runningVersion, installed) > 0 ? InstallState.InstalledOlder : InstallState.InstalledSameOrNewer,
            installed);
    }

    /// <summary>True when <paramref name="runningFolder"/> is the installed app's folder.</summary>
    public static bool IsInstalledCopy(string runningFolder, InstallLayout layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runningFolder);
        ArgumentNullException.ThrowIfNull(layout);
        return PathRules.AreSame(PathRules.ResolveAll(Path.GetFullPath(runningFolder)), PathRules.ResolveAll(layout.AppFolder));
    }

    /// <summary>The installed version, as recorded at install; null when there is no record.</summary>
    public static string? InstalledVersion(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var file = Path.Combine(layout.AppFolder, InstallLayout.VersionFile);
        return SafeFs.Probe(file) == EntryKind.File ? File.ReadAllText(file).Trim() : null;
    }

    /// <summary>
    /// Orders two versions as written by the build, "0.2.0+&lt;commit&gt;" or "0.3.0-rc.1+&lt;commit&gt;": the commit is
    /// ignored, a prerelease comes before its release, and anything that does not parse is compared as text.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        static (Version? Number, string Pre) Split(string v)
        {
            var core = v.Split('+', 2)[0];
            var dash = core.IndexOf('-', StringComparison.Ordinal);
            var number = dash < 0 ? core : core[..dash];
            return (Version.TryParse(number, out var parsed) ? parsed : null, dash < 0 ? string.Empty : core[(dash + 1)..]);
        }

        var (na, pa) = Split(a);
        var (nb, pb) = Split(b);
        if (na is null || nb is null)
        {
            return string.CompareOrdinal(a.Split('+', 2)[0], b.Split('+', 2)[0]);
        }

        var byNumber = na.CompareTo(nb);
        if (byNumber != 0)
        {
            return byNumber;
        }

        if (pa.Length == 0 || pb.Length == 0)
        {
            return pb.Length.CompareTo(pa.Length);
        }

        return string.CompareOrdinal(pa, pb);
    }

    /// <summary>
    /// Copies the package from <paramref name="sourceFolder"/> into the layout, replacing an older install: each file is
    /// copied beside its destination first and then put in place, so a failure leaves the old program whole. The programs
    /// are made executable, the link in the commands folder points at the app, and the menu entry is written. Records the
    /// version last. Changes nothing outside the layout's folders.
    /// </summary>
    public static void Install(string sourceFolder, InstallLayout layout, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("On Windows SaveTether is installed by its installer.");
        }

        var fs = new SafeFs();
        Directory.CreateDirectory(layout.AppFolder);
        Directory.CreateDirectory(layout.BinFolder);
        foreach (var name in PackageFiles.Concat(Extras.Where(e => SafeFs.Probe(Path.Combine(sourceFolder, e)) == EntryKind.File)))
        {
            var destination = name == "savetether" ? layout.CliPath : Path.Combine(layout.AppFolder, name);
            var incoming = destination + Incoming;
            File.Copy(Path.Combine(sourceFolder, name), incoming, overwrite: true);
            if (name is "savetether" or "savetether-gui")
            {
                File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            fs.ReplaceFile(incoming, destination);
        }

        if (SafeFs.Probe(layout.AppLink) is EntryKind.Symlink)
        {
            fs.RemoveLink(layout.AppLink);
        }

        if (SafeFs.Probe(layout.AppLink) != EntryKind.Missing)
        {
            throw new SafeFsException($"Cannot make '{layout.AppLink}' a link to SaveTether: something else is there.");
        }

        fs.RecreateRecordedLink(layout.AppLink, EntryKind.Symlink, layout.AppPath);
        Directory.CreateDirectory(Path.GetDirectoryName(layout.MenuEntry)!);
        var icon = Path.Combine(layout.AppFolder, IconFile);
        SafeFs.WriteFileAtomically(layout.MenuEntry, MenuEntry(layout.AppPath, SafeFs.Probe(icon) == EntryKind.File ? icon : null));
        RemoveLeftovers(layout);
        SafeFs.WriteFileAtomically(Path.Combine(layout.AppFolder, InstallLayout.VersionFile), version + "\n");
    }

    /// <summary>
    /// Before uninstalling from outside the app: asks the installed app, when it runs, to quit, as it would from its tray
    /// menu, and waits up to <paramref name="wait"/> for it to end. Only the copy running for this local folder hears the
    /// request. Returns the ids of installed copies still running, which the caller names: left running, they would go on
    /// from files that are gone, until quit or the end of the session.
    /// </summary>
    public static IReadOnlyList<int> QuitRunning(InstallLayout layout, string localFolder, IRunningPrograms programs, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(programs);
        var running = programs.Running(layout.AppPath);
        if (running.Count == 0)
        {
            return running;
        }

        AppChannel.RequestQuit(localFolder);
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(100);
            running = programs.Running(layout.AppPath);
            if (running.Count == 0)
            {
                break;
            }
        }

        return running;
    }

    /// <summary>
    /// Removes what <see cref="Install"/> put in place, and nothing else: the menu entry, the link (only while it points at
    /// the installed app), the CLI, the app folder's own files and then the folder once empty, and the autostart entry when
    /// it starts the installed app. This user's setup, links, backups and the sync folder are not touched. Returns what was
    /// removed, and what was left and why.
    /// </summary>
    public static (IReadOnlyList<string> Removed, IReadOnlyList<string> Left) Uninstall(InstallLayout layout, IAutostartStore? autostart)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var fs = new SafeFs();
        var removed = new List<string>();
        var left = new List<string>();

        void RemoveFile(string path)
        {
            if (SafeFs.Probe(path) == EntryKind.File)
            {
                fs.RemoveFile(path);
                removed.Add(path);
            }
        }

        if (autostart is not null && Autostart.Check(autostart).Program is { } program
            && (PathRules.AreSame(program, layout.AppPath) || PathRules.AreSame(program, layout.AppLink)))
        {
            autostart.Remove();
            removed.Add(autostart.Location);
        }

        RemoveFile(layout.MenuEntry);
        if (SafeFs.Probe(layout.AppLink) == EntryKind.Symlink)
        {
            if (PathRules.AreSame(SafeFs.ReadLinkTarget(layout.AppLink), layout.AppPath))
            {
                fs.RemoveLink(layout.AppLink);
                removed.Add(layout.AppLink);
            }
            else
            {
                left.Add($"{layout.AppLink}: a link SaveTether did not make");
            }
        }

        RemoveFile(layout.CliPath);
        if (SafeFs.Probe(layout.AppFolder) == EntryKind.Directory)
        {
            foreach (var name in PackageFiles.Where(n => n != "savetether").Concat(Extras).Append(InstallLayout.VersionFile))
            {
                RemoveFile(Path.Combine(layout.AppFolder, name));
            }

            RemoveLeftovers(layout);
            var others = Directory.GetFileSystemEntries(layout.AppFolder);
            if (others.Length == 0)
            {
                fs.DeleteTree(layout.AppFolder);
                removed.Add(layout.AppFolder);
            }
            else
            {
                left.AddRange(others.Select(o => $"{o}: not SaveTether's"));
            }
        }

        return (removed, left);
    }

    /// <summary>The menu entry: SaveTether in the app menu, with its icon, starting the installed app with its window.</summary>
    public static string MenuEntry(string program, string? icon)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        return
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=SaveTether\n" +
            "Comment=Keeps game save folders linked to one synced folder\n" +
            $"Exec={Autostart.QuoteExec(program)}\n" +
            (icon is null ? string.Empty : $"Icon={icon}\n") +
            "Terminal=false\n" +
            "Categories=Utility;\n";
    }

    /// <summary>Copies left by an install that stopped part way, and old files put aside by an update.</summary>
    private static void RemoveLeftovers(InstallLayout layout)
    {
        var fs = new SafeFs();
        var names = PackageFiles.Concat(Extras).ToList();
        foreach (var folder in new[] { layout.AppFolder, layout.BinFolder }.Where(f => SafeFs.Probe(f) == EntryKind.Directory))
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (names.Any(n => name == n + Incoming || name.StartsWith(n + ".old-", StringComparison.Ordinal)))
                {
                    try
                    {
                        fs.RemoveFile(file);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // Left for the next install.
                    }
                }
            }
        }
    }
}
