using System.Text.Json;
using SaveTether.Cli;
using SaveTether.Core.Definitions;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Planning;
using SaveTether.Core.Resolution;
using SaveTether.Core.Scheduling;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Cli;

/// <summary>What one run of the command line gave back.</summary>
internal sealed record CliRun(int Exit, string Out, string Err)
{
    /// <summary>Standard output parsed as the one JSON object a --json run prints.</summary>
    public JsonElement Json()
    {
        using var document = JsonDocument.Parse(Out);
        return document.RootElement.Clone();
    }

    /// <summary>The first (usually only) entry of "operations".</summary>
    public JsonElement Operation() => Json().GetProperty("operations")[0];
}

/// <summary>
/// One simulated device: its own local folder, its own copy of the sync root, and an app's save
/// folder, all inside a shared sandbox. The command line is run in-process against them.
/// </summary>
internal sealed class CliDevice
{
    public const string MappingId = "ppsspp.savedata";

    public CliDevice(Sandbox sandbox, string name)
    {
        Name = name;
        LocalFolder = sandbox.At(name, "local");
        SyncRoot = sandbox.Dir(name, "sync");
        AppFolder = sandbox.Dir(name, "apps", "ppsspp");
        Source = Path.Combine(AppFolder, "SAVEDATA");
    }

    public string Name { get; }

    public string LocalFolder { get; }

    public string SyncRoot { get; }

    public string AppFolder { get; }

    /// <summary>The app's save folder for <see cref="MappingId"/>. Its parent exists; it does not, until a save is written.</summary>
    public string Source { get; }

    public string Target => Path.Combine(SyncRoot, "savetether", "ppsspp", "SAVEDATA");

    public string BackupRoot => Path.Combine(LocalFolder, "backups");

    /// <summary>Process names reported as running.</summary>
    public List<string> Running { get; } = [];

    /// <summary>Set to act as a person at a terminal who types these lines.</summary>
    public TextReader? Typing { get; set; }

    public IFaultHook? Faults { get; set; }

    /// <summary>What the path resolver sees. Null uses a Linux-shaped home under this device's folder.</summary>
    public ResolverEnvironment? Resolver { get; set; }

    /// <summary>The Flatpak precheck. Null, the default, runs none.</summary>
    public FlatpakCheck? Flatpak { get; set; }

    /// <summary>Desktop notifications shown, as (title, body).</summary>
    public List<(string Title, string Body)> Notifications { get; } = [];

    /// <summary>System commands run by "schedule", as "file arg arg ...". None really runs.</summary>
    public RecordingRunner Commands { get; } = new();

    /// <summary>Run "schedule" as on Windows rather than Linux.</summary>
    public bool ScheduleAsWindows { get; set; }

    /// <summary>The systemd user unit folder "schedule" writes to, inside the sandbox.</summary>
    public string UnitFolder => Path.Combine(Path.GetDirectoryName(LocalFolder)!, "units");

    /// <summary>The binary the scheduled jobs would run.</summary>
    public string? Executable { get; set; } = "/opt/savetether/savetether";

    /// <summary>The home folder the default <see cref="Resolver"/> uses.</summary>
    public string Home => Path.Combine(Path.GetDirectoryName(LocalFolder)!, "home");

    public CliRun Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var environment = new CliEnvironment
        {
            LocalFolder = LocalFolder,
            Processes = new FakeProcesses(Running),
            Input = Typing,
            Interactive = Typing is not null,
            Faults = Faults,
            Flatpak = Flatpak,
            Notifier = new RecordingNotifier(Notifications),
            Schedule = new ScheduleContext(ScheduleAsWindows, UnitFolder, @"PC\owner", Commands),
            Executable = Executable,
            Resolver = Resolver ?? new ResolverEnvironment { Os = TargetOs.Linux, Home = Home, UserName = Name },
        };
        var exit = CliApp.Run(args, stdout, stderr, environment);
        return new CliRun(exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Runs and fails the test, showing the output, unless the exit code is the one expected.</summary>
    public CliRun Expect(int exit, params string[] args)
    {
        var run = Run(args);
        Assert.True(
            run.Exit == exit,
            $"'{string.Join(' ', args)}' on {Name} exited {run.Exit}, expected {exit}.\nstdout:\n{run.Out}\nstderr:\n{run.Err}");
        return run;
    }

    public CliRun Ok(params string[] args) => Expect(CliApp.ExitSuccess, args);

    /// <summary>init, then map add for the standard mapping.</summary>
    public void SetUp(bool createRoot = false)
    {
        Ok(createRoot
            ? ["init", "--sync-root", SyncRoot, "--device", Name, "--create-root"]
            : ["init", "--sync-root", SyncRoot, "--device", Name]);
        Ok("map", "add", MappingId, "--target", "ppsspp/SAVEDATA", "--source", Source, "--process", "PPSSPPSDL");
    }

    public void Save(string relative, string content) => Write(Source, relative, content);

    public Dictionary<string, string> LocalFiles() => FilesIn(Source);

    public Dictionary<string, string> SharedFiles() => FilesIn(Target);

    public bool IsLinked => SafeFs.Probe(Source) is EntryKind.Symlink or EntryKind.Junction;

    /// <summary>
    /// Stands in for the sync tool: copies this device's sync root over another's. Files only this
    /// device has are added and differing files are overwritten; nothing is deleted unless
    /// <paramref name="mirror"/> is set. With <paramref name="metadataOnly"/> only SaveTether's
    /// own files arrive, staging "the saves have not been delivered yet".
    /// </summary>
    public void DeliverTo(CliDevice other, bool metadataOnly = false, bool mirror = false)
    {
        const string meta = "savetether/.savetether/";
        bool Wanted(string relative) =>
            !relative.EndsWith(SafeFs.TemporarySuffix, StringComparison.Ordinal)
            && (!metadataOnly || relative.StartsWith(meta, StringComparison.Ordinal));

        var mine = SafeFs.EnumerateTree(SyncRoot).Where(e => Wanted(e.RelativePath)).ToList();
        foreach (var entry in mine)
        {
            var destination = Path.Combine(other.SyncRoot, entry.RelativePath);
            if (entry.Kind == EntryKind.Directory)
            {
                Directory.CreateDirectory(destination);
            }
            else if (entry.Kind == EntryKind.File)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(SyncRoot, entry.RelativePath), destination, overwrite: true);
            }
        }

        if (mirror)
        {
            var keep = mine.Select(e => e.RelativePath).ToHashSet(StringComparer.Ordinal);
            foreach (var extra in SafeFs.EnumerateTree(other.SyncRoot).Where(e => e.Kind == EntryKind.File && !keep.Contains(e.RelativePath)).ToList())
            {
                File.Delete(Path.Combine(other.SyncRoot, extra.RelativePath));
            }
        }
    }

    public static Dictionary<string, string> FilesIn(string root)
    {
        // Through a link when there is one: this is what the app would see.
        var real = SafeFs.Probe(root) is EntryKind.Symlink or EntryKind.Junction ? PathRules.ResolveAll(root) : root;
        return SafeFs.Probe(real) != EntryKind.Directory
            ? []
            : SafeFs.EnumerateTree(real)
                .Where(e => e.Kind == EntryKind.File)
                .ToDictionary(e => e.RelativePath, e => File.ReadAllText(Path.Combine(real, e.RelativePath)));
    }

    public static void Write(string root, string relative, string content)
    {
        var path = Path.Combine([root, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class FakeProcesses(List<string> names) : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => names;
    }
}

/// <summary>Records notifications instead of showing them.</summary>
internal sealed class RecordingNotifier(List<(string Title, string Body)> shown) : INotifier
{
    public bool Notify(string title, string body)
    {
        shown.Add((title, body));
        return true;
    }
}

/// <summary>Records system commands instead of running them. Each answers with <see cref="Answer"/>.</summary>
internal sealed class RecordingRunner : ICommandRunner
{
    public List<string> Calls { get; } = [];

    /// <summary>The reply to a command line, by its start; anything else succeeds with no output.</summary>
    public Func<string, (int ExitCode, string Output)> Answer { get; set; } = _ => (0, string.Empty);

    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> arguments)
    {
        var line = string.Join(" ", [file, .. arguments]);
        Calls.Add(line);
        return Answer(line);
    }
}
