namespace SaveTether.Core.Resolution;

/// <summary>
/// Finds a Lutris game's Wine prefix from its game config file, &lt;slug&gt;-&lt;number&gt;.yml in
/// Lutris's games folder, where the prefix is the "prefix" key of the "game" section. Reads only.
/// </summary>
/// <remarks>
/// The files are YAML. Only the shape Lutris writes is read, a top-level "game:" line followed by
/// indented "key: value" lines, so no YAML library is needed. Anything else is ignored.
/// </remarks>
internal sealed class LutrisGames
{
    private readonly string _home;
    private readonly string _dataHome;

    public LutrisGames(string home, string dataHome)
    {
        _home = home;
        _dataHome = dataHome;
    }

    /// <summary>
    /// Folders Lutris keeps game configs in: current and older, native and Flatpak. The Deck's
    /// Flatpak Lutris (2026-11-17) uses ~/.var/app/net.lutris.Lutris/config/lutris/games.
    /// </summary>
    private IEnumerable<string> GameFolders() =>
    [
        Path.Combine(_dataHome, "lutris", "games"),
        Path.Combine(_home, ".config", "lutris", "games"),
        Path.Combine(_home, ".var", "app", "net.lutris.Lutris", "data", "lutris", "games"),
        Path.Combine(_home, ".var", "app", "net.lutris.Lutris", "config", "lutris", "games"),
    ];

    public ResolveResult ResolvePrefix(string slug)
    {
        var files = GameFolders()
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.yml"))
            .Where(file => IsConfigFor(Path.GetFileNameWithoutExtension(file), slug))
            .Order(StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            return ResolveResult.Fail(ResolveProblem.Missing, $"No Lutris game config was found for \"{slug}\".");
        }

        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ResolveResult.Fail(ResolveProblem.Missing, $"{file} could not be read: {e.Message}");
            }

            if (FindGamePrefix(text, _home) is { } prefix)
            {
                prefixes.Add(prefix.TrimEnd('/'));
            }
        }

        return prefixes.Count switch
        {
            1 => ResolveResult.Ok(prefixes.Min!),
            0 => ResolveResult.Fail(
                ResolveProblem.Missing,
                $"The Lutris config for \"{slug}\" does not set a Wine prefix ({string.Join(", ", files)}). Set the prefix for this device instead."),
            _ => ResolveResult.Fail(
                ResolveProblem.Ambiguous,
                $"Lutris has more than one config for \"{slug}\" with different prefixes ({string.Join(", ", prefixes)})."),
        };
    }

    /// <summary>True for "&lt;slug&gt;-&lt;digits&gt;", the name Lutris gives a game's config file.</summary>
    private static bool IsConfigFor(string name, string slug) =>
        name.Length > slug.Length + 1
        && name.StartsWith(slug + "-", StringComparison.Ordinal)
        && name[(slug.Length + 1)..].All(char.IsAsciiDigit);

    /// <summary>The value of "prefix" directly inside the top-level "game" section, with quotes removed and ~ expanded.</summary>
    internal static string? FindGamePrefix(string yaml, string home)
    {
        var inGame = false;
        int? indent = null;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var lineIndent = line.Length - line.TrimStart().Length;
            if (lineIndent == 0)
            {
                inGame = line.TrimEnd() == "game:";
                indent = null;
                continue;
            }

            if (!inGame)
            {
                continue;
            }

            indent ??= lineIndent;
            if (lineIndent != indent)
            {
                continue;
            }

            var content = line.Trim();
            if (!content.StartsWith("prefix:", StringComparison.Ordinal))
            {
                continue;
            }

            var value = content["prefix:".Length..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
            {
                value = home.TrimEnd('/') + value[1..];
            }

            return value.Length > 0 ? value : null;
        }

        return null;
    }
}
