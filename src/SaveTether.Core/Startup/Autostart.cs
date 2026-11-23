using System.Text;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Startup;

/// <summary>
/// Where the desktop keeps the entry that starts the app with the session (desktop-app-spec.md, section 11).
/// The app writes and removes it; the CLI's status and the app only read it, to warn when it points to a
/// binary that is gone. Tests supply their own.
/// </summary>
public interface IAutostartStore
{
    /// <summary>Where the entry is, for messages: a file path, or the registry value's name.</summary>
    string Location { get; }

    /// <summary>The program the entry starts, or null when there is no entry or it is not SaveTether's form.</summary>
    string? ReadProgram();

    /// <summary>Writes the entry, replacing one that is there, to start <paramref name="program"/> minimized.</summary>
    void Write(string program);

    /// <summary>Removes the entry. None there is not an error.</summary>
    void Remove();
}

/// <summary>What the autostart entry says, as status and the app's banner B5 need it.</summary>
/// <param name="Program">The program the entry starts, or null when there is no entry.</param>
public sealed record AutostartState(string? Program)
{
    public bool Enabled => Program is not null;

    /// <summary>The entry's program when it is set and the file is not there.</summary>
    public string? MissingProgram => Program is { } path && !File.Exists(path) ? path : null;
}

/// <summary>The entry's text and the stores for each system.</summary>
public static class Autostart
{
    /// <summary>The argument the entry passes: start with the tray icon only.</summary>
    public const string MinimizedFlag = "--minimized";

    /// <summary>The store for this machine: the XDG autostart folder on Linux, the Run key on Windows; null elsewhere.</summary>
    public static IAutostartStore? ForSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRunKeyStore();
        }

        if (OperatingSystem.IsLinux())
        {
            var configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var configHome = string.IsNullOrEmpty(configured) || !Path.IsPathFullyQualified(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : configured;
            return new XdgAutostartStore(Path.Combine(configHome, "autostart"));
        }

        return null;
    }

    /// <summary>Reads the entry. An entry that cannot be read counts as none: status only warns about it.</summary>
    public static AutostartState Check(IAutostartStore? store)
    {
        try
        {
            return new AutostartState(store?.ReadProgram());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new AutostartState(null);
        }
    }

    /// <summary>The Linux entry: an XDG desktop entry whose Exec line quotes the program.</summary>
    public static string DesktopEntry(string program)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        return
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=SaveTether\n" +
            "Comment=Checks that game save folders stay linked to the synced folder\n" +
            $"Exec={QuoteExec(program)} {MinimizedFlag}\n" +
            "Terminal=false\n" +
            "X-GNOME-Autostart-enabled=true\n";
    }

    /// <summary>The Windows Run value: the quoted program and the flag.</summary>
    public static string RunValue(string program)
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        return $"\"{program}\" {MinimizedFlag}";
    }

    /// <summary>The program in a desktop entry's Exec line, undoing <see cref="QuoteExec"/>; null when there is none.</summary>
    public static string? ProgramInDesktopEntry(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var exec = text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        if (exec is null)
        {
            return null;
        }

        // The desktop entry format unescapes the value as a string first (\\ to \), then splits it into
        // arguments, where a quoted one has \" \` \$ \\ escaped (freedesktop.org Desktop Entry Specification, Exec key).
        var value = UnescapeString(exec["Exec=".Length..]);
        if (value.StartsWith('"'))
        {
            var program = new StringBuilder();
            for (var i = 1; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    program.Append(value[++i]);
                }
                else if (value[i] == '%' && i + 1 < value.Length && value[i + 1] == '%')
                {
                    program.Append('%');
                    i++;
                }
                else if (value[i] == '"')
                {
                    return program.ToString();
                }
                else
                {
                    program.Append(value[i]);
                }
            }

            return null;
        }

        var space = value.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? value : value[..space];
    }

    /// <summary>The program in a Run value: the quoted first part, or up to the first space.</summary>
    public static string? ProgramInRunValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end < 0 ? null : value[1..end];
        }

        var space = value.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? value : value[..space];
    }

    /// <summary>The program as one quoted Exec argument, then escaped again as a desktop entry string.</summary>
    internal static string QuoteExec(string program)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in program)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
            if (c == '%')
            {
                quoted.Append('%'); // A literal percent sign is %%, since %f and the like are field codes.
            }
        }

        quoted.Append('"');
        return quoted.ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }

    private static string UnescapeString(string value)
    {
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                result.Append(next switch { 's' => ' ', 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
            }
            else
            {
                result.Append(value[i]);
            }
        }

        return result.ToString();
    }
}

/// <summary>Linux: <c>savetether.desktop</c> in the XDG autostart folder.</summary>
public sealed class XdgAutostartStore(string autostartFolder) : IAutostartStore
{
    public const string FileName = "savetether.desktop";

    public string Location => Path.Combine(autostartFolder, FileName);

    public string? ReadProgram() =>
        SafeFs.Probe(Location) == EntryKind.File ? Autostart.ProgramInDesktopEntry(File.ReadAllText(Location)) : null;

    public void Write(string program)
    {
        Directory.CreateDirectory(autostartFolder);
        SafeFs.WriteFileAtomically(Location, Autostart.DesktopEntry(program));
    }

    public void Remove() => new SafeFs().RemoveFile(Location);
}

/// <summary>Windows: the value <c>SaveTether</c> under the current user's Run key.</summary>
/// <param name="keyPath">The key under HKEY_CURRENT_USER. Tests give one of their own, so they never touch the real Run key.</param>
public sealed class WindowsRunKeyStore(string keyPath = WindowsRunKeyStore.RunKeyPath, string valueName = WindowsRunKeyStore.DefaultValueName)
    : IAutostartStore
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string DefaultValueName = "SaveTether";

    public string Location => $@"HKCU\{keyPath}\{valueName}";

    public string? ReadProgram()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) is string value ? Autostart.ProgramInRunValue(value) : null;
    }

    public void Write(string program)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The Run key is on Windows only.");
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, Autostart.RunValue(program));
    }

    public void Remove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
