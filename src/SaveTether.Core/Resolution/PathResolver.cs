using SaveTether.Core.Definitions;

namespace SaveTether.Core.Resolution;

/// <summary>
/// Turns the paths in a definition into absolute paths on this device (design 5.2, plan P2.4).
/// It only computes paths and reads the few files a token is found from; it never creates,
/// changes or deletes anything.
/// </summary>
/// <remarks>
/// <para>
/// A path template is either absolute, or one token followed by "/" and more parts:
/// "{xdg_data}/Cemu/mlc01". The parts use forward slashes and may not be empty, ".", ".." or hold
/// another token. A token has the form {name} or {name:argument}.
/// </para>
/// <para>
/// A winpath is a Windows path. On Windows it is resolved directly. On Linux it is resolved inside
/// a Wine prefix, so {locallow} becomes drive_c/users/&lt;user&gt;/AppData/LocalLow in that prefix.
/// </para>
/// </remarks>
public sealed class PathResolver
{
    /// <summary>Folder under a Wine prefix's drive_c/users that is not anybody's profile.</summary>
    private const string WinePublicUser = "Public";

    /// <summary>The Windows user Proton creates in every prefix.</summary>
    private const string ProtonUser = "steamuser";

    private readonly ResolverEnvironment _env;

    public PathResolver(ResolverEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _env = environment;
    }

    /// <summary>The tokens a plain (not winpath) template may start with.</summary>
    public static IReadOnlyList<string> PlainTokens { get; } =
    [
        "home", "appdata", "localappdata", "locallow", "documents", "xdg_config", "xdg_data",
        "flatpak", "steam_compatdata", "sd_card", "emudeck", "install_dir",
    ];

    /// <summary>The tokens a winpath may start with. Each names a Windows folder of the user.</summary>
    public static IReadOnlyList<string> WindowsTokens { get; } = ["home", "appdata", "localappdata", "locallow", "documents"];

    /// <summary>Resolves a plain path template, as used by "dir", "file" and a plain "base".</summary>
    public ResolveResult Resolve(string template, DeviceValues values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        if (!TryParse(template, out var parsed, out var error))
        {
            return error;
        }

        if (parsed.Token is null)
        {
            return Join(parsed.Absolute!, parsed.Parts);
        }

        var root = ResolveToken(parsed.Token, parsed.Argument, values);
        return root.IsResolved ? Join(root.Path!, parsed.Parts) : root;
    }

    /// <summary>Resolves an install's base: a plain path, or a winpath with its prefix.</summary>
    public ResolveResult ResolveBase(BaseLocation location, DeviceValues values)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(values);

        if (location.Path is not null)
        {
            return Resolve(location.Path, values);
        }

        if (location.WinPath is null)
        {
            return ResolveResult.Fail(ResolveProblem.Invalid, "The base has neither a path nor a winpath.");
        }

        return ResolveWinPath(location.WinPath, location.Prefix, values);
    }

    /// <summary>
    /// Resolves the folder a role names for one install: the install's base, then the role's path
    /// for that install. Refuses an install that is not for this operating system.
    /// </summary>
    public ResolveResult ResolveRole(InstallDefinition install, RoleDefinition role, DeviceValues values)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(values);

        if (!install.Os.Contains(_env.Os))
        {
            return ResolveResult.Fail(
                ResolveProblem.NotOnThisOs,
                $"Install '{install.Id}' is not for {OsName(_env.Os)}.");
        }

        if (!role.Paths.TryGetValue(install.Id, out var relative))
        {
            return ResolveResult.Fail(
                ResolveProblem.Invalid,
                $"Role '{role.Id}' has no path for install '{install.Id}'.");
        }

        var root = ResolveBase(install.Base, values);
        if (!root.IsResolved)
        {
            return root;
        }

        return SplitRelative(relative, out var parts, out var error) ? Join(root.Path!, parts) : error;
    }

    /// <summary>Resolves a Windows path: directly on Windows, inside a Wine prefix on Linux.</summary>
    public ResolveResult ResolveWinPath(string winPath, PrefixRule? prefix, DeviceValues values)
    {
        ArgumentNullException.ThrowIfNull(winPath);
        ArgumentNullException.ThrowIfNull(values);

        if (!TryParseWindows(winPath, out var parsed, out var error))
        {
            return error;
        }

        if (_env.Os == TargetOs.Windows)
        {
            if (parsed.Token is null)
            {
                return Join(parsed.Absolute!, parsed.Parts);
            }

            var native = ResolveWindowsFolder(parsed.Token);
            return native.IsResolved ? Join(native.Path!, parsed.Parts) : native;
        }

        // Only drive C: is known to exist inside every Wine prefix; on Windows any drive works.
        if (parsed.Token is null && !parsed.Absolute!.StartsWith('C'))
        {
            return ResolveResult.Fail(
                ResolveProblem.NotYetSupported,
                $"The winpath '{winPath}' is on drive {parsed.Absolute[..2]}; only C: is looked up inside a Wine prefix.");
        }

        var prefixRoot = ResolvePrefix(prefix, values);
        if (!prefixRoot.IsResolved)
        {
            return prefixRoot;
        }

        var driveC = Path.Combine(prefixRoot.Path!, "drive_c");
        if (parsed.Token is null)
        {
            return Join(driveC, parsed.Parts);
        }

        var user = FindWineUser(driveC);
        if (!user.IsResolved)
        {
            return user;
        }

        var profile = user.Path!;
        var folder = parsed.Token switch
        {
            "home" => profile,
            "appdata" => Path.Combine(profile, "AppData", "Roaming"),
            "localappdata" => Path.Combine(profile, "AppData", "Local"),
            "locallow" => Path.Combine(profile, "AppData", "LocalLow"),

            // Older Wine and Proton prefixes call it "My Documents"; use that only when it is the one there.
            "documents" => !Directory.Exists(Path.Combine(profile, "Documents")) && Directory.Exists(Path.Combine(profile, "My Documents"))
                ? Path.Combine(profile, "My Documents")
                : Path.Combine(profile, "Documents"),
            _ => throw new InvalidOperationException($"Unhandled Windows token '{parsed.Token}'."),
        };
        return Join(folder, parsed.Parts);
    }

    private ResolveResult ResolveToken(string name, string? argument, DeviceValues values)
    {
        switch (name)
        {
            case "home":
                return ResolveResult.Ok(_env.Home);

            case "appdata" or "localappdata" or "locallow" or "documents":
                return _env.Os == TargetOs.Windows
                    ? ResolveWindowsFolder(name)
                    : ResolveResult.Fail(
                        ResolveProblem.NotOnThisOs,
                        $"{{{name}}} is a Windows folder. On Linux use it inside a winpath, which looks it up in a Wine prefix.");

            case "xdg_config":
                return LinuxOnly(name) ?? ResolveResult.Ok(Xdg("XDG_CONFIG_HOME", ".config"));

            case "xdg_data":
                return LinuxOnly(name) ?? ResolveResult.Ok(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")));

            case "flatpak":
                return LinuxOnly(name) ?? ResolveResult.Ok(Path.Combine(_env.Home, ".var", "app", argument!));

            case "sd_card":
                return LinuxOnly(name) ?? ResolveSdCard(values);

            case "emudeck":
                return LinuxOnly(name) ?? ResolveEmuDeck();

            case "steam_compatdata":
                return LinuxOnly(name) ?? ResolveResult.Fail(
                    ResolveProblem.NotYetSupported,
                    "{steam_compatdata:...} is not resolved by this version yet.");

            case "install_dir":
                return values.InstallDir is { Length: > 0 } dir
                    ? RequireAbsolute(dir, "The install folder set for this device")
                    : ResolveResult.Fail(
                        ResolveProblem.NeedsValue,
                        "{install_dir} needs the folder this app is installed in to be set for this device.");

            default:
                throw new InvalidOperationException($"Unhandled token '{name}'.");
        }
    }

    private ResolveResult ResolveWindowsFolder(string name)
    {
        var folder = name switch
        {
            "home" => _env.Home,
            "appdata" => _env.AppData,
            "localappdata" => _env.LocalAppData,
            "locallow" => _env.LocalLow,
            "documents" => _env.Documents,
            _ => throw new InvalidOperationException($"Unhandled Windows token '{name}'."),
        };

        return folder is { Length: > 0 }
            ? ResolveResult.Ok(folder)
            : ResolveResult.Fail(ResolveProblem.Missing, $"Windows did not report a folder for {{{name}}}.");
    }

    private ResolveResult? LinuxOnly(string name) =>
        _env.Os == TargetOs.Linux
            ? null
            : ResolveResult.Fail(ResolveProblem.NotOnThisOs, $"{{{name}}} exists only on Linux.");

    /// <summary>An XDG base folder: the variable when it holds an absolute path, else the default under home (XDG spec).</summary>
    private string Xdg(string variable, string fallbackUnderHome) =>
        _env.GetVariable(variable) is { Length: > 0 } value && value.StartsWith('/')
            ? value
            : Path.Combine(_env.Home, fallbackUnderHome);

    private ResolveResult ResolveSdCard(DeviceValues values)
    {
        if (values.SdCard is { Length: > 0 } set)
        {
            return RequireAbsolute(set, "The SD card folder set for this device");
        }

        var mounts = _env.ReadMounts();
        if (mounts is null)
        {
            return ResolveResult.Fail(ResolveProblem.Missing, "The mount table could not be read, so the SD card cannot be found.");
        }

        var cards = MountTable.FindSdCards(mounts);
        return cards.Count switch
        {
            0 => ResolveResult.Fail(ResolveProblem.Missing, "No SD card is mounted."),
            1 => ResolveResult.Ok(cards[0]),
            _ => ResolveResult.Fail(
                ResolveProblem.Ambiguous,
                $"More than one SD card is mounted ({string.Join(", ", cards)}). Set the one to use for this device."),
        };
    }

    private ResolveResult ResolveEmuDeck()
    {
        var settings = Path.Combine(_env.Home, ".config", "EmuDeck", "settings.sh");
        string text;
        try
        {
            text = File.ReadAllText(settings);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return ResolveResult.Fail(ResolveProblem.Missing, $"EmuDeck's settings file was not found at {settings}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ResolveResult.Fail(ResolveProblem.Missing, $"EmuDeck's settings file at {settings} could not be read: {e.Message}");
        }

        var value = ShellSettings.Find(text, "emulationPath", _env.Home);
        if (value is null)
        {
            return ResolveResult.Fail(ResolveProblem.Missing, $"EmuDeck's settings file at {settings} does not set emulationPath.");
        }

        return RequireAbsolute(value.TrimEnd('/'), $"emulationPath in {settings}");
    }

    private ResolveResult ResolvePrefix(PrefixRule? prefix, DeviceValues values)
    {
        ResolveResult root;
        if (values.WinePrefix is { Length: > 0 } set)
        {
            root = RequireAbsolute(set, "The Wine prefix set for this device");
        }
        else if (prefix?.Path is { } template)
        {
            root = Resolve(template, values);
        }
        else if (prefix is null)
        {
            return ResolveResult.Fail(
                ResolveProblem.NeedsValue,
                "A Windows path on Linux needs a Wine prefix: the definition names none and none is set for this device.");
        }
        else
        {
            var kind = prefix.Steam is not null ? "steam" : prefix.SteamShortcut is not null ? "steam_shortcut" : "lutris";
            return ResolveResult.Fail(
                ResolveProblem.NotYetSupported,
                $"A \"{kind}\" prefix is not resolved by this version yet. Set the Wine prefix for this device instead.");
        }

        if (!root.IsResolved)
        {
            return root;
        }

        // Steam keeps the prefix in compatdata/<id>/pfx; accept the compatdata folder too.
        if (!Directory.Exists(Path.Combine(root.Path!, "drive_c")) && Directory.Exists(Path.Combine(root.Path!, "pfx", "drive_c")))
        {
            return ResolveResult.Ok(Path.Combine(root.Path!, "pfx"));
        }

        return Directory.Exists(Path.Combine(root.Path!, "drive_c"))
            ? root
            : ResolveResult.Fail(ResolveProblem.Missing, $"{root.Path} is not a Wine prefix: it has no drive_c folder.");
    }

    /// <summary>The profile folder inside a prefix: the only one there, else the login name's, else Proton's.</summary>
    private ResolveResult FindWineUser(string driveC)
    {
        var users = Path.Combine(driveC, "users");
        var names = Directory.Exists(users)
            ? Directory.EnumerateDirectories(users)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !string.Equals(name, WinePublicUser, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

        if (names.Count == 1)
        {
            return ResolveResult.Ok(Path.Combine(users, names[0]));
        }

        foreach (var preferred in new[] { _env.UserName, ProtonUser })
        {
            if (preferred is { Length: > 0 } && names.Contains(preferred, StringComparer.Ordinal))
            {
                return ResolveResult.Ok(Path.Combine(users, preferred));
            }
        }

        return names.Count == 0
            ? ResolveResult.Fail(ResolveProblem.Missing, $"The Wine prefix has no user folder under {users}.")
            : ResolveResult.Fail(
                ResolveProblem.Ambiguous,
                $"The Wine prefix has more than one user folder under {users} ({string.Join(", ", names)}).");
    }

    /// <summary>
    /// Accepts a value only when it is absolute, in the form of the environment's operating system
    /// or of the one actually running (the two differ only in tests).
    /// </summary>
    private ResolveResult RequireAbsolute(string path, string what) =>
        (_env.Os == TargetOs.Windows ? IsWindowsAbsolute(path) : path.StartsWith('/')) || Path.IsPathFullyQualified(path)
            ? ResolveResult.Ok(path)
            : ResolveResult.Fail(ResolveProblem.Invalid, $"{what} must be an absolute path, but is '{path}'.");

    private static ResolveResult Join(string root, IReadOnlyList<string> parts) =>
        ResolveResult.Ok(parts.Count == 0 ? root : Path.Combine([root, .. parts]));

    private static string OsName(TargetOs os) => os == TargetOs.Windows ? "Windows" : "Linux";

    private sealed record Parsed(string? Token, string? Argument, string? Absolute, IReadOnlyList<string> Parts);

    private static bool TryParse(string template, out Parsed parsed, out ResolveResult error)
    {
        parsed = null!;
        if (!template.StartsWith('{'))
        {
            // An absolute path with no token, in this platform's own form.
            if (!template.StartsWith('/') && !IsDrivePath(template))
            {
                error = ResolveResult.Fail(
                    ResolveProblem.Invalid,
                    $"'{template}' must start with a token such as {{home}} or be an absolute path.");
                return false;
            }

            var (absoluteRoot, rest) = template.StartsWith('/') ? ("/", template[1..]) : (template[..2] + Path.DirectorySeparatorChar, template[3..]);
            if (!SplitParts(template, rest, out var absParts, out error))
            {
                return false;
            }

            parsed = new Parsed(null, null, absoluteRoot, absParts);
            return true;
        }

        if (!TrySplitToken(template, out var name, out var argument, out var remainder, out error))
        {
            return false;
        }

        if (!PlainTokens.Contains(name))
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}' uses an unknown token {{{name}}}.");
            return false;
        }

        var takesArgument = name is "flatpak" or "steam_compatdata";
        if (!CheckArgument(template, name, argument, takesArgument, out error))
        {
            return false;
        }

        if (!SplitParts(template, remainder, out var parts, out error))
        {
            return false;
        }

        parsed = new Parsed(name, argument, null, parts);
        return true;
    }

    private static bool TryParseWindows(string winPath, out Parsed parsed, out ResolveResult error)
    {
        parsed = null!;
        if (!winPath.StartsWith('{'))
        {
            if (!IsDrivePath(winPath))
            {
                error = ResolveResult.Fail(
                    ResolveProblem.Invalid,
                    $"The winpath '{winPath}' must start with a token such as {{locallow}} or a drive, such as C:/.");
                return false;
            }

            if (!SplitParts(winPath, winPath[3..], out var driveParts, out error))
            {
                return false;
            }

            parsed = new Parsed(null, null, winPath[..2].ToUpperInvariant() + "\\", driveParts);
            return true;
        }

        if (!TrySplitToken(winPath, out var name, out var argument, out var remainder, out error))
        {
            return false;
        }

        if (!WindowsTokens.Contains(name))
        {
            error = ResolveResult.Fail(
                ResolveProblem.Invalid,
                $"The winpath '{winPath}' uses {{{name}}}; a winpath may only start with {string.Join(", ", WindowsTokens.Select(t => $"{{{t}}}"))}.");
            return false;
        }

        if (!CheckArgument(winPath, name, argument, takesArgument: false, out error))
        {
            return false;
        }

        if (!SplitParts(winPath, remainder, out var parts, out error))
        {
            return false;
        }

        parsed = new Parsed(name, null, null, parts);
        return true;
    }

    private static bool TrySplitToken(string template, out string name, out string? argument, out string remainder, out ResolveResult error)
    {
        name = string.Empty;
        argument = null;
        remainder = string.Empty;

        var close = template.IndexOf('}', StringComparison.Ordinal);
        if (close < 0)
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}' has a token with no closing '}}'.");
            return false;
        }

        var inside = template[1..close];
        var colon = inside.IndexOf(':', StringComparison.Ordinal);
        name = colon < 0 ? inside : inside[..colon];
        argument = colon < 0 ? null : inside[(colon + 1)..];
        remainder = template[(close + 1)..];

        if (remainder.Length > 0 && remainder[0] != '/')
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}' must have a '/' after its token.");
            return false;
        }

        remainder = remainder.Length > 0 ? remainder[1..] : remainder;
        if (remainder.Length == 0 && template.Length > close + 1)
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}' ends with a '/'.");
            return false;
        }

        error = null!;
        return true;
    }

    private static bool CheckArgument(string template, string name, string? argument, bool takesArgument, out ResolveResult error)
    {
        if (takesArgument && string.IsNullOrEmpty(argument))
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}': {{{name}:...}} needs an argument after the colon.");
            return false;
        }

        if (!takesArgument && argument is not null)
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}': {{{name}}} takes no argument.");
            return false;
        }

        if (argument is not null && (argument.IndexOfAny(['/', '\\', '{', '}']) >= 0 || argument is "." or ".."))
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}': '{argument}' is not a valid argument for {{{name}}}.");
            return false;
        }

        if (name == "steam_compatdata" && !ulong.TryParse(argument, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            error = ResolveResult.Fail(ResolveProblem.Invalid, $"'{template}': {{steam_compatdata:...}} needs a numeric Steam app ID.");
            return false;
        }

        error = null!;
        return true;
    }

    private static bool SplitRelative(string relative, out IReadOnlyList<string> parts, out ResolveResult error) =>
        SplitParts(relative, relative, out parts, out error);

    private static bool SplitParts(string whole, string rest, out IReadOnlyList<string> parts, out ResolveResult error)
    {
        parts = rest.Length == 0 ? [] : rest.Split('/');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.IndexOfAny(['\\', '{', '}']) >= 0)
            {
                error = ResolveResult.Fail(
                    ResolveProblem.Invalid,
                    $"'{whole}' has a part '{part}' that is not allowed: parts are separated by single '/' and may not be '.', '..' or hold '\\', '{{' or '}}'.");
                return false;
            }
        }

        error = null!;
        return true;
    }

    private static bool IsWindowsAbsolute(string text) =>
        (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '/' or '\\')
        || text.StartsWith(@"\\", StringComparison.Ordinal);

    private static bool IsDrivePath(string text) =>
        text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '/';
}
