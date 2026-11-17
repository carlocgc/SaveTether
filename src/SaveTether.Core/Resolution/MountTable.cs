using System.Text;

namespace SaveTether.Core.Resolution;

/// <summary>Reads the Linux mount table (/proc/mounts) to find removable SD cards.</summary>
internal static class MountTable
{
    /// <summary>
    /// Mount points of SD cards: an mmcblk device mounted for the desktop user under /run/media or
    /// /media, which is where SteamOS and desktop Linux put removable media. Sorted, no duplicates.
    /// </summary>
    public static IReadOnlyList<string> FindSdCards(string mounts)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in mounts.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !fields[0].StartsWith("/dev/mmcblk", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Unescape(fields[1]);
            if (target.StartsWith("/run/media/", StringComparison.Ordinal) || target.StartsWith("/media/", StringComparison.Ordinal))
            {
                found.Add(target);
            }
        }

        return [.. found];
    }

    /// <summary>The kernel writes space, tab, newline and backslash in mount paths as \ and three octal digits.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
        {
            return field;
        }

        var text = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length && IsOctal(field.AsSpan(i + 1, 3)))
            {
                text.Append((char)Convert.ToInt32(field.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                text.Append(field[i]);
            }
        }

        return text.ToString();
    }

    private static bool IsOctal(ReadOnlySpan<char> digits)
    {
        foreach (var c in digits)
        {
            if (c is < '0' or > '7')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Reads simple NAME=value lines from a shell settings file, such as EmuDeck's settings.sh.</summary>
internal static class ShellSettings
{
    /// <summary>
    /// The value of the last assignment to <paramref name="name"/>, with quotes removed and a
    /// leading ~, $HOME or ${HOME} replaced by <paramref name="home"/>. Null when it is not set.
    /// Anything more complicated than a plain assignment is not interpreted.
    /// </summary>
    public static string? Find(string text, string name, string home)
    {
        string? value = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            if (!line.StartsWith(name + "=", StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = line[(name.Length + 1)..].Trim();
            if (candidate.Length >= 2 && (candidate[0] == '"' || candidate[0] == '\'') && candidate[^1] == candidate[0])
            {
                candidate = candidate[1..^1];
            }

            value = ExpandHome(candidate, home);
        }

        return value is { Length: > 0 } ? value : null;
    }

    private static string ExpandHome(string value, string home)
    {
        foreach (var prefix in new[] { "${HOME}", "$HOME", "~" })
        {
            if (value == prefix)
            {
                return home;
            }

            if (value.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return home.TrimEnd('/') + value[prefix.Length..];
            }
        }

        return value;
    }
}
