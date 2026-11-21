using SaveTether.Core.FileSystem;
using SaveTether.Core.Startup;

namespace SaveTether.Gui.Presentation;

/// <summary>The offer as the window shows it.</summary>
public sealed record InstallOfferView(bool IsUpdate, string Title, string Text, string Confirm);

/// <summary>
/// "Install SaveTether for this user?" on Linux (desktop-app-spec.md, section 11, "Installing", as decided by the owner on
/// 2026-11-19): offered at any start from somewhere other than the installed location, until declined there; and "Update
/// the installed SaveTether?" when an older version is installed. Not offered from an incomplete package, such as a
/// development build. "Not now" is remembered per location, in the local folder. Also Settings' Uninstall. On Windows the
/// installer does all of this, so there is no offer.
/// </summary>
public sealed class InstallOffer(string localFolder, InstallLayout layout, string runningFolder, string version)
{
    public const string DeclinedFile = "install-declined.txt";

    public const string UninstallTitle = "Uninstall SaveTether?";

    public InstallLayout Layout { get; } = layout;

    public string Version { get; } = version;

    /// <summary>Settings offers Uninstall only in the installed copy: an unpacked one is removed by deleting its folder.</summary>
    public bool CanUninstall => Installer.IsInstalledCopy(runningFolder, Layout);

    /// <summary>This user's offer for the app running from <paramref name="runningFolder"/>; null where SaveTether does not install itself.</summary>
    public static InstallOffer? ForSystem(string localFolder, string runningFolder, string version) =>
        InstallLayout.ForSystem() is { } layout ? new InstallOffer(localFolder, layout, runningFolder, version) : null;

    /// <summary>"0.2.0+abc1234" as "0.2.0 (abc1234)", as About shows it.</summary>
    public static string Display(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var parts = version.Split('+', 2);
        return parts.Length == 2 ? $"{parts[0]} ({parts[1][..Math.Min(7, parts[1].Length)]})" : version;
    }

    public InstallCheck Check() => Installer.Inspect(runningFolder, Layout, Version);

    /// <summary>
    /// What to ask, or null: nothing to offer, or "Not now" was chosen for this location. Settings asks with
    /// <paramref name="ignoreDeclined"/>, so "Not now" can be taken back.
    /// </summary>
    public InstallOfferView? Offer(bool ignoreDeclined = false)
    {
        var check = Check();
        if (!check.Offers || (!ignoreDeclined && Declined()))
        {
            return null;
        }

        if (check.State == InstallState.InstalledOlder)
        {
            return new InstallOfferView(
                true,
                $"Update the installed SaveTether to {Display(Version)}?",
                $"Version {Display(check.InstalledVersion ?? "unknown")} is installed in {Layout.AppFolder}. Updating replaces it with this one, "
                    + "closing it first if it is running, and starts it. Your setup and saves are not changed.",
                "Update");
        }

        return new InstallOfferView(
            false,
            "Install SaveTether for this user?",
            $"SaveTether is running from {Normalized(runningFolder)}. Installing copies SaveTether to {Layout.AppFolder}, adds the savetether "
                + $"command to {Layout.BinFolder}, and adds SaveTether to the app menu, then starts it from there. "
                + "It then keeps working if this folder is moved or deleted; the files here can be deleted afterwards.",
            "Install");
    }

    /// <summary>"Not now": not offered again for this location.</summary>
    public void Decline()
    {
        var file = Path.Combine(localFolder, DeclinedFile);
        var lines = ReadDeclined().Append(Normalized(runningFolder)).Distinct(StringComparer.Ordinal);
        Directory.CreateDirectory(localFolder);
        SafeFs.WriteFileAtomically(file, string.Join("\n", lines) + "\n");
    }

    public bool Declined() => ReadDeclined().Contains(Normalized(runningFolder), StringComparer.Ordinal);

    /// <summary>Installs or updates. Each program file is put in place whole or not at all.</summary>
    public (bool Installed, string? Problem) Install()
    {
        try
        {
            Installer.Install(runningFolder, Layout, Version);
            return (true, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SafeFsException)
        {
            return (false, $"SaveTether could not be installed in {Layout.AppFolder}: {e.Message}");
        }
    }

    /// <summary>Uninstall's confirmation: what goes, and what stays.</summary>
    public string UninstallText() =>
        $"This removes SaveTether's programs from {Layout.AppFolder} and {Layout.BinFolder}, its app menu entry, and starting with the session. "
        + "Your setup, your linked folders, their backups and the sync folder are not touched: linked folders stay linked and keep syncing, "
        + "but nothing checks on them until SaveTether is installed again. SaveTether quits afterwards.";

    /// <summary>Uninstall. A problem is said in words; files that are not SaveTether's are left and named.</summary>
    public (bool Done, string? Problem) Uninstall(IAutostartStore? autostart)
    {
        try
        {
            var (_, left) = Installer.Uninstall(Layout, autostart);
            return (true, left.Count == 0 ? null : "Some files were left, because they are not SaveTether's: " + string.Join("; ", left));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SafeFsException)
        {
            return (false, $"SaveTether could not be uninstalled: {e.Message}");
        }
    }

    private static string Normalized(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    private IEnumerable<string> ReadDeclined()
    {
        var file = Path.Combine(localFolder, DeclinedFile);
        return SafeFs.Probe(file) == EntryKind.File
            ? File.ReadAllLines(file).Where(l => l.Length > 0)
            : [];
    }
}
