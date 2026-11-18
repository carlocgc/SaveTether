using SaveTether.Core.Planning;

namespace SaveTether.Cli;

/// <summary>What the commands need from the machine. Tests supply their own.</summary>
internal sealed class CliEnvironment
{
    /// <summary>Overrides where this device's local files are kept.</summary>
    public const string HomeVariable = SaveTether.Core.Storage.LocalStore.HomeVariable;

    /// <summary>This device's own folder: bootstrap, state, journal, and by default the backups.</summary>
    public required string LocalFolder { get; init; }

    public IProcessLister Processes { get; init; } = new SystemProcessLister();

    /// <summary>The Flatpak precheck for apply. Null where there are no Flatpaks.</summary>
    public FlatpakCheck? Flatpak { get; init; } = FlatpakCheck.ForSystem();

    /// <summary>What the path resolver sees of this machine: home, special folders, mounts.</summary>
    public SaveTether.Core.Resolution.ResolverEnvironment Resolver { get; init; } = SaveTether.Core.Resolution.ResolverEnvironment.FromSystem();

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
        LocalFolder = SaveTether.Core.Storage.LocalStore.DefaultFolder(),
        Input = Console.In,
        Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected,
    };
}
