using SaveTether.Core;

namespace SaveTether.Cli;

/// <summary>
/// Entry point that tests can call in-process. Milestone M0 only: version and help.
/// The real argument parser and commands arrive in M6.
/// </summary>
internal static class CliApp
{
    public const int ExitSuccess = 0;

    /// <summary>
    /// Provisional code for a command line the tool does not understand.
    /// Design 5.12 defines 0 to 3 for operations; usage errors are settled in M6.
    /// </summary>
    public const int ExitUsage = 64;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            stdout.WriteLine(Usage);
            return ExitSuccess;
        }

        if (args is ["--version"])
        {
            stdout.WriteLine($"savetether {ProductInfo.Version}");
            return ExitSuccess;
        }

        stderr.WriteLine($"savetether: unknown command or option '{args[0]}'");
        stderr.WriteLine("Run 'savetether --help' for usage.");
        return ExitUsage;
    }

    private const string Usage = """
        SaveTether: redirect save folders into a synced folder.

        Usage:
          savetether --version    Print the version
          savetether --help       Print this help

        No commands yet. This is an early development build.
        """;
}
