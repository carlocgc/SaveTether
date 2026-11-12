using SaveTether.Core.FileSystem;
using SaveTether.Core.Model;
using SaveTether.Core.Planning;

namespace SaveTether.Tests.Planning;

/// <summary>Builds snapshots by hand, so planner tests need no disk.</summary>
internal static class Snap
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\" : "/";

    public static readonly DateTime Older = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Newer = new(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc);

    public static string SyncRoot => P("sync");

    public static string DataRoot => P("sync", "savetether");

    public static string Source => P("apps", "ppsspp", "SAVEDATA");

    public static string Target => P("sync", "savetether", "ppsspp", "SAVEDATA");

    public static string P(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>A healthy starting point: S is a real, empty folder and T does not exist yet.</summary>
    public static Snapshot Base() => new()
    {
        MappingId = "ppsspp.savedata",
        DeviceName = "deck",
        Source = Source,
        Target = Target,
        ResolvedSource = Source,
        ResolvedTarget = Target,
        SyncRoot = SyncRoot,
        DataRoot = DataRoot,
        SourceKind = EntryKind.Directory,
        TargetKind = EntryKind.Missing,
        BackupRoot = P("backups"),
    };

    /// <summary>S holds saves; T is as given.</summary>
    public static Snapshot WithLocalSaves(TreeFacts? shared = null) => Base() with
    {
        SourceTree = Tree(("GAME1/save.bin", 100, "aaa"), ("GAME2/save.bin", 200, "bbb")),
        TargetKind = shared is null ? EntryKind.Missing : EntryKind.Directory,
        TargetTree = shared ?? TreeFacts.Empty,
    };

    /// <summary>S and T both hold saves: the guard.</summary>
    public static Snapshot Guarded() => WithLocalSaves(Tree(("GAME1/save.bin", 100, "zzz"), ("GAME3/save.bin", 300, "ccc")));

    public static Snapshot LinkedTo(string linkTarget, EntryKind targetKind = EntryKind.Directory) => Base() with
    {
        SourceKind = OperatingSystem.IsWindows() ? EntryKind.Junction : EntryKind.Symlink,
        SourceLinkTarget = linkTarget,
        TargetKind = targetKind,
    };

    public static TreeFacts Tree(params (string Path, long Size, string? Hash)[] files)
    {
        var entries = new List<FileFact>();
        foreach (var (path, size, hash) in files)
        {
            // Add the folders leading to each file, as a real listing would have them.
            var parts = path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var folder = string.Join('/', parts[..i]);
                if (!entries.Any(e => e.RelativePath == folder))
                {
                    entries.Add(new FileFact(folder, EntryKind.Directory, 0, default));
                }
            }

            entries.Add(new FileFact(path, EntryKind.File, size, Older, hash));
        }

        return new TreeFacts(entries);
    }

    public static TreeFacts FoldersOnly(params string[] folders) =>
        new(folders.Select(f => new FileFact(f, EntryKind.Directory, 0, default)).ToList());

    public static SeedMarker Marker(string device, params string[] files) => new()
    {
        Schema = SeedMarker.CurrentSchema,
        MappingId = "ppsspp.savedata",
        DeviceName = device,
        SeededUtc = new DateTimeOffset(Older),
        Files = files.Select(f => new SeedFile(f, 1)).ToList(),
    };

    public static StepKind[] Kinds(this PlanOutcome outcome) => outcome.Plan!.Steps.Select(s => s.Kind).ToArray();
}
