using System.Text.Json;
using SaveTether.Cli;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Planning;
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
