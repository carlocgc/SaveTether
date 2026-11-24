using SaveTether.Core.FileSystem;

namespace SaveTether.Gui.Presentation;

/// <summary>
/// Settings' "Desktop notifications" switch (desktop-app-spec.md, section 11): on unless turned off. Kept in the
/// local folder, as "Not now" to the install offer is. Off silences SaveTether's notifications only; the tray icon
/// still shows the state.
/// </summary>
public sealed class NotificationSetting(string localFolder)
{
    public const string FileName = "notifications.txt";

    private const string Off = "off";

    private string File => Path.Combine(localFolder, FileName);

    /// <summary>On unless the file says off. A file that cannot be read (locked mid-write, say) counts as on, the default.</summary>
    public bool IsOn
    {
        get
        {
            try
            {
                return SafeFs.Probe(File) != EntryKind.File || System.IO.File.ReadAllText(File).Trim() != Off;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    /// <summary>The switch turned on or off. Answers why it failed, or null.</summary>
    public string? Set(bool on)
    {
        try
        {
            Directory.CreateDirectory(localFolder);
            SafeFs.WriteFileAtomically(File, (on ? "on" : Off) + "\n");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SafeFsException)
        {
            return $"The setting could not be saved in {File}: {e.Message}";
        }
    }
}
