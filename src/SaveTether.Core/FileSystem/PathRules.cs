namespace SaveTether.Core.FileSystem;

/// <summary>Path comparison and containment rules, per operating system.</summary>
public static class PathRules
{
    private const int MaxLinkHops = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    /// <summary>How names compare on this platform: case is ignored on Windows only.</summary>
    public static StringComparison NameComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Absolute form with no trailing separator (a filesystem root keeps its own). Purely lexical.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>Lexical equality of two paths under this platform's case rule.</summary>
    public static bool AreSame(string a, string b) => string.Equals(Normalize(a), Normalize(b), NameComparison);

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or lies below it. Purely lexical:
    /// pass paths through <see cref="ResolveParents"/> or <see cref="ResolveAll"/> first when links matter.
    /// </summary>
    public static bool IsSameOrInside(string path, string root)
    {
        var candidate = Normalize(path);
        var container = Normalize(root);
        if (string.Equals(candidate, container, NameComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(container) ? container : container + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, NameComparison);
    }

    /// <summary>
    /// Resolves every link in the folders leading to <paramref name="path"/>, but not the final
    /// entry itself. This is where an operation on the path would really land.
    /// </summary>
    public static string ResolveParents(string path) => Resolve(path, resolveFinal: false);

    /// <summary>Resolves every link in <paramref name="path"/>, including the final entry.</summary>
    public static string ResolveAll(string path) => Resolve(path, resolveFinal: true);

    /// <summary>Groups of names that differ only by letter case. Such names collide on Windows.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> FindCaseOnlyCollisions(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names
            .Distinct(StringComparer.Ordinal)
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => (IReadOnlyList<string>)[.. group.Order(StringComparer.Ordinal)])
            .ToList();
    }

    private static string Resolve(string path, bool resolveFinal)
    {
        var full = Normalize(path);
        var current = Path.GetPathRoot(full) ?? throw new SafeFsException($"Not an absolute path: {path}");
        var pending = new Stack<string>(SplitBelowRoot(full, current).Reverse());
        var hops = 0;

        while (pending.Count > 0)
        {
            var candidate = Path.Combine(current, pending.Pop());
            var isFinal = pending.Count == 0;

            if ((!isFinal || resolveFinal) && SafeFs.Probe(candidate) is EntryKind.Symlink or EntryKind.Junction)
            {
                if (++hops > MaxLinkHops)
                {
                    throw new SafeFsException($"Too many links while resolving '{path}'. There may be a link cycle.");
                }

                // Continue from the link's target, with the rest of the original path still to come.
                var target = SafeFs.ReadLinkTarget(candidate);
                current = Path.GetPathRoot(target) ?? throw new SafeFsException($"Link target is not absolute: {target}");
                foreach (var part in SplitBelowRoot(target, current).Reverse())
                {
                    pending.Push(part);
                }

                continue;
            }

            current = candidate;
        }

        return current;
    }

    private static string[] SplitBelowRoot(string fullPath, string root) =>
        fullPath[root.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
