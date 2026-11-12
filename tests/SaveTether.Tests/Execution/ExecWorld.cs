using SaveTether.Core.Backup;
using SaveTether.Core.Execution;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;
using Xunit.Sdk;

namespace SaveTether.Tests.Execution;

/// <summary>
/// One device, for real: a sync root, an app's save folder, a backup folder and the local
/// settings folder, all in a sandbox with a canary beside it.
/// </summary>
internal sealed class ExecWorld : IDisposable
{
    public const string MappingId = "ppsspp.savedata";
    public const string Device = "deck";

    private static readonly string[] ProcessNames = ["PPSSPPSDL"];

    private readonly MappingFile _mapping = new()
    {
        Schema = MappingFile.CurrentSchema, Id = MappingId, Target = "ppsspp/SAVEDATA", Mechanism = Mechanism.Link,
    };

    /// <param name="syncRoot">Where the sync root goes; inside the sandbox when null.</param>
    public ExecWorld(string? syncRoot = null)
    {
        if (syncRoot is not null)
        {
            Directory.CreateDirectory(syncRoot);
        }

        Shared = new SharedStore(syncRoot ?? Sandbox.Dir("sync"));
        Shared.CreateRoot();
        Shared.WriteMapping(_mapping);
        Source = Path.Combine(Sandbox.Dir("apps", "ppsspp"), "SAVEDATA");
        BackupRoot = Sandbox.Dir("backups");
        Backups = new BackupStore(BackupRoot, Shared.SyncRoot);
        Local = new LocalStore(Sandbox.At("local"));
    }

    public Sandbox Sandbox { get; } = new();

    public SharedStore Shared { get; }

    public LocalStore Local { get; }

    public BackupStore Backups { get; }

    public string BackupRoot { get; }

    public string Source { get; }

    public string Target => Shared.TargetPathOf(_mapping);

    public string Aside => Source + Planner.AsideSuffix;

    public string Staging => Source + Planner.StagingSuffix;

    public Journal Journal => Journal.For(Local);

    public void LocalSave(string relative, string content) => Write(Source, relative, content);

    public void SharedSave(string relative, string content) => Write(Target, relative, content);

    public Snapshot Read(string[]? running = null, bool? journalOpen = null) => SnapshotReader.Read(
        Shared, _mapping, Device, new DevicePath { Source = Source, ProcessNames = ProcessNames },
        BackupRoot, new FakeProcesses(running ?? []), journalOpen ?? Journal.IsOpen);

    public PlanOutcome PlanApply(ApplyRequest? request = null) => Planner.Plan(Read(), request ?? new ApplyRequest());

    public PlanOutcome PlanRestore(string backupId, RestoreRequest? request = null) => RestorePlanner.Plan(
        Backups, Shared, backupId, request ?? new RestoreRequest(), ProcessNames, new FakeProcesses(), Journal.IsOpen);

    public Executor Executor(IFaultHook? faults = null) => new(Shared, Local, Backups, Device, faults: faults);

    public ExecutionResult Apply(ApplyRequest? request = null) => Executor().Run(Planned(PlanApply(request)));

    public ExecutionResult Revert() => Executor().Run(Planned(Planner.PlanRevert(Read())));

    public ExecutionResult Restore(string backupId, RestoreRequest? request = null) =>
        Executor().Run(Planned(PlanRestore(backupId, request)));

    /// <summary>The plan, or a test failure that says why there is none.</summary>
    public static Plan Planned(PlanOutcome outcome) =>
        outcome.Plan ?? throw new XunitException("Expected a plan, but was refused: " + outcome.Refusal?.Message);

    /// <summary>Every file below a folder, by relative path. Empty when the folder is not a real folder.</summary>
    public static Dictionary<string, string> Files(string root) =>
        SafeFs.Probe(root) != EntryKind.Directory
            ? []
            : SafeFs.EnumerateTree(root)
                .Where(e => e.Kind == EntryKind.File)
                .ToDictionary(e => e.RelativePath, e => File.ReadAllText(Path.Combine(root, e.RelativePath)));

    public static bool IsLink(string path) => SafeFs.Probe(path) is EntryKind.Symlink or EntryKind.Junction;

    public void Dispose() => Sandbox.Dispose();

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine([root, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class FakeProcesses(params string[] names) : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => names;
    }
}

/// <summary>Stands in for the process dying: not an I/O failure, so the executor does not handle it.</summary>
internal sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
        : base("simulated crash")
    {
    }

    public SimulatedCrashException(string message)
        : base(message)
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Crashes at one point of one step.</summary>
internal sealed class CrashAt(int index, FaultPoint point) : IFaultHook
{
    public void At(FaultPoint at, int stepIndex, PlanStep planStep)
    {
        if (stepIndex == index && at == point)
        {
            throw new SimulatedCrashException();
        }
    }
}

/// <summary>Makes one kind of step fail, as a disk error would.</summary>
internal sealed class FailAt(StepKind kind, FaultPoint point) : IFaultHook
{
    public void At(FaultPoint at, int stepIndex, PlanStep planStep)
    {
        if (planStep.Kind == kind && at == point)
        {
            throw new IOException("injected failure");
        }
    }
}
