using System.Diagnostics;
using System.Text.RegularExpressions;
using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Planning;

public class SnapshotReaderTests
{
    private const string MappingId = "ppsspp.savedata";

    [Fact]
    public void A_first_device_is_read_from_disk_and_planned()
    {
        using var world = new World();
        world.LocalSave("GAME1/save.bin", "one");
        world.LocalSave("GAME2/save.bin", "two!");

        var snapshot = world.Read();

        Assert.Equal(EntryKind.Directory, snapshot.SourceKind);
        Assert.Equal(EntryKind.Missing, snapshot.TargetKind);
        Assert.True(snapshot.SyncRootInitialized);
        Assert.True(snapshot.SourceParentExists);
        Assert.Equal(7, snapshot.SourceTree.TotalBytes);
        Assert.Equal(["GAME1", "GAME2"], snapshot.SourceTree.TopLevelNames);
        Assert.Null(snapshot.BackupRootProblem);
        Assert.True(snapshot.FreeBytesAtBackupRoot > 0);
        Assert.True(snapshot.FreeBytesAtTarget > 0);
        Assert.True(snapshot.TargetOnLocalVolume);
        Assert.Equal(Path.Combine(world.Store.DataRoot, "ppsspp", "SAVEDATA"), snapshot.Target);

        var outcome = Planner.Plan(snapshot, new ApplyRequest(SetupChoice.Copy));
        Assert.Equal(Classification.FirstDevice, outcome.Plan!.Classification);
    }

    [Fact]
    public void Only_files_that_could_be_identical_are_hashed()
    {
        using var world = new World();
        world.LocalSave("SAME/save.bin", "identical");
        world.SharedSave("SAME/save.bin", "identical");
        world.LocalSave("EDITED/save.bin", "version-A");
        world.SharedSave("EDITED/save.bin", "version-B");
        world.LocalSave("GREW/save.bin", "short");
        world.SharedSave("GREW/save.bin", "much longer");
        world.LocalSave("MINE/save.bin", "mine");

        var snapshot = world.Read();

        string? Hash(TreeFacts tree, string path) => tree.Entries.Single(e => e.RelativePath == path).Sha256;
        Assert.NotNull(Hash(snapshot.SourceTree, "SAME/save.bin"));
        Assert.Equal(Hash(snapshot.SourceTree, "SAME/save.bin"), Hash(snapshot.TargetTree, "SAME/save.bin"));
        Assert.NotEqual(Hash(snapshot.SourceTree, "EDITED/save.bin"), Hash(snapshot.TargetTree, "EDITED/save.bin"));
        Assert.Null(Hash(snapshot.SourceTree, "GREW/save.bin"));
        Assert.Null(Hash(snapshot.SourceTree, "MINE/save.bin"));

        var refusal = Planner.Plan(snapshot, new ApplyRequest()).Refusal!;
        Assert.Equal(Classification.Guard, refusal.Classification);
        Assert.Equal(
            [("EDITED", EntryPresence.Different), ("GREW", EntryPresence.Different), ("MINE", EntryPresence.OnlyLocal), ("SAME", EntryPresence.Identical)],
            refusal.Comparison!.Entries.Select(e => (e.Name, e.Presence)));
    }

    [Fact]
    public void An_existing_link_is_recognised_as_ours_or_foreign()
    {
        using var world = new World();
        world.SharedSave("GAME1/save.bin", "shared");
        var fs = new SafeFs();

        fs.CreateDirectoryLink(world.Source, world.Target);
        Assert.Equal(Classification.AlreadyApplied, Planner.Classify(world.Read()));

        fs.RemoveLink(world.Source);
        fs.CreateDirectoryLink(world.Source, world.Sandbox.Dir("old-script-folder"));
        var foreign = world.Read();
        Assert.Equal(Classification.ForeignLink, Planner.Classify(foreign));
        Assert.True(PathRules.AreSame(world.Sandbox.At("old-script-folder"), foreign.SourceLinkTarget!));
    }

    [Fact]
    public void Superseded_markers_are_ignored_and_missing_seed_files_are_listed()
    {
        using var world = new World();
        world.SharedSave("GAME1/save.bin", "arrived");
        world.Store.WriteSeedMarker(Marker("desktop", "GAME1/save.bin", "GAME2/save.bin", "GAME3/save.bin"));
        world.Store.WriteSeedMarker(Marker("laptop", "GAME9/save.bin"));
        world.Store.SupersedeOtherMarkers(MappingId, "desktop", DateTimeOffset.UnixEpoch);

        var snapshot = world.Read();

        Assert.Equal(["desktop"], snapshot.ActiveMarkers.Select(m => m.DeviceName));
        Assert.Equal(["GAME2/save.bin", "GAME3/save.bin"], snapshot.MissingSeedFiles);
    }

    [Fact]
    public void A_seeded_mapping_that_has_not_arrived_is_seen_as_such()
    {
        using var world = new World();
        world.LocalSave("GAME1/save.bin", "local");
        world.Store.WriteSeedMarker(Marker("desktop", "GAME1/save.bin"));

        Assert.Equal(Classification.NotArrived, Planner.Classify(world.Read()));
    }

    [Fact]
    public void Problems_on_disk_become_the_matching_precheck_failures()
    {
        using var world = new World();
        world.LocalSave("GAME1/save.bin", "local");

        PrecheckKind[] Failures(Snapshot snapshot) =>
            Planner.Plan(snapshot, new ApplyRequest(SetupChoice.Copy)).Refusal!.Failures.Select(f => f.Kind).ToArray();

        // A backup location inside the sync root.
        Assert.Equal(
            [PrecheckKind.BackupLocationUnusable],
            Failures(world.Read(backupRoot: Path.Combine(world.Store.SyncRoot, "backups"))));
        Assert.Equal([PrecheckKind.BackupLocationUnusable], Failures(world.Read(backupRoot: world.Sandbox.At("no-such-folder"))));

        // A mistyped save folder path.
        Assert.Equal(
            [PrecheckKind.SourceParentMissing],
            Failures(world.Read(source: world.Sandbox.At("apps", "ppssp-typo", "SAVEDATA"))));

        // A save folder that is really inside the sync root, reached through a link.
        var oldLayout = Path.Combine(world.Store.SyncRoot, "Retroarch", "saves");
        Directory.CreateDirectory(oldLayout);
        File.WriteAllText(Path.Combine(oldLayout, "game.srm"), "x");
        Sandbox.Link(world.Sandbox.At("alias"), Path.Combine(world.Store.SyncRoot, "Retroarch"));
        Assert.Equal(
            [PrecheckKind.SourceInsideSyncRoot],
            Failures(world.Read(source: Path.Combine(world.Sandbox.At("alias"), "saves"))));

        // The app is running.
        Assert.Equal([PrecheckKind.AppRunning], Failures(world.Read(running: ["PPSSPPSDL"])));

        // A journal entry is open.
        Assert.Equal([PrecheckKind.UnfinishedOperation], Failures(world.Read(journalOpen: true)));
    }

    [Fact]
    public void An_uninitialized_sync_root_is_reported_not_thrown()
    {
        using var sandbox = new Sandbox();
        var store = new SharedStore(sandbox.Dir("sync"));
        var mapping = new MappingFile { Schema = 1, Id = MappingId, Target = "ppsspp/SAVEDATA", Mechanism = Mechanism.Link };
        var path = new DevicePath { Source = sandbox.Dir("apps", "SAVEDATA") };

        var snapshot = SnapshotReader.Read(store, mapping, "deck", path, sandbox.Dir("backups"), new FakeProcesses(), journalOpen: false);

        Assert.False(snapshot.SyncRootInitialized);
        Assert.Empty(snapshot.ActiveMarkers);
        Assert.Equal(
            [PrecheckKind.SyncRootNotInitialized],
            Planner.Plan(snapshot, new ApplyRequest()).Refusal!.Failures.Select(f => f.Kind));
    }

    [Fact]
    public void A_relative_save_folder_path_is_refused()
    {
        using var world = new World();

        var error = Assert.Throws<StoreException>(() => world.Read(source: "relative/SAVEDATA"));
        Assert.Equal(StoreProblem.Invalid, error.Problem);
    }

    [Fact]
    public void Reading_changes_nothing_on_disk()
    {
        using var world = new World();
        world.LocalSave("GAME1/save.bin", "same");
        world.SharedSave("GAME1/save.bin", "same");
        world.Store.WriteSeedMarker(Marker("desktop", "GAME1/save.bin"));
        Sandbox.Link(world.Sandbox.At("a-link"), world.Sandbox.Canary);

        string[] Listing() => SafeFs.EnumerateTree(world.Sandbox.Work)
            .Select(e => $"{e.RelativePath}|{e.Kind}|{e.Length}|{e.LastWriteTimeUtc.Ticks}")
            .ToArray();
        var before = Listing();

        world.Read();
        Planner.Plan(world.Read(), new ApplyRequest());

        Assert.Equal(before, Listing());
        world.Sandbox.AssertCanaryIntact();
    }

    private static SeedMarker Marker(string device, params string[] files) => new()
    {
        Schema = SeedMarker.CurrentSchema,
        MappingId = MappingId,
        DeviceName = device,
        SeededUtc = DateTimeOffset.UnixEpoch,
        Files = files.Select(f => new SeedFile(f, 1)).ToList(),
    };

    /// <summary>A sync root, an app's save folder and a backup folder, all real, in a sandbox.</summary>
    private sealed class World : IDisposable
    {
        private readonly MappingFile _mapping = new()
        {
            Schema = MappingFile.CurrentSchema, Id = MappingId, Target = "ppsspp/SAVEDATA", Mechanism = Mechanism.Link,
        };

        public World()
        {
            Store = new SharedStore(Sandbox.Dir("sync"));
            Store.CreateRoot();
            Store.WriteMapping(_mapping);
            Source = Path.Combine(Sandbox.Dir("apps", "ppsspp"), "SAVEDATA");
            Backups = Sandbox.Dir("backups");
        }

        public Sandbox Sandbox { get; } = new();

        public SharedStore Store { get; }

        public string Source { get; }

        public string Backups { get; }

        public string Target => Store.TargetPathOf(_mapping);

        public void LocalSave(string relative, string content) => Write(Source, relative, content);

        public void SharedSave(string relative, string content) => Write(Target, relative, content);

        public Snapshot Read(string? source = null, string? backupRoot = null, string[]? running = null, bool journalOpen = false) =>
            SnapshotReader.Read(
                Store, _mapping, "deck",
                new DevicePath { Source = source ?? Source, ProcessNames = ["PPSSPPSDL", "PPSSPPQt"] },
                backupRoot ?? Backups, new FakeProcesses(running ?? []), journalOpen);

        public void Dispose() => Sandbox.Dispose();

        private static void Write(string root, string relative, string content)
        {
            var path = Path.Combine([root, .. relative.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    private sealed class FakeProcesses(params string[] names) : IProcessLister
    {
        public IReadOnlyList<string> RunningProcessNames() => names;
    }
}

public class ProcessCheckTests
{
    [Fact]
    public void Configured_names_match_running_processes()
    {
        string[] running = ["systemd", "PPSSPPSDL", "bash"];

        Assert.Equal(["PPSSPPSDL"], ProcessCheck.FindRunning(["PPSSPPSDL", "PPSSPPQt"], running));
        Assert.Empty(ProcessCheck.FindRunning(["retroarch"], running));
        Assert.Empty(ProcessCheck.FindRunning([], running));
        Assert.Empty(ProcessCheck.FindRunning(["", "  "], running));
        Assert.Empty(ProcessCheck.FindRunning(["PPSSPP"], running));
    }

    [Fact]
    public void The_exe_ending_is_ignored_on_either_side()
    {
        Assert.Equal(["PPSSPPWindows64.exe"], ProcessCheck.FindRunning(["PPSSPPWindows64.exe"], ["PPSSPPWindows64"]));
        Assert.Equal(["eden"], ProcessCheck.FindRunning(["eden"], ["eden.exe"]));
    }

    [Fact]
    public void Case_is_ignored_only_on_windows()
    {
        var found = ProcessCheck.FindRunning(["RetroArch"], ["retroarch"]);

        Assert.Equal(OperatingSystem.IsWindows(), found.Count == 1);
    }

    [Fact]
    public void A_name_linux_truncated_to_15_characters_still_matches()
    {
        // /proc/<pid>/comm holds at most 15 characters.
        Assert.Equal(["org.ppsspp.PPSSPP-wrapper"], ProcessCheck.FindRunning(["org.ppsspp.PPSSPP-wrapper"], ["org.ppsspp.PPSS"]));
        Assert.Empty(ProcessCheck.FindRunning(["org.ppsspp.PPSSPP-wrapper"], ["org.ppsspp"]));
    }

    [Fact]
    public void The_system_lister_sees_this_test_process()
    {
        var names = new SystemProcessLister().RunningProcessNames();

        using var current = Process.GetCurrentProcess();
        Assert.NotEmpty(names);
        Assert.NotEmpty(ProcessCheck.FindRunning([current.ProcessName], names));
    }
}

/// <summary>Keeps the planner honest: deciding must never change the disk.</summary>
public partial class PlanningPurityTests
{
    [GeneratedRegex(
        @"new\s+SafeFs\s*\(|\.(DeleteTree|RemoveLink|CreateDirectoryLink|RenameDirectory|WriteFileAtomically)\s*\(" +
        @"|\bSafeFs\.CopyTree\b|\bFile\.(Write|Append|Create|Copy|SetAttributes|SetLastWriteTime)\w*\s*\(" +
        @"|\bDirectory\.CreateDirectory\b|\.(WriteMapping|WriteDevice|WriteSeedMarker|SupersedeOtherMarkers|WriteState|WriteBootstrap|CreateRoot)\s*\(" +
        @"|\.Create\s*\(\s*\w+\s*,\s*\w+\s*,\s*BackupReason")]
    private static partial Regex WritingCall();

    [Fact]
    public void Nothing_in_the_planning_folder_writes_to_disk()
    {
        var folder = Path.Combine(FindRepositoryRoot(), "src", "SaveTether.Core", "Planning");
        var files = Directory.GetFiles(folder, "*.cs");
        Assert.True(files.Length >= 6);

        var offences = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, i) => (file, line, number: i + 1)))
            .Where(x => !x.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && WritingCall().IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.number}: {x.line.Trim()}")
            .ToList();

        Assert.True(offences.Count == 0, "Planning code must only read. Found:\n" + string.Join("\n", offences));
    }

    [Theory]
    [InlineData("new SafeFs(root).DeleteTree(path);")]
    [InlineData("fs.RenameDirectory(a, b);")]
    [InlineData("File.WriteAllText(path, text);")]
    [InlineData("Directory.CreateDirectory(path);")]
    [InlineData("shared.WriteSeedMarker(marker);")]
    [InlineData("store.Create(id, source, BackupReason.PreApply);")]
    public void The_purity_pattern_catches_writes(string line)
    {
        Assert.Matches(WritingCall(), line);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SaveTether.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find SaveTether.slnx above the test folder.");
    }
}
