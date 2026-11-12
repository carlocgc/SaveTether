using SaveTether.Core.Planning;

namespace SaveTether.Cli;

/// <summary>What the commands need from the machine. Tests supply their own.</summary>
internal sealed class CliEnvironment
{
    /// <summary>Overrides where this device's local files are kept.</summary>
    public const string HomeVariable = "SAVETETHER_HOME";

    /// <summary>This device's own folder: bootstrap, state, journal, and by default the backups.</summary>
    public required string LocalFolder { get; init; }

    public IProcessLister Processes { get; init; } = new SystemProcessLister();

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Where answers to prompts are read from. Only used when <see cref="Interactive"/>.</summary>
    public TextReader? Input { get; init; }

    /// <summary>True when a person is at a terminal. Without it, and always with --json, nothing is ever asked.</summary>
    public bool Interactive { get; init; }

    /// <summary>Test seam for fault injection; passed through to the executor.</summary>
    public SaveTether.Core.Execution.IFaultHook? Faults { get; init; }

    public string DefaultBackupRoot => Path.Combine(LocalFolder, "backups");

    public static CliEnvironment FromSystem() => new()
    {
        LocalFolder = FindLocalFolder(),
        Input = Console.In,
        Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected,
    };

    private static string FindLocalFolder()
    {
        if (Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home)
        {
            return Path.GetFullPath(home);
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaveTether");
        }

        var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(data, "savetether");
    }
}
