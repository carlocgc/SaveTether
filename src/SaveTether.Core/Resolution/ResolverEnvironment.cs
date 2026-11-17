using SaveTether.Core.Definitions;

namespace SaveTether.Core.Resolution;

/// <summary>
/// What the resolver needs to know about this machine. <see cref="FromSystem"/> reads the real
/// one; tests build one pointing into a fixture tree, so no real Steam, SD card or EmuDeck is needed.
/// </summary>
public sealed class ResolverEnvironment
{
    public required TargetOs Os { get; init; }

    /// <summary>The user's home folder: the profile folder on Windows, $HOME on Linux.</summary>
    public required string Home { get; init; }

    /// <summary>The user's login name. Used to pick the user folder inside a Wine prefix.</summary>
    public string? UserName { get; init; }

    /// <summary>Windows only: the roaming application data folder.</summary>
    public string? AppData { get; init; }

    /// <summary>Windows only: the local application data folder.</summary>
    public string? LocalAppData { get; init; }

    /// <summary>Windows only: the LocalLow folder that Unity games and others save into.</summary>
    public string? LocalLow { get; init; }

    /// <summary>Windows only: the user's Documents folder.</summary>
    public string? Documents { get; init; }

    /// <summary>Reads an environment variable. Used for XDG_CONFIG_HOME and XDG_DATA_HOME.</summary>
    public Func<string, string?> GetVariable { get; init; } = _ => null;

    /// <summary>
    /// Linux only: the mount table in the format of /proc/mounts, or null when it cannot be read.
    /// The SD card is found from it.
    /// </summary>
    public Func<string?> ReadMounts { get; init; } = () => null;

    /// <summary>
    /// Linux only: the system-wide Flatpak installation, where an installed app has a folder
    /// app/&lt;id&gt;. The per-user installation is found under the XDG data folder.
    /// </summary>
    public string? FlatpakSystemInstallation { get; init; }

    public static ResolverEnvironment FromSystem()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new ResolverEnvironment
            {
                Os = TargetOs.Windows,
                Home = home,
                UserName = Environment.UserName,
                AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                LocalAppData = local,

                // There is no SpecialFolder for LocalLow. It is the sibling of Local unless the
                // owner has moved it, which Windows offers no setting for.
                LocalLow = Path.Combine(Path.GetDirectoryName(local) ?? home, "LocalLow"),
                Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                GetVariable = Environment.GetEnvironmentVariable,
            };
        }

        return new ResolverEnvironment
        {
            Os = TargetOs.Linux,
            Home = home,
            UserName = Environment.UserName,
            GetVariable = Environment.GetEnvironmentVariable,
            ReadMounts = ReadProcMounts,
            FlatpakSystemInstallation = "/var/lib/flatpak",
        };
    }

    private static string? ReadProcMounts()
    {
        try
        {
            return File.ReadAllText("/proc/mounts");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Values set per device and per app, for what cannot be found by looking (design 5.2). Where
/// they are stored is decided with "app add" (plan P2.5); the resolver only takes them.
/// </summary>
/// <param name="InstallDir">The folder a portable app was unpacked into: {install_dir}.</param>
/// <param name="SdCard">Overrides the SD card found from the mount table: {sd_card}.</param>
/// <param name="WinePrefix">
/// The Wine prefix (the folder holding drive_c) for a winpath on Linux. When set it wins over
/// whatever the definition's prefix says.
/// </param>
public sealed record DeviceValues(string? InstallDir = null, string? SdCard = null, string? WinePrefix = null)
{
    public static DeviceValues None { get; } = new();
}
