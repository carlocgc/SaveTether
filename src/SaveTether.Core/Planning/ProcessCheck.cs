using System.Diagnostics;

namespace SaveTether.Core.Planning;

/// <summary>Lists the names of the processes running on this machine.</summary>
public interface IProcessLister
{
    IReadOnlyList<string> RunningProcessNames();
}

/// <summary>
/// The app-running check (design 5.12). It compares process names, so it is a heuristic: the
/// output always says which names were looked for, and the owner can override it.
/// </summary>
public static class ProcessCheck
{
    /// <summary>Linux keeps only the first 15 characters of a process name in /proc/&lt;pid&gt;/comm.</summary>
    private const int LinuxCommLength = 15;

    /// <summary>The configured names that match a running process.</summary>
    public static IReadOnlyList<string> FindRunning(IEnumerable<string> configuredNames, IReadOnlyList<string> running)
    {
        ArgumentNullException.ThrowIfNull(configuredNames);
        ArgumentNullException.ThrowIfNull(running);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return configuredNames.Where(name => running.Any(process => Matches(name, process, comparison))).ToList();
    }

    private static bool Matches(string configured, string running, StringComparison comparison)
    {
        var wanted = StripExe(configured.Trim());
        var actual = StripExe(running.Trim());
        if (wanted.Length == 0)
        {
            return false;
        }

        return string.Equals(wanted, actual, comparison)
            || (wanted.Length > LinuxCommLength
                && actual.Length == LinuxCommLength
                && wanted.StartsWith(actual, comparison));
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

/// <summary>Reads process names from the operating system.</summary>
public sealed class SystemProcessLister : IProcessLister
{
    public IReadOnlyList<string> RunningProcessNames() =>
        OperatingSystem.IsLinux() ? FromProc() : FromProcessApi();

    private static List<string> FromProcessApi()
    {
        var names = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    names.Add(process.ProcessName);
                }
                catch (InvalidOperationException)
                {
                    // The process exited while we were looking.
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Reads /proc directly. A Flatpak app's processes are visible here from an unsandboxed tool.
    /// Each process gives two names: the short "comm" name, and the file name of its executable.
    /// </summary>
    private static List<string> FromProc()
    {
        var names = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!Path.GetFileName(directory).All(char.IsAsciiDigit))
            {
                continue;
            }

            try
            {
                names.Add(File.ReadAllText(Path.Combine(directory, "comm")).Trim());
                var commandLine = File.ReadAllText(Path.Combine(directory, "cmdline"));
                var executable = commandLine.Split('\0', 2)[0];
                if (executable.Length > 0)
                {
                    names.Add(Path.GetFileName(executable));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The process exited, or belongs to another user.
            }
        }

        return names;
    }
}
