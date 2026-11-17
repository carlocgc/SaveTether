using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using SaveTether.Core.FileSystem;

namespace SaveTether.Core.Scheduling;

/// <summary>What "schedule install" sets up.</summary>
/// <param name="Executable">The SaveTether binary the scheduled jobs run, as an absolute path.</param>
/// <param name="Snapshots">Also take a daily snapshot. Off unless asked for.</param>
/// <param name="Keep">Snapshots kept per mapping.</param>
/// <param name="SaveTetherHome">SAVETETHER_HOME to pass on, when it is set for this run. Linux only; see <see cref="Schedule.WindowsTask"/>.</param>
public sealed record ScheduleRequest(string Executable, bool Snapshots, int Keep, string? SaveTetherHome);

/// <summary>One file a schedule writes, with its full content.</summary>
public sealed record ScheduleFile(string Path, string Content);

/// <summary>What is installed now, as far as SaveTether can tell.</summary>
/// <param name="Jobs">The installed jobs, by name ("status", "snapshot").</param>
/// <param name="Where">Where they live: the unit folder, or the Task Scheduler folder.</param>
public sealed record ScheduleState(IReadOnlyList<string> Jobs, string Where);

/// <summary>Runs a system command. Tests supply their own.</summary>
public interface ICommandRunner
{
    /// <summary>Runs <paramref name="file"/> and returns its exit code and standard output (stderr appended).</summary>
    (int ExitCode, string Output) Run(string file, IReadOnlyList<string> arguments);
}

/// <summary>Runs commands with a time limit.</summary>
public sealed class SystemCommandRunner : ICommandRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException($"Could not start '{file}'.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, $"'{file}' did not finish within {Timeout.TotalSeconds} seconds.");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, $"Could not run '{file}': {e.Message}");
        }
    }
}

/// <summary>
/// The opt-in scheduled jobs (plan P2.7, design 5.5): "status" shortly after login, which notifies
/// only when something is wrong, and, when asked for, a daily snapshot. Linux uses systemd user
/// units, which need no root; Windows uses Task Scheduler tasks for the current user.
/// </summary>
public static class Schedule
{
    public const string StatusJob = "status";
    public const string SnapshotJob = "snapshot";

    private const string UnitPrefix = "savetether-";
    private const string TaskFolder = @"\SaveTether\";
    private const string Header = "# Written by 'savetether schedule install'. Remove with 'savetether schedule remove'.";

    /// <summary>The arguments each job runs SaveTether with.</summary>
    public static IReadOnlyList<string> ArgumentsOf(string job, int keep) => job == StatusJob
        ? ["status", "--notify", "--log"]
        : ["snapshot", "--keep", keep.ToString(CultureInfo.InvariantCulture), "--notify", "--log"];

    private static IEnumerable<string> JobsOf(ScheduleRequest request) =>
        request.Snapshots ? [StatusJob, SnapshotJob] : [StatusJob];

    // ---- Linux: systemd user units ----

    /// <summary>The systemd user unit folder: $XDG_CONFIG_HOME/systemd/user, else ~/.config/systemd/user.</summary>
    public static string UnitFolder(string home, Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var config = getVariable("XDG_CONFIG_HOME") is { Length: > 0 } value && value.StartsWith('/') ? value : Path.Combine(home, ".config");
        return Path.Combine(config, "systemd", "user");
    }

    /// <summary>The unit files for <paramref name="request"/>: a service and a timer per job.</summary>
    public static IReadOnlyList<ScheduleFile> SystemdUnits(ScheduleRequest request, string unitFolder)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = new List<ScheduleFile>();
        foreach (var job in JobsOf(request))
        {
            var description = job == StatusJob ? "SaveTether: check the save links after login" : "SaveTether: daily snapshot of the shared save folders";
            var service = new StringBuilder()
                .AppendLine(Header)
                .AppendLine("[Unit]")
                .AppendLine($"Description={description}")
                .AppendLine()
                .AppendLine("[Service]")
                .AppendLine("Type=oneshot");
            if (request.SaveTetherHome is { } home)
            {
                service.AppendLine($"Environment={SystemdQuote("SAVETETHER_HOME=" + home)}");
            }

            service.AppendLine($"ExecStart={string.Join(" ", new[] { request.Executable }.Concat(ArgumentsOf(job, request.Keep)).Select(SystemdQuote))}");

            var timer = new StringBuilder()
                .AppendLine(Header)
                .AppendLine("[Unit]")
                .AppendLine($"Description={description}")
                .AppendLine()
                .AppendLine("[Timer]");
            if (job == StatusJob)
            {
                // Two minutes after the user's session starts, so a sync root on an SD card or a
                // network drive has had time to mount.
                timer.AppendLine("OnStartupSec=2min");
            }
            else
            {
                timer.AppendLine("OnCalendar=daily").AppendLine("Persistent=true").AppendLine("RandomizedDelaySec=15min");
            }

            timer.AppendLine().AppendLine("[Install]").AppendLine("WantedBy=timers.target");

            files.Add(new ScheduleFile(Path.Combine(unitFolder, $"{UnitPrefix}{job}.service"), Lf(service)));
            files.Add(new ScheduleFile(Path.Combine(unitFolder, $"{UnitPrefix}{job}.timer"), Lf(timer)));
        }

        return files;
    }

    /// <summary>The timer unit names for jobs, as systemctl takes them.</summary>
    public static string TimerOf(string job) => $"{UnitPrefix}{job}.timer";

    /// <summary>Double quotes for systemd, which also needs '%' doubled and '\' and '"' escaped.</summary>
    internal static string SystemdQuote(string value)
    {
        if (value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+' or ',' or '='))
        {
            return value;
        }

        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";
    }

    private static string Lf(StringBuilder text) => text.ToString().ReplaceLineEndings("\n");

    // ---- Windows: Task Scheduler ----

    /// <summary>The Task Scheduler name of a job, in SaveTether's own folder.</summary>
    public static string TaskNameOf(string job) => TaskFolder + (job == StatusJob ? "Status" : "Snapshot");

    /// <summary>
    /// The task definition for one job, in Task Scheduler's XML format. It runs as the current user,
    /// only while logged on, with least privilege, and through "conhost --headless" so no console
    /// window flashes up.
    /// </summary>
    public static string WindowsTask(ScheduleRequest request, string job, string userId)
    {
        ArgumentNullException.ThrowIfNull(request);
        var arguments = string.Join(" ", ArgumentsOf(job, request.Keep).Select(WindowsQuote));
        // SAVETETHER_HOME is not passed on: a task sees the user's own environment variables, so it
        // works when the variable is set for the account, which "schedule install" points out.
        var actionArguments = $"--headless {WindowsQuote(request.Executable)} {arguments}";

        var trigger = job == StatusJob
            ? $"""
                  <LogonTrigger>
                    <Enabled>true</Enabled>
                    <UserId>{Xml(userId)}</UserId>
                    <Delay>PT2M</Delay>
                  </LogonTrigger>
              """
            : """
                  <CalendarTrigger>
                    <StartBoundary>2026-01-01T12:00:00</StartBoundary>
                    <Enabled>true</Enabled>
                    <RandomDelay>PT15M</RandomDelay>
                    <ScheduleByDay>
                      <DaysInterval>1</DaysInterval>
                    </ScheduleByDay>
                  </CalendarTrigger>
              """;
        var description = job == StatusJob
            ? "SaveTether: check the save links after login. Remove with 'savetether schedule remove'."
            : "SaveTether: daily snapshot of the shared save folders. Remove with 'savetether schedule remove'.";

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{Xml(description)}</Description>
              </RegistrationInfo>
              <Triggers>
            {trigger}
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Xml(userId)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>conhost.exe</Command>
                  <Arguments>{Xml(actionArguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        return xml.ReplaceLineEndings("\r\n") + "\r\n";
    }

    private static string Xml(string text) => SecurityElement.Escape(text) ?? string.Empty;

    /// <summary>Quotes an argument for a Windows command line when it holds a space or a quote.</summary>
    internal static string WindowsQuote(string value) =>
        value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"') ? value : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    // ---- Installing ----

    /// <summary>
    /// Installs the jobs for this operating system and removes a job no longer asked for (the
    /// snapshot job when --snapshots is left out). Returns what was done, line by line.
    /// </summary>
    public static IReadOnlyList<string> Install(ScheduleRequest request, ScheduleContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return context.IsWindows ? InstallWindows(request, context) : InstallSystemd(request, context);
    }

    /// <summary>Removes every SaveTether job. Returns what was done, line by line.</summary>
    public static IReadOnlyList<string> Remove(ScheduleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var done = new List<string>();
        foreach (var job in Installed(context).Jobs)
        {
            done.AddRange(context.IsWindows ? RemoveWindowsJob(job, context) : RemoveSystemdJob(job, context));
        }

        if (!context.IsWindows && done.Count > 0)
        {
            Systemctl(context, "daemon-reload");
        }

        if (context.IsWindows && done.Count > 0 && RemoveEmptyTaskFolder(context))
        {
            done.Add($"Removed the empty Task Scheduler folder {TaskFolder.TrimEnd('\\')}");
        }

        return done.Count == 0 ? ["Nothing was scheduled."] : done;
    }

    /// <summary>Which jobs are installed now.</summary>
    public static ScheduleState Installed(ScheduleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsWindows)
        {
            var jobs = new[] { StatusJob, SnapshotJob }
                .Where(job => context.Commands.Run("schtasks.exe", ["/Query", "/TN", TaskNameOf(job)]).ExitCode == 0)
                .ToList();
            return new ScheduleState(jobs, "Task Scheduler, folder " + TaskFolder.TrimEnd('\\'));
        }

        var folder = context.UnitFolder;
        return new ScheduleState(
            [.. new[] { StatusJob, SnapshotJob }.Where(job => File.Exists(Path.Combine(folder, TimerOf(job))))],
            folder);
    }

    private static List<string> InstallSystemd(ScheduleRequest request, ScheduleContext context)
    {
        var done = new List<string>();
        Directory.CreateDirectory(context.UnitFolder);
        foreach (var file in SystemdUnits(request, context.UnitFolder))
        {
            File.WriteAllText(file.Path, file.Content);
            done.Add($"Wrote {file.Path}");
        }

        if (!request.Snapshots && File.Exists(Path.Combine(context.UnitFolder, TimerOf(SnapshotJob))))
        {
            done.AddRange(RemoveSystemdJob(SnapshotJob, context));
        }

        Systemctl(context, "daemon-reload");
        foreach (var job in JobsOf(request))
        {
            Systemctl(context, "enable", "--now", TimerOf(job));
            done.Add($"Enabled {TimerOf(job)}");
        }

        return done;
    }

    private static List<string> RemoveSystemdJob(string job, ScheduleContext context)
    {
        // Disabling a timer that is not enabled is harmless; its failure is not a reason to keep the files.
        context.Commands.Run("systemctl", ["--user", "disable", "--now", TimerOf(job)]);
        var done = new List<string> { $"Disabled {TimerOf(job)}" };
        foreach (var name in new[] { $"{UnitPrefix}{job}.timer", $"{UnitPrefix}{job}.service" })
        {
            var path = Path.Combine(context.UnitFolder, name);
            if (File.Exists(path))
            {
                new SafeFs().DeleteTree(path);
                done.Add($"Removed {path}");
            }
        }

        return done;
    }

    private static void Systemctl(ScheduleContext context, params string[] arguments)
    {
        var (exit, output) = context.Commands.Run("systemctl", ["--user", .. arguments]);
        if (exit != 0)
        {
            throw new IOException($"'systemctl --user {string.Join(" ", arguments)}' failed: {output.Trim()}");
        }
    }

    private static List<string> InstallWindows(ScheduleRequest request, ScheduleContext context)
    {
        var done = new List<string>();
        foreach (var job in JobsOf(request))
        {
            var file = Path.Combine(Path.GetTempPath(), $"savetether-{job}-{Guid.NewGuid():N}.xml");
            try
            {
                // schtasks reads task XML as UTF-16, as its header says.
                File.WriteAllText(file, WindowsTask(request, job, context.UserId), Encoding.Unicode);
                var (exit, output) = context.Commands.Run("schtasks.exe", ["/Create", "/TN", TaskNameOf(job), "/XML", file, "/F"]);
                if (exit != 0)
                {
                    throw new IOException($"Task Scheduler refused the task '{TaskNameOf(job)}': {output.Trim()}");
                }
            }
            finally
            {
                new SafeFs().DeleteTree(file);
            }

            done.Add($"Created the task {TaskNameOf(job)}");
        }

        if (!request.Snapshots && Installed(context).Jobs.Contains(SnapshotJob))
        {
            done.AddRange(RemoveWindowsJob(SnapshotJob, context));
        }

        return done;
    }

    /// <summary>
    /// schtasks deletes tasks but not the folder they were in. Removes SaveTether's folder through
    /// Task Scheduler's own scripting interface, only when it holds no task. Best effort.
    /// </summary>
    private static bool RemoveEmptyTaskFolder(ScheduleContext context)
    {
        const string Script =
            "$s=New-Object -ComObject Schedule.Service;$s.Connect();" +
            "try{$f=$s.GetFolder('\\SaveTether')}catch{exit 1};" +
            "if($f.GetTasks(1).Count -eq 0 -and $f.GetFolders(0).Count -eq 0){$s.GetFolder('\\').DeleteFolder('SaveTether',0);exit 0};exit 1";
        return context.Commands.Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", Script]).ExitCode == 0;
    }

    private static List<string> RemoveWindowsJob(string job, ScheduleContext context)
    {
        var (exit, output) = context.Commands.Run("schtasks.exe", ["/Delete", "/TN", TaskNameOf(job), "/F"]);
        return exit == 0
            ? [$"Deleted the task {TaskNameOf(job)}"]
            : throw new IOException($"Task Scheduler did not delete '{TaskNameOf(job)}': {output.Trim()}");
    }
}

/// <summary>Where and how the schedule is installed on this machine. Tests supply their own.</summary>
/// <param name="UnitFolder">Linux: the systemd user unit folder.</param>
/// <param name="UserId">Windows: the user the tasks run as, "DOMAIN\name".</param>
public sealed record ScheduleContext(bool IsWindows, string UnitFolder, string UserId, ICommandRunner Commands)
{
    public static ScheduleContext ForSystem() => new(
        OperatingSystem.IsWindows(),
        Schedule.UnitFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable),
        $@"{Environment.UserDomainName}\{Environment.UserName}",
        new SystemCommandRunner());
}
