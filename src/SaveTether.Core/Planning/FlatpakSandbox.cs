using System.Diagnostics;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Planning;

/// <summary>Reads a Flatpak app's permissions, as "flatpak info --show-permissions &lt;id&gt;" prints them.</summary>
public interface IFlatpakInfo
{
    /// <summary>The permissions text, or null when they could not be read (no flatpak command, app not installed).</summary>
    string? ShowPermissions(string appId);
}

/// <summary>Runs the flatpak command. Reads only.</summary>
public sealed class SystemFlatpakInfo : IFlatpakInfo
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public string? ShowPermissions(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        var start = new ProcessStartInfo("flatpak")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("info");
        start.ArgumentList.Add("--show-permissions");
        start.ArgumentList.Add(appId);

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// What a Flatpak app's sandbox lets it see of the shared folder. A link inside the app's own
/// folder (~/.var/app/&lt;id&gt;/) is followed inside the sandbox, so it only works when the sandbox
/// can write where it points (design 2.1, "Flatpak sandboxes").
/// </summary>
/// <param name="AppId">The Flatpak app whose folder holds the save folder.</param>
/// <param name="CanReach">True or false when the permissions were read; null when they could not be.</param>
/// <param name="Unreachable">The paths the sandbox cannot write, when <paramref name="CanReach"/> is false.</param>
/// <param name="FixCommand">The command that grants access, when <paramref name="CanReach"/> is false.</param>
public sealed record SandboxReach(string AppId, bool? CanReach, IReadOnlyList<string> Unreachable, string? FixCommand);

/// <summary>
/// The Flatpak precheck (plan P2.6): for a save folder inside a Flatpak app's folder, reads the
/// app's filesystem permissions and works out whether it can write the shared folder.
/// </summary>
public sealed class FlatpakCheck
{
    private readonly IFlatpakInfo _info;
    private readonly string _home;
    private readonly Func<string, string?> _getVariable;

    public FlatpakCheck(IFlatpakInfo info, string home, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(getVariable);
        _info = info;
        _home = home;
        _getVariable = getVariable;
    }

    /// <summary>The check for this machine: on Linux it runs the flatpak command; elsewhere there are no Flatpaks.</summary>
    public static FlatpakCheck? ForSystem() => OperatingSystem.IsLinux()
        ? new FlatpakCheck(new SystemFlatpakInfo(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable)
        : null;

    /// <summary>The Flatpak app whose folder (~/.var/app/&lt;id&gt;) holds <paramref name="source"/>, or null.</summary>
    public string? AppOwning(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var apps = Path.Combine(_home, ".var", "app");
        if (!PathRules.IsSameOrInside(source, apps) || PathRules.AreSame(source, apps))
        {
            return null;
        }

        var relative = Path.GetRelativePath(PathRules.Normalize(apps), PathRules.Normalize(source));
        var id = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return id.Length > 0 && id != ".." ? id : null;
    }

    /// <summary>
    /// Null when the save folder is not inside a Flatpak app's folder. Otherwise whether the sandbox
    /// can write the shared folder, both as the link names it (<paramref name="target"/>) and where
    /// links in that path lead (<paramref name="resolvedTarget"/>). The fix grants the savetether
    /// folder in the same form, so one command covers every mapping of that app.
    /// </summary>
    public SandboxReach? Check(string source, string target, string resolvedTarget, string dataRoot, string resolvedDataRoot)
    {
        if (AppOwning(source) is not { } appId)
        {
            return null;
        }

        var text = _info.ShowPermissions(appId);
        if (text is null)
        {
            return new SandboxReach(appId, null, [], null);
        }

        var needed = new List<(string Target, string Grant)> { (target, dataRoot) };
        if (!PathRules.AreSame(target, resolvedTarget))
        {
            needed.Add((resolvedTarget, resolvedDataRoot));
        }

        var filesystems = FlatpakPermissions.ParseFilesystems(text);
        var missing = needed.Where(n => !FlatpakPermissions.CanWrite(filesystems, n.Target, _home, _getVariable)).ToList();
        if (missing.Count == 0)
        {
            return new SandboxReach(appId, true, [], null);
        }

        var command = "flatpak override --user " +
            string.Join(" ", missing.Select(m => "--filesystem=" + Quote(m.Grant))) + " " + appId;
        return new SandboxReach(appId, false, [.. missing.Select(m => m.Target)], command);
    }

    /// <summary>Single quotes for the shell when the path holds anything but plain characters.</summary>
    internal static string Quote(string path) =>
        path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or '@' or '%' or '=')
            ? path
            : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>Flatpak's filesystem permission rules, as far as SaveTether needs them.</summary>
internal static class FlatpakPermissions
{
    /// <summary>Host folders that "host" does not expose: the sandbox has its own.</summary>
    private static readonly string[] HostReserved =
        ["/app", "/bin", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/proc", "/sbin", "/sys", "/usr", "/tmp", "/run/flatpak", "/run/host"];

    /// <summary>
    /// The "filesystems" entries of the [Context] group, in order, with negations applied: a later
    /// "!entry" removes the same entry, as Flatpak merges overrides. Value: true when read-only.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> ParseFilesystems(string keyFile)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var inContext = false;
        foreach (var raw in keyFile.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inContext = line == "[Context]";
                continue;
            }

            if (!inContext || !line.StartsWith("filesystems=", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var item in line["filesystems=".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var negated = item.StartsWith('!');
                var (entry, readOnly) = SplitMode(negated ? item[1..] : item);
                if (negated)
                {
                    result.Remove(entry);
                }
                else
                {
                    result[entry] = readOnly;
                }
            }
        }

        return result;
    }

    /// <summary>True when some entry gives write access to <paramref name="path"/>.</summary>
    public static bool CanWrite(IReadOnlyDictionary<string, bool> filesystems, string path, string home, Func<string, string?> getVariable) =>
        filesystems.Any(pair => !pair.Value && Covers(pair.Key, path, home, getVariable));

    private static (string Entry, bool ReadOnly) SplitMode(string item)
    {
        foreach (var (suffix, readOnly) in new[] { (":ro", true), (":rw", false), (":create", false) })
        {
            if (item.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (item[..^suffix.Length], readOnly);
            }
        }

        return (item, false);
    }

    private static bool Covers(string entry, string path, string home, Func<string, string?> getVariable)
    {
        switch (entry)
        {
            case "host":
                return !HostReserved.Any(reserved => PathRules.IsSameOrInside(path, reserved));
            case "host-os" or "host-etc" or "host-root":
                return false;
            case "home" or "~":
                return PathRules.IsSameOrInside(path, home);
        }

        if (entry.StartsWith("~/", StringComparison.Ordinal))
        {
            return PathRules.IsSameOrInside(path, Path.Combine(home, entry[2..]));
        }

        // Flatpak paths start with '/'; the second test only matters when tests run on Windows.
        if (entry.StartsWith('/') || Path.IsPathFullyQualified(entry))
        {
            return PathRules.IsSameOrInside(path, entry);
        }

        if (entry.StartsWith("xdg-", StringComparison.Ordinal))
        {
            var slash = entry.IndexOf('/', StringComparison.Ordinal);
            var name = slash < 0 ? entry : entry[..slash];
            var rest = slash < 0 ? null : entry[(slash + 1)..];
            return XdgFolder(name, home, getVariable) is { } folder
                && PathRules.IsSameOrInside(path, rest is null ? folder : Path.Combine(folder, rest));
        }

        return false;
    }

    /// <summary>The host folder an xdg-* entry names; null for those that are not under the user's control (xdg-run).</summary>
    private static string? XdgFolder(string name, string home, Func<string, string?> getVariable)
    {
        string Base(string variable, string fallback) =>
            getVariable(variable) is { Length: > 0 } value && value.StartsWith('/') ? value : Path.Combine(home, fallback);

        return name switch
        {
            "xdg-config" => Base("XDG_CONFIG_HOME", ".config"),
            "xdg-data" => Base("XDG_DATA_HOME", Path.Combine(".local", "share")),
            "xdg-cache" => Base("XDG_CACHE_HOME", ".cache"),
            "xdg-desktop" => UserDir("DESKTOP", "Desktop", home, getVariable),
            "xdg-documents" => UserDir("DOCUMENTS", "Documents", home, getVariable),
            "xdg-download" => UserDir("DOWNLOAD", "Downloads", home, getVariable),
            "xdg-music" => UserDir("MUSIC", "Music", home, getVariable),
            "xdg-pictures" => UserDir("PICTURES", "Pictures", home, getVariable),
            "xdg-public-share" => UserDir("PUBLICSHARE", "Public", home, getVariable),
            "xdg-templates" => UserDir("TEMPLATES", "Templates", home, getVariable),
            "xdg-videos" => UserDir("VIDEOS", "Videos", home, getVariable),
            _ => null,
        };
    }

    /// <summary>A user folder from user-dirs.dirs (XDG_&lt;NAME&gt;_DIR="$HOME/..."), else its usual name under home.</summary>
    private static string UserDir(string name, string fallback, string home, Func<string, string?> getVariable)
    {
        var config = getVariable("XDG_CONFIG_HOME") is { Length: > 0 } value && value.StartsWith('/') ? value : Path.Combine(home, ".config");
        try
        {
            var file = Path.Combine(config, "user-dirs.dirs");
            if (File.Exists(file))
            {
                var key = $"XDG_{name}_DIR=";
                var line = File.ReadLines(file).Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));
                if (line is not null)
                {
                    var dir = line[key.Length..].Trim('"');
                    return dir.StartsWith("$HOME/", StringComparison.Ordinal) ? Path.Combine(home, dir["$HOME/".Length..])
                        : dir == "$HOME" ? home
                        : dir;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall back to the usual name.
        }

        return Path.Combine(home, fallback);
    }
}
