namespace SaveTether.Core.Model;

/// <summary>Rules for the names SaveTether uses as file and folder names.</summary>
public static class Identifiers
{
    private static readonly string[] WindowsReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// A mapping ID is "app.role": two or more dot-separated parts, each starting with a lowercase
    /// letter or digit and continuing with lowercase letters, digits, '-' or '_'. For example
    /// "ppsspp.savedata". It is used as a folder name, so nothing else is allowed.
    /// </summary>
    public static bool IsValidMappingId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 100)
        {
            return false;
        }

        var parts = id.Split('.');
        return parts.Length >= 2 && parts.All(IsValidPart);
    }

    /// <summary>The app part of a mapping ID: everything before the first dot.</summary>
    public static string AppOf(string mappingId) => mappingId[..mappingId.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>
    /// A device name is one part under the same rule as a mapping ID part, up to 40 characters.
    /// For example "deck" or "desktop-pc". It is used as a file name on every device.
    /// </summary>
    public static bool IsValidDeviceName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 40 && IsValidPart(name) && !IsWindowsReserved(name);

    /// <summary>
    /// Checks a mapping's target, the path of its shared folder below &lt;sync root&gt;/savetether/.
    /// Returns null when it is valid, or a sentence saying what is wrong.
    /// </summary>
    public static string? DescribeTargetProblem(string mappingId, string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return "The target is empty.";
        }

        if (target.Contains('\\', StringComparison.Ordinal))
        {
            return "The target must use '/' between its two parts, not '\\'.";
        }

        var parts = target.Split('/');
        if (parts.Length != 2)
        {
            return "The target must have exactly two parts, 'app/save folder'.";
        }

        if (!string.Equals(parts[0], AppOf(mappingId), StringComparison.Ordinal))
        {
            return $"The target's first part must be '{AppOf(mappingId)}', the app part of the mapping ID.";
        }

        return DescribeFolderNameProblem(parts[1]);
    }

    private static string? DescribeFolderNameProblem(string name)
    {
        if (name.Length == 0)
        {
            return "The target's save folder name is empty.";
        }

        if (name.Length > 100)
        {
            return "The target's save folder name is longer than 100 characters.";
        }

        if (name[0] == '.')
        {
            return "The target's save folder name must not start with '.' (that covers '.', '..' and the reserved '.savetether').";
        }

        if (name[^1] is '.' or ' ')
        {
            return "The target's save folder name must not end with '.' or a space; Windows cannot create such a folder.";
        }

        if (name.Any(c => c < ' ' || c is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            return "The target's save folder name contains a character Windows does not allow in folder names.";
        }

        if (IsWindowsReserved(name))
        {
            return $"'{name}' is a name Windows reserves and cannot be used as a folder name.";
        }

        return null;
    }

    private static bool IsWindowsReserved(string name)
    {
        var stem = name.Split('.')[0];
        return WindowsReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsValidPart(string part)
    {
        if (part.Length == 0 || !IsLowerLetterOrDigit(part[0]))
        {
            return false;
        }

        return part.All(c => IsLowerLetterOrDigit(c) || c is '-' or '_');
    }

    private static bool IsLowerLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
