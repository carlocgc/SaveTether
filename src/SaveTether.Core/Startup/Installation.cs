using SaveTether.Core.FileSystem;
using SaveTether.Core.Definitions;

namespace SaveTether.Core.Startup;

/// <summary>
/// Where "Install SaveTether for this user" puts the programs (desktop-app-spec.md, section 11, "Installing").
/// Windows: everything in <c>%LOCALAPPDATA%\Programs\SaveTether</c>. Linux: the app and its native libraries in
/// <c>~/.local/lib/savetether</c>, the CLI and a <c>savetether-gui</c> link to the app in <c>~/.local/bin</c>, so that
/// folder holds only programs, and a menu entry in the applications folder (decided by the owner on 2026-11-19).
/// </summary>
/// <param name="AppFolder">The app, its libraries and the record of the installed version.</param>
/// <param name="BinFolder">Linux: where the CLI and the app's link go. Null on Windows, where both are in <paramref name="AppFolder"/>.</param>
/// <param name="MenuEntry">Linux: the desktop entry that puts SaveTether in the app menu. Null on Windows for now.</param>
public sealed record InstallLayout(TargetOs Os, string AppFolder, string? BinFolder, string? MenuEntry)
{
    public const string VersionFile = "version.txt";

    /// <summary>The installed app.</summary>
    public string AppPath => Path.Combine(AppFolder, Os == TargetOs.Windows ? "savetether-gui.exe" : "savetether-gui");

    /// <summary>The installed CLI.</summary>
    public string CliPath => Os == TargetOs.Windows ? Path.Combine(AppFolder, "savetether.exe") : Path.Combine(BinFolder!, "savetether");

    /// <summary>Linux: the link in the commands folder to the app. Null on Windows.</summary>
    public string? AppLink => BinFolder is null ? null : Path.Combine(BinFolder, "savetether-gui");

    /// <summary>The layout for this user on this machine; null on a system SaveTether does not install on.</summary>
    public static InstallLayout? ForSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return For(TargetOs.Windows, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null);
        }

        return OperatingSystem.IsLinux()
            ? For(TargetOs.Linux, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null, Environment.GetEnvironmentVariable("XDG_DATA_HOME"))
            : null;
    }

    /// <param name="xdgDataHome">Linux: <c>$XDG_DATA_HOME</c>, used when absolute; else <c>~/.local/share</c>.</param>
    public static InstallLayout For(TargetOs os, string home, string? localAppData, string? xdgDataHome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        if (os == TargetOs.Windows)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
            return new InstallLayout(os, Path.Combine(localAppData, "Programs", "SaveTether"), null, null);
        }

        var dataHome = string.IsNullOrEmpty(xdgDataHome) || !xdgDataHome.StartsWith('/') ? Path.Combine(home, ".local", "share") : xdgDataHome;
        return new InstallLayout(
            os,
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

/// <summary>Installs or updates SaveTether's programs for this user. Every move goes through <see cref="SafeFs"/>.</summary>
public static class Installer
{
    private const string Incoming = ".new";

    /// <summary>The package's files beside the app (docs/releasing.md), which an install copies.</summary>
    public static IReadOnlyList<string> PackageFiles(TargetOs os) => os == TargetOs.Windows
        ? ["savetether-gui.exe", "savetether.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"]
        : ["savetether-gui", "savetether", "libSkiaSharp.so", "libHarfBuzzSharp.so"];

    /// <summary>Whether to offer anything, for the app running from <paramref name="runningFolder"/>.</summary>
    public static InstallCheck Inspect(string runningFolder, InstallLayout layout, string runningVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runningFolder);
        ArgumentNullException.ThrowIfNull(layout);
        if (PathRules.AreSame(PathRules.ResolveAll(Path.GetFullPath(runningFolder)), PathRules.ResolveAll(layout.AppFolder)))
        {
            return new InstallCheck(InstallState.RunningInstalled, runningVersion);
        }

        if (PackageFiles(layout.Os).Any(name => SafeFs.Probe(Path.Combine(runningFolder, name)) != EntryKind.File))
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
    /// copied beside its destination first and then put in place, so a failure leaves the old program whole. On Linux the
    /// programs are made executable, the link in the commands folder points at the app, and the menu entry is written.
    /// Records the version last. Changes nothing outside the layout's folders.
    /// </summary>
    public static void Install(string sourceFolder, InstallLayout layout, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var fs = new SafeFs();
        Directory.CreateDirectory(layout.AppFolder);
        if (layout.BinFolder is not null)
        {
            Directory.CreateDirectory(layout.BinFolder);
        }

        foreach (var name in PackageFiles(layout.Os))
        {
            var destination = layout.Os == TargetOs.Linux && name == "savetether" ? layout.CliPath : Path.Combine(layout.AppFolder, name);
            var incoming = destination + Incoming;
            File.Copy(Path.Combine(sourceFolder, name), incoming, overwrite: true);
            if (!OperatingSystem.IsWindows() && name is "savetether" or "savetether-gui")
            {
                File.SetUnixFileMode(incoming, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            fs.ReplaceFile(incoming, destination);
        }

        foreach (var extra in new[] { "LICENSE", "README.md" })
        {
            if (SafeFs.Probe(Path.Combine(sourceFolder, extra)) == EntryKind.File)
            {
                var destination = Path.Combine(layout.AppFolder, extra);
                File.Copy(Path.Combine(sourceFolder, extra), destination + Incoming, overwrite: true);
                fs.ReplaceFile(destination + Incoming, destination);
            }
        }

        if (layout.AppLink is { } link)
        {
            if (SafeFs.Probe(link) is EntryKind.Symlink)
            {
                fs.RemoveLink(link);
            }

            if (SafeFs.Probe(link) != EntryKind.Missing)
            {
                throw new SafeFsException($"Cannot make '{link}' a link to SaveTether: something else is there.");
            }

            fs.RecreateRecordedLink(link, EntryKind.Symlink, layout.AppPath);
        }

        if (layout.MenuEntry is { } entry)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            SafeFs.WriteFileAtomically(entry, MenuEntry(layout.AppPath));
        }

        RemoveLeftovers(layout);
        SafeFs.WriteFileAtomically(Path.Combine(layout.AppFolder, InstallLayout.VersionFile), version + "\n");
    }

    /// <summary>The Linux menu entry: SaveTether in the app menu, starting the installed app with its window.</summary>
    public static string MenuEntry(string program)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        return
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=SaveTether\n" +
            "Comment=Keeps game save folders linked to one synced folder\n" +
            $"Exec={Autostart.QuoteExec(program)}\n" +
            "Terminal=false\n" +
            "Categories=Utility;\n";
    }

    /// <summary>Old program files an earlier install could not remove while they ran (Windows).</summary>
    private static void RemoveLeftovers(InstallLayout layout)
    {
        var fs = new SafeFs();
        foreach (var folder in new[] { layout.AppFolder, layout.BinFolder }.OfType<string>())
        {
            foreach (var old in Directory.EnumerateFiles(folder, "*.old-*"))
            {
                if (!PackageFiles(layout.Os).Any(name => Path.GetFileName(old).StartsWith(name + ".old-", StringComparison.Ordinal)))
                {
                    continue;
                }

                try
                {
                    fs.RemoveFile(old);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Still in use: the next install tries again.
                }
            }
        }
    }
}
