namespace SaveTether.Core.Model;

/// <summary>Rules for the names SaveTether uses as file and folder names.</summary>
public static class Identifiers
{
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
