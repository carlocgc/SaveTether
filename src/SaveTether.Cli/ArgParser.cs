namespace SaveTether.Cli;

/// <summary>The command line is not one SaveTether understands. Nothing was done.</summary>
internal sealed class UsageException : Exception
{
    public UsageException()
    {
    }

    public UsageException(string message)
        : base(message)
    {
    }

    public UsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What one command accepts.</summary>
/// <param name="Name">One or two words, for example "apply" or "backup restore".</param>
/// <param name="Flags">Options that take no value.</param>
/// <param name="Options">Options that take one value and may be given once.</param>
/// <param name="Repeatable">Options that take one value and may be given several times.</param>
internal sealed record CommandSpec(
    string Name, int MinPositionals, int MaxPositionals, string[] Flags, string[] Options, string[] Repeatable);

/// <summary>A command line, taken apart.</summary>
internal sealed record ParsedCommand(
    string Name,
    IReadOnlyList<string> Positionals,
    IReadOnlySet<string> Flags,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Options)
{
    public string? FirstPositional => Positionals.Count > 0 ? Positionals[0] : null;

    public bool Has(string flag) => Flags.Contains(flag);

    /// <summary>The value of an option given at most once, or null.</summary>
    public string? Value(string option) => Options.TryGetValue(option, out var values) ? values[0] : null;

    public IReadOnlyList<string> Values(string option) => Options.TryGetValue(option, out var values) ? values : [];
}

/// <summary>
/// Hand-written argument parsing (implementation plan, section 1: no dependency).
/// Form: <c>command [subcommand] [positionals] [--flag] [--option value | --option=value] [-- positionals]</c>.
/// Options may come before or after positionals. Everything after <c>--</c> is a positional, so a
/// path that starts with a dash can still be given.
/// </summary>
internal static class ArgParser
{
    public const string Json = "--json";
    public const string DryRun = "--dry-run";
    public const string Help = "--help";

    private static readonly string[] None = [];

    /// <summary>Every command. <c>--json</c> and <c>--help</c> are accepted by all of them.</summary>
    public static IReadOnlyList<CommandSpec> Commands { get; } =
    [
        new("init", 0, 0, ["--create-root", "--take-over"], ["--sync-root", "--device", "--backup-root"], None),
        new("map add", 1, 1, None, ["--target", "--source", "--entry-depth"], ["--process"]),
        new("map remove", 1, 1, None, None, None),
        new("status", 0, 1, None, None, None),
        new(
            "apply", 0, 1,
            [
                "--all", "--copy", "--no-copy", "--add-missing", "--replace-shared", "--confirm-replace",
                "--assume-synced", "--assume-closed", DryRun,
            ],
            None, None),
        new("revert", 1, 1, ["--assume-closed", DryRun], None, None),
        new("backup list", 0, 1, None, None, None),
        new("backup restore", 1, 1, ["--confirm-replace", "--assume-closed", DryRun], None, None),
        new("recover", 0, 0, None, None, None),
    ];

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            throw new UsageException("no command given");
        }

        var spec = FindCommand(args, out var consumed);
        var positionals = new List<string>();
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var onlyPositionals = false;

        for (var i = consumed; i < args.Count; i++)
        {
            var arg = args[i];
            if (onlyPositionals || !arg.StartsWith('-') || arg == "-")
            {
                positionals.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyPositionals = true;
                continue;
            }

            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? arg : arg[..equals];
            var inlineValue = equals < 0 ? null : arg[(equals + 1)..];

            if (name is Json or Help || spec.Flags.Contains(name, StringComparer.Ordinal))
            {
                if (inlineValue is not null)
                {
                    throw new UsageException($"option '{name}' does not take a value");
                }

                flags.Add(name);
                continue;
            }

            var repeatable = spec.Repeatable.Contains(name, StringComparer.Ordinal);
            if (!repeatable && !spec.Options.Contains(name, StringComparer.Ordinal))
            {
                throw new UsageException($"unknown option '{name}' for '{spec.Name}'");
            }

            string value;
            if (inlineValue is not null)
            {
                value = inlineValue;
            }
            else if (i + 1 < args.Count && args[i + 1] != "--")
            {
                value = args[++i];
            }
            else
            {
                throw new UsageException($"option '{name}' needs a value");
            }

            if (value.Length == 0)
            {
                throw new UsageException($"option '{name}' needs a value that is not empty");
            }

            if (!options.TryGetValue(name, out var values))
            {
                options[name] = values = [];
            }
            else if (!repeatable)
            {
                throw new UsageException($"option '{name}' was given more than once");
            }

            values.Add(value);
        }

        // With --help the rest of the line does not have to be complete.
        if (!flags.Contains(Help))
        {
            if (positionals.Count < spec.MinPositionals)
            {
                throw new UsageException($"'{spec.Name}' needs {(spec.MaxPositionals == 1 ? "an argument" : "more arguments")}");
            }

            if (positionals.Count > spec.MaxPositionals)
            {
                throw new UsageException($"unexpected argument '{positionals[spec.MaxPositionals]}' for '{spec.Name}'");
            }
        }

        return new ParsedCommand(
            spec.Name, positionals, flags,
            options.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal));
    }

    private static CommandSpec FindCommand(IReadOnlyList<string> args, out int consumed)
    {
        var first = args[0];
        if (args.Count > 1 && Commands.FirstOrDefault(c => c.Name == $"{first} {args[1]}") is { } twoWords)
        {
            consumed = 2;
            return twoWords;
        }

        if (Commands.FirstOrDefault(c => c.Name == first) is { } oneWord)
        {
            consumed = 1;
            return oneWord;
        }

        var subcommands = Commands
            .Where(c => c.Name.StartsWith(first + " ", StringComparison.Ordinal))
            .Select(c => c.Name[(first.Length + 1)..])
            .ToList();
        if (subcommands.Count > 0)
        {
            throw new UsageException(
                args.Count > 1 && !args[1].StartsWith('-')
                    ? $"unknown command '{first} {args[1]}'; '{first}' takes: {string.Join(", ", subcommands)}"
                    : $"'{first}' needs a subcommand: {string.Join(", ", subcommands)}");
        }

        throw new UsageException($"unknown command or option '{first}'");
    }
}
