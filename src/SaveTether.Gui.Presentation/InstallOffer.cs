using SaveTether.Core.Definitions;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Startup;

namespace SaveTether.Gui.Presentation;

/// <summary>The offer as the window shows it.</summary>
/// <param name="OffersPath">Windows: the opt-in "available in terminals" box (decision 10).</param>
public sealed record InstallOfferView(bool IsUpdate, string Title, string Text, string Confirm, bool OffersPath, string PathLabel);

/// <summary>
/// "Install SaveTether for this user?" (desktop-app-spec.md, section 11, "Installing", as decided by the owner on
/// 2026-11-19): offered at any start from somewhere other than the installed location, until declined there; and
/// "Update the installed SaveTether?" when an older version is installed. Not offered from an incomplete package,
/// such as a development build. "Not now" is remembered per location, in the local folder.
/// </summary>
public sealed class InstallOffer(string localFolder, InstallLayout layout, string runningFolder, string version)
{
    public const string DeclinedFile = "install-declined.txt";

    public InstallLayout Layout { get; } = layout;

    public string Version { get; } = version;

    /// <summary>This user's offer for the app running from <paramref name="runningFolder"/>; null when there is none.</summary>
    public static InstallOffer? ForSystem(string localFolder, string runningFolder, string version) =>
        InstallLayout.ForSystem() is { } layout ? new InstallOffer(localFolder, layout, runningFolder, version) : null;

    public InstallCheck Check() => Installer.Inspect(runningFolder, Layout, Version);

    /// <summary>What to ask, or null: nothing to offer, or "Not now" was chosen for this location.</summary>
    public InstallOfferView? Offer()
    {
        var check = Check();
        if (!check.Offers || Declined())
        {
            return null;
        }

        var windows = Layout.Os == TargetOs.Windows;
        var pathLabel = $"Make the savetether command available in terminals (adds {Layout.AppFolder} to your Path)";
        if (check.State == InstallState.InstalledOlder)
        {
            return new InstallOfferView(
                true,
                $"Update the installed SaveTether to {Display(Version)}?",
                $"Version {Display(check.InstalledVersion ?? "unknown")} is installed in {Layout.AppFolder}. Updating replaces it with this one, "
                    + "closing it first if it is running, and starts it. Your setup and saves are not changed.",
                "Update",
                windows,
                pathLabel);
        }

        var where = windows
            ? $"Installing copies SaveTether to {Layout.AppFolder}"
            : $"Installing copies SaveTether to {Layout.AppFolder}, adds the savetether command to {Layout.BinFolder}, and adds SaveTether to the app menu";
        return new InstallOfferView(
            false,
            "Install SaveTether for this user?",
            $"SaveTether is running from {runningFolder}. {where}, then starts it from there. "
                + "It then keeps working if this folder is moved or deleted; the files here can be deleted afterwards.",
            "Install",
            windows,
            pathLabel);
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

    /// <summary>
    /// Installs or updates. Each program file is put in place whole or not at all; a problem after the programs are in
    /// place (the Path) leaves them installed and is said.
    /// </summary>
    public (bool Installed, string? Problem) Install(bool addToPath)
    {
        try
        {
            Installer.Install(runningFolder, Layout, Version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SafeFsException)
        {
            return (false, $"SaveTether could not be installed in {Layout.AppFolder}: {e.Message}");
        }

        if (addToPath && OperatingSystem.IsWindows())
        {
            try
            {
                UserPath.Add(Layout.AppFolder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return (true, $"SaveTether was installed, but its folder could not be added to your Path: {e.Message}");
            }
        }

        return (true, null);
    }

    /// <summary>"0.2.0+abc1234" as "0.2.0 (abc1234)", as About shows it.</summary>
    public static string Display(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var parts = version.Split('+', 2);
        return parts.Length == 2 ? $"{parts[0]} ({parts[1][..Math.Min(7, parts[1].Length)]})" : version;
    }

    private IEnumerable<string> ReadDeclined()
    {
        var file = Path.Combine(localFolder, DeclinedFile);
        return SafeFs.Probe(file) == EntryKind.File
            ? File.ReadAllLines(file).Where(l => l.Length > 0)
            : [];
    }

    private static string Normalized(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
