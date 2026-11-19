using System.Runtime.InteropServices;

namespace SaveTether.Core.Startup;

/// <summary>
/// "Make the savetether command available in terminals" on Windows (desktop-app-spec.md, section 11, decision 10):
/// the install folder added to the user's own <c>Path</c> in <c>HKCU\Environment</c>. Opt-in, since it changes the
/// user's environment for every program. The value keeps its registry type, so a <c>%VAR%</c> in it still expands.
/// </summary>
public static partial class UserPath
{
    public const string EnvironmentKey = "Environment";

    /// <summary>
    /// The <c>Path</c> value with <paramref name="folder"/> added at the end, or null when it is there already (in any
    /// letter case, with or without a trailing backslash). Empty entries between others are kept; trailing semicolons are not.
    /// </summary>
    public static string? WithFolder(string? current, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var wanted = folder.TrimEnd('\\');
        var entries = (current ?? string.Empty).Split(';');
        if (entries.Any(e => string.Equals(e.Trim().TrimEnd('\\'), wanted, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return string.IsNullOrEmpty(current) ? wanted : current.TrimEnd(';') + ";" + wanted;
    }

    /// <summary>
    /// Adds the folder to the user's <c>Path</c> under <paramref name="keyPath"/> (tests pass a key of their own) and
    /// tells running programs the environment changed. False when it was there already. Windows only.
    /// </summary>
    public static bool Add(string folder, string keyPath = EnvironmentKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The user's Path is changed this way on Windows only.");
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
        // Read without expanding, so %VAR% entries are written back as they were.
        var current = key.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var kind = current is null ? Microsoft.Win32.RegistryValueKind.ExpandString : key.GetValueKind("Path");
        if (WithFolder(current, folder) is not { } updated)
        {
            return false;
        }

        key.SetValue("Path", updated, kind is Microsoft.Win32.RegistryValueKind.String ? kind : Microsoft.Win32.RegistryValueKind.ExpandString);
        if (keyPath == EnvironmentKey)
        {
            // Explorer re-reads the environment on this message, so terminals started from it see the new Path.
            _ = SendMessageTimeoutW(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
        }

        return true;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeoutW(
        IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeoutMs, out IntPtr result);
}
